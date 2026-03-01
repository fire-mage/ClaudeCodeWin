using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
    private bool _isSessionActive;
    private string _lastStderr = string.Empty;
    private readonly StreamJsonParser _parser = new();

    // Stream stall detection
    private DateTime _lastStdoutActivity = DateTime.UtcNow;
    private bool _streamStalled;
    private System.Timers.Timer? _stallTimer;
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
            if (_process is not null && !_process.HasExited)
                return; // Already running

            _readCts = new CancellationTokenSource();

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
                _process = Process.Start(startInfo);
                if (_process is null)
                {
                    OnError?.Invoke("Failed to start claude process");
                    return;
                }

                _isSessionActive = true;
                _lastStdoutActivity = DateTime.UtcNow;
                _streamStalled = false;
                StartStallTimer();
                DiagnosticLogger.Log("PROCESS_START", $"pid={_process.Id} exe={startInfo.FileName} args={startInfo.Arguments}");

                // Start background reading loops
                var ct = _readCts.Token;
                _ = Task.Run(() => ReadStdoutLoop(ct), ct);
                _ = Task.Run(() => ReadStderrLoop(ct), ct);

                // Monitor process exit
                _process.EnableRaisingEvents = true;
                _process.Exited += (_, _) => HandleProcessExited();
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

    public void Cancel()
    {
        StopSession();
    }

    public void ResetSession()
    {
        StopSession();
        _sessionId = null;
        _parser.Reset();
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
    public void StopSession()
    {
        StopStallTimer();
        lock (_processLock)
        {
            _isSessionActive = false;
            _readCts?.Cancel();

            try
            {
                if (_process is not null && !_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { }

            _process?.Dispose();
            _process = null;
            _readCts?.Dispose();
            _readCts = null;
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

        try
        {
            var reader = proc.StandardError;
            var sb = new StringBuilder();
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
            _lastStderr = sb.ToString();
        }
        catch (OperationCanceledException) { }
        catch { }
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
        if (!_isSessionActive) return;

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

    private void HandleProcessExited()
    {
        DiagnosticLogger.Log("PROCESS_EXIT", $"expected={!_isSessionActive}");
        if (!_isSessionActive) return; // Expected stop

        // Unexpected process death — include stderr if available
        var errorMsg = "Claude process exited unexpectedly";
        if (!string.IsNullOrWhiteSpace(_lastStderr))
        {
            errorMsg += $"\n{_lastStderr.Trim()}";

            // Detect rate limit from stderr keywords
            var stderrLower = _lastStderr.ToLowerInvariant();
            if (stderrLower.Contains("rate limit") || stderrLower.Contains("rate_limit")
                || stderrLower.Contains("overloaded") || stderrLower.Contains("too many requests")
                || stderrLower.Contains("429"))
            {
                OnRateLimitDetected?.Invoke();
            }
        }

        OnError?.Invoke(errorMsg);
        lock (_processLock)
        {
            _process?.Dispose();
            _process = null;
        }
    }
}
