using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Optional diagnostic logger that writes raw stream-json lines to a daily log file.
/// </summary>
public static class DiagnosticLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeWin", "logs");

    private static string LogPath => Path.Combine(LogDir,
        $"stream-{DateTime.Now:yyyy-MM-dd}.log");

    private static bool _enabled;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value) try { Directory.CreateDirectory(LogDir); } catch { }
        }
    }

    public static void Log(string category, string message)
    {
        if (!_enabled) return;
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {message}\n"); }
        catch { }
    }
}

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

    public string ClaudeExePath { get; set; } = "claude";
    public string? WorkingDirectory { get; set; }
    public string? ModelOverride { get; set; }

    public ClaudeCliService()
    {
        _parser.OnTextDelta += text => OnTextDelta?.Invoke(text);
        _parser.OnTextBlockStart += () => OnTextBlockStart?.Invoke();
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
            var startInfo = new ProcessStartInfo
            {
                FileName = ClaudeExePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

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

    private string BuildArguments()
    {
        var sb = new StringBuilder("-p --output-format stream-json --input-format stream-json --verbose --include-partial-messages --dangerously-skip-permissions --permission-prompt-tool stdio");

        if (!string.IsNullOrEmpty(ModelOverride))
            sb.Append($" --model \"{ModelOverride}\"");

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
                    break; // EOF

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

    private void HandleProcessExited()
    {
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
