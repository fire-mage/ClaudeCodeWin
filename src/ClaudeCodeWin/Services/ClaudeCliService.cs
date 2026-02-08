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
    private CancellationTokenSource? _cts;
    private string? _sessionId;
    private string? _currentToolName;
    private readonly StringBuilder _toolInputBuffer = new();
    private readonly ConcurrentDictionary<string, string?> _fileSnapshots = new();

    public string? SessionId => _sessionId;
    public bool IsProcessing => _process is not null && !_process.HasExited;

    public event Action<string>? OnTextDelta;
    public event Action<string, string>? OnToolUseStarted; // toolName, input
    public event Action<string, string>? OnToolUseCompleted; // toolName, output
    public event Action<ResultData>? OnCompleted;
    public event Action<string>? OnError;
    public event Action<string>? OnAskUserQuestion; // raw JSON input of AskUserQuestion tool
    public event Action<string>? OnFileChanged; // filePath from Write/Edit/NotebookEdit tools

    public string ClaudeExePath { get; set; } = "claude";
    public string? WorkingDirectory { get; set; }

    public async Task SendMessageAsync(string prompt, List<FileAttachment>? attachments = null)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var args = BuildArguments();
        var fullPrompt = BuildPrompt(prompt, attachments);

        var startInfo = new ProcessStartInfo
        {
            FileName = ClaudeExePath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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

            await _process.StandardInput.WriteLineAsync(fullPrompt).ConfigureAwait(false);
            _process.StandardInput.Close();

            // Read stderr concurrently to avoid pipe buffer deadlock.
            // Without this, if the process fills the 4 KB stderr buffer,
            // it blocks — which also blocks stdout, hanging the app.
            var stderrTask = _process.StandardError.ReadToEndAsync(ct);

            await ReadStreamAsync(_process.StandardOutput, ct).ConfigureAwait(false);

            var stderr = await stderrTask.ConfigureAwait(false);

            await _process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (_process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                OnError?.Invoke(stderr);
        }
        catch (OperationCanceledException)
        {
            KillProcess();
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex.Message);
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
        KillProcess();
    }

    public void ResetSession()
    {
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

    private string BuildArguments()
    {
        var sb = new StringBuilder("-p --output-format stream-json --verbose");

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

    private async Task ReadStreamAsync(System.IO.StreamReader reader, CancellationToken ct)
    {
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            ProcessJsonLine(line);
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

                case "assistant":
                    HandleAssistantMessage(root);
                    break;

                case "content_block_start":
                    HandleContentBlockStart(root);
                    break;

                case "content_block_delta":
                    HandleContentBlockDelta(root);
                    break;

                case "content_block_stop":
                    HandleContentBlockStop();
                    break;

                case "message_start":
                    break;

                case "message_delta":
                    break;

                case "message_stop":
                    break;

                case "result":
                    HandleResult(root);
                    break;
            }
        }
        catch (JsonException)
        {
            // Not valid JSON — ignore
        }
    }

    private void HandleSystemMessage(JsonElement root)
    {
        if (root.TryGetProperty("session_id", out var sid))
            _sessionId = sid.GetString();
    }

    private void HandleAssistantMessage(JsonElement root)
    {
        if (root.TryGetProperty("message", out var msg)
            && msg.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var bt))
                    continue;

                var blockType = bt.GetString();

                if (blockType == "text" && block.TryGetProperty("text", out var text))
                {
                    OnTextDelta?.Invoke(text.GetString() ?? string.Empty);
                }
                else if (blockType == "tool_use")
                {
                    var toolName = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    OnToolUseStarted?.Invoke(toolName, "");
                    DetectFileChange(toolName, block);
                }
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

            _currentToolName = toolName;
            _toolInputBuffer.Clear();

            // Seed buffer from initial input if it's non-empty (some CLIs send full input here)
            if (block.TryGetProperty("input", out var inp)
                && inp.ValueKind == JsonValueKind.Object
                && inp.EnumerateObject().Any())
            {
                _toolInputBuffer.Append(inp.GetRawText());
            }

            var inputStr = block.TryGetProperty("input", out var inp2) ? inp2.ToString() : "";
            OnToolUseStarted?.Invoke(toolName, inputStr);
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

        _currentToolName = null;
        _toolInputBuffer.Clear();
    }

    /// <summary>
    /// Detect file changes from tool_use blocks in complete assistant events.
    /// Called for Write/Edit/NotebookEdit tools.
    /// </summary>
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

    /// <summary>
    /// Extract file_path from tool input, snapshot the file, and fire OnFileChanged.
    /// </summary>
    private void SnapshotAndNotify(JsonElement input)
    {
        string? filePath = null;
        if (input.TryGetProperty("file_path", out var fp))
            filePath = fp.GetString();
        else if (input.TryGetProperty("notebook_path", out var np))
            filePath = np.GetString();

        if (!string.IsNullOrEmpty(filePath))
        {
            // Snapshot file content (before modification if possible)
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

        // Extract model and contextWindow from modelUsage (more reliable than top-level "model")
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

    private void KillProcess()
    {
        try
        {
            if (_process is not null && !_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may have already exited
        }
    }
}
