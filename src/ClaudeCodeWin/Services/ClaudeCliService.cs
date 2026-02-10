using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class ClaudeCliService
{
    private Process? _process;
    private CancellationTokenSource? _readCts;
    private string? _sessionId;
    private string? _currentToolName;
    private string? _currentToolUseId;
    private readonly StringBuilder _toolInputBuffer = new();
    private readonly ConcurrentDictionary<string, string?> _fileSnapshots = new();
    private readonly object _processLock = new();
    private bool _isSessionActive;
    private string _lastStderr = string.Empty;

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
    public event Action<string, string, string>? OnToolUseStarted; // toolName, toolUseId, input
    public event Action<string, string, string>? OnToolResult; // toolName, toolUseId, content
    public event Action<ResultData>? OnCompleted;
    public event Action<string>? OnError;
    public event Action<string>? OnAskUserQuestion; // raw JSON input of AskUserQuestion tool
    public event Action? OnExitPlanMode; // ExitPlanMode tool requires user confirmation
    public event Action<string>? OnFileChanged; // filePath from Write/Edit/NotebookEdit tools
    public event Action<string, string, List<string>>? OnSessionStarted; // sessionId, model, tools

    public string ClaudeExePath { get; set; } = "claude";
    public string? WorkingDirectory { get; set; }

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

    public void Cancel()
    {
        StopSession();
    }

    public void ResetSession()
    {
        StopSession();
        _sessionId = null;
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
        var sb = new StringBuilder("-p --output-format stream-json --input-format stream-json --verbose --include-partial-messages --dangerously-skip-permissions");

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

                ProcessJsonLine(line);
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
                    sb.AppendLine(line);
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
            errorMsg += $"\n{_lastStderr.Trim()}";

        OnError?.Invoke(errorMsg);
        lock (_processLock)
        {
            _process?.Dispose();
            _process = null;
        }
    }

    private void ProcessJsonLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return;

            var type = typeProp.GetString();

            switch (type)
            {
                case "system":
                    HandleSystemMessage(root);
                    break;

                case "stream_event":
                    HandleStreamEvent(root);
                    break;

                case "assistant":
                    HandleAssistantMessage(root);
                    break;

                case "user":
                    HandleUserMessage(root);
                    break;

                case "result":
                    HandleResult(root);
                    break;

                // Legacy top-level events (fallback in case CLI sends them)
                case "content_block_start":
                    HandleContentBlockStart(root);
                    break;
                case "content_block_delta":
                    HandleContentBlockDelta(root);
                    break;
                case "content_block_stop":
                    HandleContentBlockStop();
                    break;
            }
        }
        catch (JsonException)
        {
            // Not valid JSON — ignore
        }
    }

    /// <summary>
    /// Unwrap stream_event wrapper and dispatch inner event.
    /// </summary>
    private void HandleStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var evt))
            return;

        if (!evt.TryGetProperty("type", out var evtType))
            return;

        var eventType = evtType.GetString();

        switch (eventType)
        {
            case "content_block_start":
                HandleContentBlockStart(evt);
                break;
            case "content_block_delta":
                HandleContentBlockDelta(evt);
                break;
            case "content_block_stop":
                HandleContentBlockStop();
                break;
            case "message_start":
            case "message_delta":
            case "message_stop":
                // Not needed for current features
                break;
        }
    }

    private void HandleSystemMessage(JsonElement root)
    {
        if (root.TryGetProperty("session_id", out var sid))
            _sessionId = sid.GetString();

        // Parse init event data
        string? model = null;
        var tools = new List<string>();

        if (root.TryGetProperty("model", out var m))
            model = m.GetString();

        if (root.TryGetProperty("tools", out var toolsArr) && toolsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in toolsArr.EnumerateArray())
            {
                if (tool.ValueKind == JsonValueKind.String)
                    tools.Add(tool.GetString()!);
                else if (tool.TryGetProperty("name", out var tn))
                    tools.Add(tn.GetString() ?? "");
            }
        }

        if (_sessionId is not null)
            OnSessionStarted?.Invoke(_sessionId, model ?? "", tools);
    }

    private void HandleAssistantMessage(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)
            || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt))
                continue;

            var blockType = bt.GetString();

            if (blockType == "tool_use")
            {
                var toolName = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                DetectFileChange(toolName, block);

                // Try to extract complete input for summary
                if (block.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.Object)
                {
                    var toolUseId = block.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    // Don't re-fire OnToolUseStarted — it was already fired from content_block_start
                }
            }
        }
    }

    /// <summary>
    /// Handle tool_result events from the CLI (type:"user" messages).
    /// </summary>
    private void HandleUserMessage(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg))
            return;

        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt))
                continue;

            var blockType = bt.GetString();

            if (blockType == "tool_result")
            {
                var toolUseId = block.TryGetProperty("tool_use_id", out var tid) ? tid.GetString() ?? "" : "";
                var resultContent = "";

                if (block.TryGetProperty("content", out var c))
                {
                    if (c.ValueKind == JsonValueKind.String)
                    {
                        resultContent = c.GetString() ?? "";
                    }
                    else if (c.ValueKind == JsonValueKind.Array)
                    {
                        // Array of content blocks
                        var sb = new StringBuilder();
                        foreach (var part in c.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var t))
                                sb.Append(t.GetString());
                        }
                        resultContent = sb.ToString();
                    }
                }

                // Extract structured data (e.g., file info from Read tool)
                string? filePath = null;
                if (block.TryGetProperty("tool_use_result", out var tur))
                {
                    if (tur.TryGetProperty("file", out var file))
                    {
                        if (file.TryGetProperty("filePath", out var fp))
                            filePath = fp.GetString();
                        if (file.TryGetProperty("content", out var fc))
                            resultContent = fc.GetString() ?? resultContent;
                    }
                }

                // Find tool name by toolUseId — use the last started tool as fallback
                var toolName = _currentToolName ?? "Unknown";
                OnToolResult?.Invoke(toolName, toolUseId, resultContent);
            }
        }
    }

    private void HandleContentBlockStart(JsonElement root)
    {
        if (!root.TryGetProperty("content_block", out var block))
            return;

        if (block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_use")
        {
            var toolName = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var toolUseId = block.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";

            _currentToolName = toolName;
            _currentToolUseId = toolUseId;
            _toolInputBuffer.Clear();

            // Seed buffer from initial input if it's non-empty
            if (block.TryGetProperty("input", out var inp)
                && inp.ValueKind == JsonValueKind.Object
                && inp.EnumerateObject().Any())
            {
                _toolInputBuffer.Append(inp.GetRawText());
            }

            var inputStr = block.TryGetProperty("input", out var inp2) ? inp2.ToString() : "";
            OnToolUseStarted?.Invoke(toolName, toolUseId, inputStr);
        }
    }

    private void HandleContentBlockDelta(JsonElement root)
    {
        if (!root.TryGetProperty("delta", out var delta))
            return;

        if (delta.TryGetProperty("type", out var dt))
        {
            var deltaType = dt.GetString();

            if (deltaType == "text_delta" && delta.TryGetProperty("text", out var text))
            {
                OnTextDelta?.Invoke(text.GetString() ?? string.Empty);
            }
            else if (deltaType == "input_json_delta" && delta.TryGetProperty("partial_json", out var pj))
            {
                _toolInputBuffer.Append(pj.GetString() ?? "");
            }
        }
    }

    private void HandleContentBlockStop()
    {
        if (_currentToolName == "AskUserQuestion" && _toolInputBuffer.Length > 0)
        {
            OnAskUserQuestion?.Invoke(_toolInputBuffer.ToString());
        }

        if (_currentToolName == "ExitPlanMode")
        {
            OnExitPlanMode?.Invoke();
        }

        // Detect file changes from Write/Edit/NotebookEdit tools
        if (_currentToolName is "Write" or "Edit" or "NotebookEdit" && _toolInputBuffer.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(_toolInputBuffer.ToString());
                SnapshotAndNotify(doc.RootElement);
            }
            catch (JsonException) { }
            catch (IOException) { }
        }

        // Finalize tool input — update the tool use with complete input
        if (_currentToolName is not null && _toolInputBuffer.Length > 0)
        {
            // The complete input is now available — fire a "tool input complete" via OnToolUseStarted
            // with the full input JSON so the ViewModel can parse the summary
            OnToolUseStarted?.Invoke(_currentToolName, _currentToolUseId ?? "", _toolInputBuffer.ToString());
        }

        _currentToolName = null;
        _currentToolUseId = null;
        _toolInputBuffer.Clear();
    }

    private void DetectFileChange(string toolName, JsonElement block)
    {
        if (toolName is not ("Write" or "Edit" or "NotebookEdit"))
            return;

        try
        {
            if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                SnapshotAndNotify(input);
        }
        catch (JsonException) { }
        catch (IOException) { }
    }

    private void SnapshotAndNotify(JsonElement input)
    {
        string? filePath = null;
        if (input.TryGetProperty("file_path", out var fp))
            filePath = fp.GetString();
        else if (input.TryGetProperty("notebook_path", out var np))
            filePath = np.GetString();

        if (!string.IsNullOrEmpty(filePath))
        {
            _fileSnapshots.TryAdd(filePath,
                File.Exists(filePath) ? File.ReadAllText(filePath) : null);

            OnFileChanged?.Invoke(filePath);
        }
    }

    private void HandleResult(JsonElement root)
    {
        string? sessionId = null;
        string? model = null;
        int inputTokens = 0, outputTokens = 0, cacheRead = 0, cacheCreation = 0;
        int contextWindow = 0;

        if (root.TryGetProperty("session_id", out var sid))
            sessionId = sid.GetString();

        if (root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in mu.EnumerateObject())
            {
                model = prop.Name;
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("contextWindow", out var cw))
                    contextWindow = cw.GetInt32();
                break;
            }
        }

        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it))
                inputTokens = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot))
                outputTokens = ot.GetInt32();
            if (usage.TryGetProperty("cache_read_input_tokens", out var cr))
                cacheRead = cr.GetInt32();
            if (usage.TryGetProperty("cache_creation_input_tokens", out var cc))
                cacheCreation = cc.GetInt32();
        }

        if (sessionId is not null)
            _sessionId = sessionId;

        OnCompleted?.Invoke(new ResultData(sessionId, model, inputTokens, outputTokens, cacheRead, cacheCreation, contextWindow));
    }
}
