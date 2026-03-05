using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class ClaudeCliService
{
    private Process? _process;
    private CancellationTokenSource? _readCts;
    private string? _sessionId;
    private readonly ConcurrentDictionary<string, string?> _fileSnapshots = new();
    private readonly object _processLock = new();
    // BUG FIX: volatile for cross-thread visibility (read loops check this on pool threads,
    // StopSession/HandleProcessExited set it on other threads)
    private volatile bool _isSessionActive;
    // FIX (CRITICAL #1): Separate flag to distinguish expected stop from unexpected exit.
    // Set inside the lock by StopSessionAsync so HandleProcessExitedAsync can tell the difference
    // even when both race for the lock simultaneously.
    private bool _expectedStop;
    // Cross-thread field: written by ReadStderrLoop (thread pool), read by HandleProcessExited.
    // Uses Volatile.Read/Write for visibility since WaitAsync timeout path has no barrier.
    private string _lastStderr = string.Empty;
    private Task? _stderrTask;
    // Fix WARNING #2: track stdout task so StopSessionAsync can await it, preventing
    // parser Reset() from racing with ProcessLine() in ReadStdoutLoop
    private Task? _stdoutTask;
    // Fix #1: track async cleanup so StartSession can wait for prior disposal to complete
    private Task? _cleanupTask;
    private readonly StreamJsonParser _parser = new();

    // Stream stall detection
    private DateTime _lastStdoutActivity = DateTime.UtcNow;
    private bool _streamStalled;
    // FIX: volatile — accessed from UI thread, ThreadPool (HandleProcessExited), and Timer thread
    private volatile System.Timers.Timer? _stallTimer;
    private const int StallCheckIntervalMs = 5_000;
    private const int StallThresholdSeconds = 30;

    public string? SessionId => _sessionId;
    public bool IsProcessRunning
    {
        get
        {
            lock (_processLock)
                return _process is not null && !_process.HasExited;
        }
    }

    // Events
    public event Action<string>? OnTextDelta;
    public event Action? OnTextBlockStart; // fires when a new text content_block starts
    public event Action<string>? OnThinkingDelta; // extended thinking text chunk
    public event Action<string, string, string>? OnToolUseStarted; // toolName, toolUseId, input
    public event Action<string, string, string>? OnToolResult; // toolName, toolUseId, content
    public event Action<ResultData>? OnCompleted;
    public event Action<string>? OnError;
    public event Action<string>? OnFileChanged; // filePath from Write/Edit/NotebookEdit tools
    public event Action<string, string, string, JsonElement>? OnControlRequest; // requestId, toolName, toolUseId, input
    public event Action<string, string, List<string>>? OnSessionStarted; // sessionId, model, tools
    public event Action? OnRateLimitDetected; // fired when CLI stderr indicates rate limiting
    public event Action<string>? OnCompactionDetected; // message about context compaction
    public event Action<string>? OnSystemNotification; // human-readable system message from CLI
    public event Action<string, int, int, int>? OnMessageStarted; // model, inputTokens, cacheReadTokens, cacheCreationTokens
    public event Action<int>? OnStreamStalled; // seconds since last activity
    public event Action? OnStreamResumed;

    public string ClaudeExePath { get; set; } = "claude";
    public string? WorkingDirectory { get; set; }
    public string? ModelOverride { get; set; }
    public string? SystemPrompt { get; set; }
    public bool AppendSystemPrompt { get; set; } = true;
    public bool DangerouslySkipPermissions { get; set; } = true;

    public ClaudeCliService()
    {
        _parser.OnTextDelta += text => OnTextDelta?.Invoke(text);
        _parser.OnTextBlockStart += () => OnTextBlockStart?.Invoke();
        _parser.OnThinkingDelta += text => OnThinkingDelta?.Invoke(text);
        _parser.OnToolUseStarted += (name, id, input) => OnToolUseStarted?.Invoke(name, id, input);
        _parser.OnToolResult += (name, id, content) => OnToolResult?.Invoke(name, id, content);
        _parser.OnControlRequest += (rid, name, id, input) => OnControlRequest?.Invoke(rid, name, id, input);

        _parser.OnSessionStarted += (sid, model, tools) =>
        {
            _sessionId = sid;
            OnSessionStarted?.Invoke(sid, model, tools);
        };

        _parser.OnMessageStarted += (model, input, cacheRead, cacheCreation) =>
            OnMessageStarted?.Invoke(model, input, cacheRead, cacheCreation);

        _parser.OnCompleted += result =>
        {
            if (result.SessionId is not null)
                _sessionId = result.SessionId;
            OnCompleted?.Invoke(result);
        };

        _parser.OnFileChanged += filePath =>
        {
            try
            {
                _fileSnapshots.TryAdd(filePath,
                    File.Exists(filePath) ? File.ReadAllText(filePath) : null);
            }
            catch (IOException) { }
            OnFileChanged?.Invoke(filePath);
        };

        _parser.OnSystemNotification += msg =>
        {
            OnSystemNotification?.Invoke(msg);
            if (msg.Contains("compact", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("summariz", StringComparison.OrdinalIgnoreCase))
            {
                OnCompactionDetected?.Invoke(msg);
            }
        };

        _parser.OnUnknownEvent += (type, raw) =>
            DiagnosticLogger.Log("UNKNOWN_EVENT", $"type={type} json={raw}");
    }

    /// <summary>
    /// Start a new persistent CLI process for the session.
    /// </summary>
    public void StartSession()
    {
        lock (_processLock)
        {
            // FIX: check _cleanupTask inside lock to prevent race with StopSession.
            // If cleanup is still running, log a warning. Cleanup is fire-and-forget
            // (only disposes old process/CTS handles) so it's safe to overlap with a new session.
            if (_cleanupTask is not null)
            {
                if (_cleanupTask.IsCompleted)
                    _cleanupTask = null;
                else
                    DiagnosticLogger.Log("START_SESSION", "Prior cleanup task still running — proceeding (fire-and-forget disposal only)");
            }

            if (_process is not null && !_process.HasExited)
                return; // Already running

            _readCts = new CancellationTokenSource();
            // Bug fix: clear stale stderr from previous session to prevent showing
            // old error messages if the new session exits before ReadStderrLoop writes
            Volatile.Write(ref _lastStderr, string.Empty);

            var args = BuildArguments();

            // .cmd/.bat files cannot be launched directly by CreateProcessW;
            // wrap them in cmd.exe /c so that redirected I/O still works.
            var exePath = ClaudeExePath;
            var isCmdFile = exePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                         || exePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

            // Try to bypass cmd.exe by extracting node.exe + script from npm shim.
            // This avoids cmd.exe's broken argument quoting for system prompts
            // containing " or other special characters.
            if (isCmdFile)
            {
                var nodeInfo = TryBypassCmdShim(exePath);
                if (nodeInfo != null)
                {
                    args = $"\"{nodeInfo.Value.ScriptPath}\" {args}";
                    exePath = nodeInfo.Value.NodeExe;
                    isCmdFile = false;
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = isCmdFile ? "cmd.exe" : exePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            // Handle system prompt: use env var for cmd.exe to avoid broken \" escaping
            if (!string.IsNullOrEmpty(SystemPrompt))
            {
                var flag = AppendSystemPrompt ? "--append-system-prompt" : "--system-prompt";
                var promptForCli = SystemPrompt.Replace("\r", "").Replace("\n", "\\n");

                if (isCmdFile)
                {
                    // cmd.exe expands %VAR% in /c command string
                    startInfo.Environment["CCW_SYSPROMPT"] = promptForCli;
                    args += $" {flag} \"%CCW_SYSPROMPT%\"";
                }
                else
                {
                    var escaped = promptForCli.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    args += $" {flag} \"{escaped}\"";
                }
            }

            startInfo.Arguments = isCmdFile ? $"/c \"\"{exePath}\" {args}\"" : args;

            if (!string.IsNullOrEmpty(WorkingDirectory))
                startInfo.WorkingDirectory = WorkingDirectory;

            startInfo.Environment["LANG"] = "en_US.UTF-8";

            // Add local GhCli to PATH (portable install)
            var ghCliBin = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeCodeWin", "GhCli", "bin");
            if (Directory.Exists(ghCliBin))
            {
                var currentPath = startInfo.Environment["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? "";
                startInfo.Environment["PATH"] = ghCliBin + ";" + currentPath;
            }

            try
            {
                // FIX (Issue 3): Set EnableRaisingEvents BEFORE Start() to avoid race where
                // process exits between Start() and EnableRaisingEvents=true, losing the Exited event.
                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                // FIX (Issue 3): HandleProcessExited returns Task; discard to avoid async void.
                // The method has its own top-level try/catch so UnobservedTaskException is safe.
                process.Exited += (_, _) => _ = HandleProcessExitedAsync();
                if (!process.Start())
                {
                    process.Dispose();
                    _readCts?.Dispose();
                    _readCts = null;
                    OnError?.Invoke("Failed to start claude process");
                    return;
                }
                _process = process;

                _isSessionActive = true;
                _expectedStop = false; // Reset for new session
                _lastStdoutActivity = DateTime.UtcNow;
                _streamStalled = false;
                StartStallTimer();
                DiagnosticLogger.Log("PROCESS_START", $"pid={_process.Id} exe={startInfo.FileName} args={startInfo.Arguments}");

                // Start background reading loops
                // FIX: Don't pass ct to Task.Run — if token is cancelled between CTS creation
                // and here, Task.Run would refuse to start, losing any stderr already written.
                // The loops check ct internally via ReadLineAsync(ct).
                var ct = _readCts.Token;
                // Fix WARNING #2: store stdout task so StopSessionAsync can await it
                _stdoutTask = Task.Run(() => ReadStdoutLoop(ct));
                // BUG FIX: store stderr task so HandleProcessExited can await completion
                // before reading _lastStderr (Exited event may fire before pipe is drained)
                _stderrTask = Task.Run(() => ReadStderrLoop(ct));
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Failed to start claude: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Send a user message to the running CLI process via stdin.
    /// Starts the process if not running.
    /// </summary>
    public void SendMessage(string text, List<FileAttachment>? attachments = null)
    {
        var fullText = BuildPrompt(text, attachments);

        lock (_processLock)
        {
            if (_process is null || _process.HasExited)
                StartSession();

            if (_process is null || _process.HasExited)
            {
                OnError?.Invoke("Claude process is not running");
                return;
            }

            try
            {
                var messageJson = JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new
                    {
                        role = "user",
                        content = fullText
                    }
                });

                DiagnosticLogger.Log("STDIN_MSG", $"len={messageJson.Length} text_len={fullText.Length}");
                _process.StandardInput.WriteLine(messageJson);
                _process.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Failed to send message: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Send a control_response to the CLI process (for permission prompts like ExitPlanMode, AskUserQuestion).
    /// </summary>
    public void SendControlResponse(string requestId, string behavior,
        string? updatedInputJson = null, string? toolUseId = null, string? errorMessage = null)
    {
        lock (_processLock)
        {
            if (_process is null || _process.HasExited) return;

            try
            {
                string json;
                if (behavior == "allow")
                {
                    var updatedInput = updatedInputJson ?? "{}";
                    var rid = StreamJsonParser.EscapeJson(requestId);
                    var tuid = StreamJsonParser.EscapeJson(toolUseId ?? "");

                    json = "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\","
                         + "\"request_id\":\"" + rid + "\","
                         + "\"response\":{\"behavior\":\"allow\","
                         + "\"updatedInput\":" + updatedInput + ","
                         + "\"toolUseID\":\"" + tuid + "\"}}}";
                }
                else
                {
                    var rid = StreamJsonParser.EscapeJson(requestId);
                    var err = StreamJsonParser.EscapeJson(errorMessage ?? "User denied");

                    json = "{\"type\":\"control_response\",\"response\":{\"subtype\":\"error\","
                         + "\"request_id\":\"" + rid + "\","
                         + "\"error\":\"" + err + "\"}}";
                }

                DiagnosticLogger.Log("STDIN_CTRL", $"behavior={behavior} requestId={requestId} len={json.Length}");
                _process.StandardInput.WriteLine(json);
                _process.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Failed to send control response: {ex.Message}");
            }
        }
    }

    /// Fire-and-forget stop for callers that don't need completion guarantees.
    // FIX: Log exceptions from fire-and-forget StopSessionAsync to avoid silent UnobservedTaskException
    public void StopSession()
    {
        StopSessionAsync().ContinueWith(t =>
            DiagnosticLogger.Log("STOP_SESSION_ERROR", t.Exception?.InnerException?.Message ?? "unknown"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Cancel()
    {
        StopSession();
    }

    // FIX: ResetSession returns Task so callers can await and avoid race conditions
    // where _sessionId is still set when a new session starts.
    public async Task ResetSessionAsync()
    {
        try
        {
            await StopSessionAsync();
            _sessionId = null;
            _parser.Reset();
        }
        catch (Exception ex) { DiagnosticLogger.Log("RESET_SESSION_ERROR", ex.Message); }
    }

    /// <summary>
    /// Synchronous reset — nulls _sessionId immediately to prevent BuildArguments from using
    /// a stale session ID, then stops the process. Parser reset is deferred until after
    /// the process is stopped to avoid ReadStdoutLoop feeding data to a reset parser.
    /// </summary>
    public void ResetSessionSync()
    {
        _sessionId = null;
        // FIX (CRITICAL #1): Don't reset parser here — the process may still be sending
        // stdout data. ReadStdoutLoop would parse with a clean parser, losing context or
        // causing errors. Instead, reset parser after process stop completes.
        // FIX: Don't use ExecuteSynchronously — if StopSessionAsync completes synchronously,
        // the callback would run on the calling thread (possibly UI), risking deadlocks.
        StopSessionAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                DiagnosticLogger.Log("RESET_STOP_ERROR", t.Exception?.InnerException?.Message ?? "unknown");
            try { _parser.Reset(); }
            catch (Exception ex) { DiagnosticLogger.Log("PARSER_RESET_ERROR", ex.Message); }
        }, TaskScheduler.Default);
    }

    public void RestoreSession(string sessionId)
    {
        _sessionId = sessionId;
    }

    public string? GetFileSnapshot(string filePath)
    {
        _fileSnapshots.TryGetValue(filePath, out var snapshot);
        return snapshot;
    }

    public void ClearFileSnapshots()
    {
        _fileSnapshots.Clear();
    }

    /// <summary>
    /// Stop the CLI process, cancel reading loops.
    /// </summary>
    // FIX (CRITICAL #1): Drain stderr BEFORE cancelling CTS to avoid losing stderr data.
    // Previously CTS was cancelled first (line _readCts?.Cancel()), which killed ReadStderrLoop
    // before stderr could be fully read. Now we capture stderrTask, kill the process (which closes
    // its stdout/stderr streams, causing reads to complete), drain stderr, THEN schedule cleanup.
    // FIX (Issue 1): Changed from async void to Task so callers can await completion.
    public async Task StopSessionAsync()
    {
        StopStallTimer();

        Task? stderrWait;
        Task? stdoutWait;
        CancellationTokenSource? ctsToDispose;
        Process? procToDispose;

        lock (_processLock)
        {
            // FIX (CRITICAL #1): Set _expectedStop inside the lock BEFORE nulling fields.
            // This way HandleProcessExitedAsync (if it wins the lock later) can distinguish
            // an expected stop from an unexpected crash and avoid silently swallowing errors.
            _expectedStop = true;
            _isSessionActive = false;

            stderrWait = _stderrTask;
            _stderrTask = null;
            // Fix WARNING #2: capture stdout task to await alongside stderr
            stdoutWait = _stdoutTask;
            _stdoutTask = null;
            // FIX (Issue 2): Plain assignment inside lock instead of redundant Interlocked.Exchange.
            // The lock already provides mutual exclusion; Interlocked is misleading here.
            ctsToDispose = _readCts;
            _readCts = null;
            procToDispose = _process;
            _process = null;

            // Kill process first — this closes its streams, allowing ReadStderrLoop to complete naturally
            try
            {
                if (procToDispose is not null && !procToDispose.HasExited)
                    procToDispose.Kill(entireProcessTree: true);
            }
            catch { }
        }

        // Drain stderr and stdout OUTSIDE the lock and BEFORE cancelling CTS
        if (stderrWait is not null)
        {
            try { await stderrWait.WaitAsync(TimeSpan.FromMilliseconds(2000)); } catch { }
        }
        // Fix WARNING #2: await stdout task so ReadStdoutLoop has fully exited before
        // any caller (e.g. ResetSessionSync) calls _parser.Reset()
        if (stdoutWait is not null)
        {
            try { await stdoutWait.WaitAsync(TimeSpan.FromMilliseconds(2000)); } catch { }
        }

        // Now schedule disposal — CTS cancel is harmless since stderr is already drained
        lock (_processLock)
        {
            ScheduleResourceCleanup(ctsToDispose, procToDispose);
        }
    }

    /// <summary>
    /// Extracts the Node.js executable and script path from an npm-generated .cmd shim,
    /// allowing us to bypass cmd.exe entirely and avoid its argument quoting issues.
    /// Returns null if the .cmd file doesn't match the expected npm shim pattern.
    /// </summary>
    private static (string NodeExe, string ScriptPath)? TryBypassCmdShim(string cmdPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(cmdPath);
            if (dir == null) return null;

            var content = File.ReadAllText(cmdPath);

            // npm shims reference scripts like:
            //   New (npm v9+): "%dp0%\node_modules\...\cli.mjs"  (SET dp0=%~dp0, then %dp0%)
            //   Old:           "%~dp0\node_modules\...\cli.mjs"  (%~dp0 is a batch modifier, no closing %)
            var match = Regex.Match(content, @"""%(?:~dp0\\|dp0%\\)([^""]+\.m?js)""");
            if (!match.Success) return null;

            var scriptPath = Path.GetFullPath(Path.Combine(dir, match.Groups[1].Value));
            if (!File.Exists(scriptPath)) return null;

            // Prefer node.exe in same directory (portable install), fall back to PATH
            var localNode = Path.Combine(dir, "node.exe");
            var nodeExe = File.Exists(localNode) ? localNode : "node";

            return (nodeExe, scriptPath);
        }
        catch
        {
            return null;
        }
    }

    private string BuildArguments()
    {
        var sb = new StringBuilder("-p --output-format stream-json --input-format stream-json --verbose --include-partial-messages --permission-prompt-tool stdio");

        if (DangerouslySkipPermissions)
            sb.Append(" --dangerously-skip-permissions");

        if (!string.IsNullOrEmpty(ModelOverride))
            sb.Append($" --model \"{ModelOverride}\"");

        // SystemPrompt is handled in StartSession() to avoid cmd.exe escaping issues

        if (!string.IsNullOrEmpty(_sessionId))
            sb.Append($" --resume \"{_sessionId}\"");

        return sb.ToString();
    }

    private static string BuildPrompt(string prompt, List<FileAttachment>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
            return prompt;

        var sb = new StringBuilder(prompt);
        sb.AppendLine();
        sb.AppendLine();

        foreach (var att in attachments)
        {
            sb.AppendLine(att.IsScreenshot
                ? $"[Screenshot: {att.FilePath}]"
                : $"[File: {att.FilePath}]");
        }

        return sb.ToString();
    }

    private async Task ReadStdoutLoop(CancellationToken ct)
    {
        Process? proc;
        lock (_processLock)
            proc = _process;

        if (proc is null) return;

        try
        {
            var reader = proc.StandardOutput;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    DiagnosticLogger.Log("STDOUT_EOF", "CLI stdout stream ended");
                    break;
                }

                _lastStdoutActivity = DateTime.UtcNow;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                DiagnosticLogger.Log("STDOUT", line);
                _parser.ProcessLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isSessionActive)
                OnError?.Invoke($"Read error: {ex.Message}");
        }
    }

    private async Task ReadStderrLoop(CancellationToken ct)
    {
        Process? proc;
        lock (_processLock)
            proc = _process;

        if (proc is null) return;

        var sb = new StringBuilder();
        try
        {
            var reader = proc.StandardError;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line))
                {
                    DiagnosticLogger.Log("STDERR", line);
                    sb.AppendLine(line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            // Defense-in-depth: always save accumulated stderr, even if cancelled early.
            // FIX: use Volatile.Write for cross-thread visibility (paired with Volatile.Read
            // in HandleProcessExited)
            Volatile.Write(ref _lastStderr, sb.ToString());
        }
    }

    private void StartStallTimer()
    {
        StopStallTimer();
        _stallTimer = new System.Timers.Timer(StallCheckIntervalMs);
        _stallTimer.Elapsed += (_, _) => CheckForStall();
        _stallTimer.AutoReset = true;
        _stallTimer.Start();
    }

    private void StopStallTimer()
    {
        _stallTimer?.Stop();
        _stallTimer?.Dispose();
        _stallTimer = null;
        if (_streamStalled)
        {
            _streamStalled = false;
            OnStreamResumed?.Invoke();
        }
    }

    private void CheckForStall()
    {
        // FIX: guard against timer firing after disposal (race with StopStallTimer)
        if (!_isSessionActive || _stallTimer is null) return;

        var elapsed = (int)(DateTime.UtcNow - _lastStdoutActivity).TotalSeconds;

        if (elapsed >= StallThresholdSeconds && !_streamStalled)
        {
            _streamStalled = true;
            DiagnosticLogger.Log("STREAM_STALL", $"No stdout data for {elapsed}s");
            OnStreamStalled?.Invoke(elapsed);
        }
        else if (elapsed < StallThresholdSeconds && _streamStalled)
        {
            _streamStalled = false;
            DiagnosticLogger.Log("STREAM_RESUMED", "Stdout data flowing again");
            OnStreamResumed?.Invoke();
        }
        else if (_streamStalled)
        {
            // Update elapsed time while stalled
            OnStreamStalled?.Invoke(elapsed);
        }
    }

    // FIX (Issue 3): Changed from async void to async Task — async void unhandled exceptions
    // terminate the process. Caller discards the Task; top-level try/catch ensures no unobserved exceptions.
    private async Task HandleProcessExitedAsync()
    {
        try
        {
            try { StopStallTimer(); } catch { }

            Task? stderrWait;
            Task? stdoutWait;
            CancellationTokenSource? ctsToDispose;
            Process? procToDispose;
            lock (_processLock)
            {
                // FIX (CRITICAL #1): Use _expectedStop flag (set inside lock by StopSessionAsync)
                // instead of checking _isSessionActive. This prevents the race where StopSessionAsync
                // sets _isSessionActive=false before acquiring the lock, causing HandleProcessExitedAsync
                // to silently swallow an unexpected exit error.
                DiagnosticLogger.Log("PROCESS_EXIT", $"expected={_expectedStop}");
                if (_expectedStop)
                {
                    _expectedStop = false; // Reset for next session
                    return; // Expected stop (StopSession already cleaned up)
                }

                _isSessionActive = false;
                stderrWait = _stderrTask;
                stdoutWait = _stdoutTask;
                // FIX: Plain assignment inside lock (consistent with StopSessionAsync).
                // The lock already provides mutual exclusion; Interlocked is redundant here.
                ctsToDispose = _readCts;
                _readCts = null;
                procToDispose = _process;
                _process = null;
                _stderrTask = null;
                _stdoutTask = null;
            }

            // Drain stderr and stdout BEFORE cancelling CTS to avoid losing data.
            if (stderrWait is not null)
            {
                try { await stderrWait.WaitAsync(TimeSpan.FromMilliseconds(2000)); } catch { }
            }
            if (stdoutWait is not null)
            {
                try { await stdoutWait.WaitAsync(TimeSpan.FromMilliseconds(2000)); } catch { }
            }

            // Now schedule disposal — CTS cancel is harmless since stderr is already drained
            lock (_processLock)
            {
                ScheduleResourceCleanup(ctsToDispose, procToDispose);
            }

            // Fire OnError after stderr has been drained
            try
            {
                int? exitCode = null;
                try { exitCode = procToDispose?.ExitCode; } catch { }

                var errorMsg = exitCode.HasValue
                    ? $"Claude process exited unexpectedly (exit code {exitCode.Value})"
                    : "Claude process exited unexpectedly";

                DiagnosticLogger.Log("PROCESS_EXIT_CODE", $"exitCode={exitCode}");

                var stderr = Volatile.Read(ref _lastStderr);
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    errorMsg += $"\n{stderr.Trim()}";

                    var stderrLower = stderr.ToLowerInvariant();
                    if (stderrLower.Contains("rate limit") || stderrLower.Contains("rate_limit")
                        || stderrLower.Contains("overloaded") || stderrLower.Contains("too many requests")
                        || stderrLower.Contains("429"))
                    {
                        OnRateLimitDetected?.Invoke();
                    }
                }

                OnError?.Invoke(errorMsg);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("HANDLE_EXIT_ERROR", ex.Message);
                try { OnError?.Invoke("Claude process exited unexpectedly (error handling failed)"); } catch { }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("HANDLE_EXIT_FATAL", ex.Message);
        }
    }

    /// <summary>
    /// Shared cleanup: chains onto previous cleanup, disposes CTS and process.
    /// Must be called inside _processLock. Callers must drain stderr before calling.
    /// </summary>
    // FIX (WARNING #2): Removed stderrWait/stderrTimeout params — both callers drain stderr
    // themselves before calling. Having these params was a latent trap (TimeSpan.Zero would
    // cause immediate TimeoutException if a future caller passed non-null stderrWait).
    private void ScheduleResourceCleanup(CancellationTokenSource? cts, Process? proc)
    {
        // FIX: runtime guard — Debug.Assert is stripped in Release builds, but calling
        // without the lock silently corrupts the cleanup chain
        if (!Monitor.IsEntered(_processLock))
        {
            DiagnosticLogger.Log("CLEANUP_LOCK_VIOLATION", "ScheduleResourceCleanup called without _processLock");
            return;
        }

        // FIX (Issue 1): Skip if both params are null — avoids wasting a Task.Run allocation
        // when the second-to-arrive caller (StopSessionAsync vs HandleProcessExited race) finds
        // all fields already nulled out by the first caller.
        if (cts is null && proc is null) return;

        // FIX (CRITICAL #2): Guard against reentrancy — if a cleanup task is already running
        // and we have real resources to dispose, chain onto it. If it's already completed,
        // clear the reference to avoid indefinite chain growth.
        var previousCleanup = _cleanupTask;
        if (previousCleanup is { IsCompleted: true })
            previousCleanup = null;

        _cleanupTask = Task.Run(async () =>
        {
            try
            {
                if (previousCleanup is not null)
                {
                    try { await previousCleanup; } catch { }
                }
                cts?.Cancel();
                cts?.Dispose();
                proc?.Dispose();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("RESOURCE_CLEANUP_ERROR", ex.Message);
            }
        });
    }
}
