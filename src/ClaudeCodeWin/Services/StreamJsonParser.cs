using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class StreamJsonParser
{
    private string? _sessionId;
    private string? _currentToolName;
    private string? _currentToolUseId;
    private readonly StringBuilder _toolInputBuffer = new();

    public string? SessionId => _sessionId;

    // Events
    public event Action<string>? OnTextDelta;
    public event Action? OnTextBlockStart;
    public event Action<string, string, string>? OnToolUseStarted; // toolName, toolUseId, input
    public event Action<string, string, string>? OnToolResult; // toolName, toolUseId, content
    public event Action<ResultData>? OnCompleted;
    public event Action<string>? OnFileChanged; // filePath from Write/Edit/NotebookEdit tools
    public event Action<string, string, string, JsonElement>? OnControlRequest; // requestId, toolName, toolUseId, input
    public event Action<string, string, List<string>>? OnSessionStarted; // sessionId, model, tools

    public static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    public void ProcessLine(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
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
                case "control_request":
                    HandleControlRequest(root);
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

    public void Reset()
    {
        _currentToolName = null;
        _currentToolUseId = null;
        _toolInputBuffer.Clear();
        _sessionId = null;
    }

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
                break;
        }
    }

    private void HandleSystemMessage(JsonElement root)
    {
        if (root.TryGetProperty("session_id", out var sid))
            _sessionId = sid.GetString();

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

            if (bt.GetString() == "tool_use")
            {
                var toolName = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                DetectFileChange(toolName, block);
            }
        }
    }

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

            if (bt.GetString() != "tool_result")
                continue;

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
                    var sb = new StringBuilder();
                    foreach (var part in c.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var t))
                            sb.Append(t.GetString());
                    }
                    resultContent = sb.ToString();
                }
            }

            if (block.TryGetProperty("tool_use_result", out var tur))
            {
                if (tur.TryGetProperty("file", out var file))
                {
                    if (file.TryGetProperty("content", out var fc))
                        resultContent = fc.GetString() ?? resultContent;
                }
            }

            var toolName = _currentToolName ?? "Unknown";
            OnToolResult?.Invoke(toolName, toolUseId, resultContent);
        }
    }

    private void HandleContentBlockStart(JsonElement root)
    {
        if (!root.TryGetProperty("content_block", out var block))
            return;

        if (!block.TryGetProperty("type", out var bt))
            return;

        var blockType = bt.GetString();

        if (blockType == "text")
        {
            OnTextBlockStart?.Invoke();
            return;
        }

        if (blockType == "tool_use")
        {
            var toolName = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var toolUseId = block.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";

            _currentToolName = toolName;
            _currentToolUseId = toolUseId;
            _toolInputBuffer.Clear();

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

        if (!delta.TryGetProperty("type", out var dt))
            return;

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

    private void HandleContentBlockStop()
    {
        if (_currentToolName is "Write" or "Edit" or "NotebookEdit" && _toolInputBuffer.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(_toolInputBuffer.ToString());
                NotifyFileChanged(doc.RootElement);
            }
            catch (JsonException) { }
        }

        if (_currentToolName is not null && _toolInputBuffer.Length > 0)
        {
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
                NotifyFileChanged(input);
        }
        catch (JsonException) { }
    }

    private void NotifyFileChanged(JsonElement input)
    {
        string? filePath = null;
        if (input.TryGetProperty("file_path", out var fp))
            filePath = fp.GetString();
        else if (input.TryGetProperty("notebook_path", out var np))
            filePath = np.GetString();

        if (!string.IsNullOrEmpty(filePath))
            OnFileChanged?.Invoke(filePath);
    }

    private void HandleControlRequest(JsonElement root)
    {
        if (!root.TryGetProperty("request_id", out var ridProp)) return;
        var requestId = ridProp.GetString() ?? "";

        if (!root.TryGetProperty("request", out var request)) return;
        if (!request.TryGetProperty("subtype", out var subtype)
            || subtype.GetString() != "can_use_tool") return;

        var toolName = request.TryGetProperty("tool_name", out var tn)
            ? tn.GetString() ?? "" : "";
        var toolUseId = request.TryGetProperty("tool_use_id", out var tid)
            ? tid.GetString() ?? "" : "";
        var input = request.TryGetProperty("input", out var inp)
            ? inp.Clone() : default;

        OnControlRequest?.Invoke(requestId, toolName, toolUseId, input);
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
