using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class StreamJsonParser
{
    // BUG FIX: was leaking undisposed JsonDocument — use static ctor to dispose after cloning
    private static readonly JsonElement s_emptyObject;
    static StreamJsonParser()
    {
        using var doc = JsonDocument.Parse("{}");
        s_emptyObject = doc.RootElement.Clone();
    }

    private string? _sessionId;
    // Fix Issue #3: lock guards concurrent access between ReadStdoutLoop (ProcessLine)
    // and ResetSessionSync (Reset) which run on different threads
    private readonly object _parseLock = new();
    private string? _currentToolName;
    private string? _currentToolUseId;
    private readonly StringBuilder _toolInputBuffer = new();
    // Fix #6: prevent unbounded buffer growth if CLI sends extremely large tool inputs
    private const int MaxToolInputBufferSize = 10 * 1024 * 1024; // 10 MB

    // FIX: deferred event queue — events are collected inside _parseLock and fired after
    // releasing the lock, preventing potential deadlocks when event handlers acquire other locks
    private readonly List<Action> _deferredEvents = new();

    // Per-call usage from the last message_start/message_delta in the current turn.
    // These represent the actual context size for the final API call (not aggregated).
    private int _lastMsgInputTokens;
    private int _lastMsgCacheReadTokens;
    private int _lastMsgCacheCreationTokens;
    private int _lastMsgOutputTokens;

    public string? SessionId => _sessionId;

    // Events
    public event Action<string>? OnTextDelta;
    public event Action? OnTextBlockStart;
    public event Action<string>? OnThinkingDelta;
    public event Action<string, string, string>? OnToolUseStarted; // toolName, toolUseId, input
    public event Action<string, string, string>? OnToolResult; // toolName, toolUseId, content
    public event Action<ResultData>? OnCompleted;
    public event Action<string>? OnFileChanged; // filePath from Write/Edit/NotebookEdit tools
    public event Action<string, string, string, JsonElement>? OnControlRequest; // requestId, toolName, toolUseId, input
    public event Action<string, string, List<string>>? OnSessionStarted; // sessionId, model, tools
    public event Action<string>? OnSystemNotification; // human-readable system notification from CLI
    public event Action<string, string>? OnUnknownEvent; // type, rawJson
    public event Action<string, int, int, int>? OnMessageStarted; // model, inputTokens, cacheReadTokens, cacheCreationTokens

    // FIX: escape all JSON control characters (U+0000..U+001F) to produce valid JSON
    public static string EscapeJson(string s)
    {
        // Performance fix: fast-path — return original string if no escaping needed (zero-alloc)
        bool needsEscape = false;
        foreach (var ch in s)
        {
            if (ch == '\\' || ch == '"' || ch < ' ')
            {
                needsEscape = true;
                break;
            }
        }
        if (!needsEscape)
            return s;

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < ' ')
                    {
                        sb.Append("\\u");
                        sb.Append(((int)ch).ToString("X4"));
                    }
                    else
                        sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    public void ProcessLine(string jsonLine)
    {
        // FIX: collect events inside lock, fire them after releasing to prevent deadlocks
        // FIX CRITICAL #1: snapshot deferred events into a local list before releasing lock
        // to prevent race condition where another thread calls Clear() during iteration
        List<Action> toFire;
        lock (_parseLock)
        {
            _deferredEvents.Clear();
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
                    default:
                        var unknownType = type ?? "";
                        var rawLine = jsonLine;
                        _deferredEvents.Add(() => OnUnknownEvent?.Invoke(unknownType, rawLine));
                        break;
                }
            }
            catch (JsonException)
            {
                // Not valid JSON — ignore
            }
            toFire = _deferredEvents.Count > 0
                ? new List<Action>(_deferredEvents)
                : [];
        }

        // Fire all deferred events outside the lock (using snapshot)
        foreach (var evt in toFire)
            evt();
    }

    public void Reset()
    {
        lock (_parseLock)
        {
            _currentToolName = null;
            _currentToolUseId = null;
            _toolInputBuffer.Clear();
            _sessionId = null;
            ResetPerCallUsage();
        }
    }

    private void ResetPerCallUsage()
    {
        _lastMsgInputTokens = 0;
        _lastMsgCacheReadTokens = 0;
        _lastMsgCacheCreationTokens = 0;
        _lastMsgOutputTokens = 0;
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
                HandleMessageStart(evt);
                break;
            case "message_delta":
                HandleMessageDelta(evt);
                break;
            case "message_stop":
                break;
            default:
                DiagnosticLogger.Log("UNKNOWN_STREAM_EVENT", $"eventType={eventType}");
                break;
        }
    }

    private void HandleSystemMessage(JsonElement root)
    {
        if (root.TryGetProperty("session_id", out var sid))
            _sessionId = sid.GetString();

        // Check for human-readable notification text (compaction, warnings, etc.)
        if (root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String)
        {
            var message = msgProp.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(message))
                _deferredEvents.Add(() => OnSystemNotification?.Invoke(message));
        }

        if (root.TryGetProperty("subtype", out var subProp) && subProp.ValueKind == JsonValueKind.String)
        {
            var subtype = subProp.GetString() ?? "";
            if (subtype.Contains("compact", StringComparison.OrdinalIgnoreCase))
            {
                var subtypeMsg = $"[subtype: {subtype}]";
                _deferredEvents.Add(() => OnSystemNotification?.Invoke(subtypeMsg));
            }
        }

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
        {
            var sessionId = _sessionId;
            var modelStr = model ?? "";
            _deferredEvents.Add(() => OnSessionStarted?.Invoke(sessionId, modelStr, tools));
        }
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
            var rc = resultContent;
            _deferredEvents.Add(() => OnToolResult?.Invoke(toolName, toolUseId, rc));
        }
    }

    /// <summary>
    /// Extracts per-call input token usage from a message_start streaming event.
    /// Each API call in the agentic loop produces a message_start with the actual
    /// input tokens for THAT call (= current conversation size).
    /// We keep overwriting so _lastMsg* always reflects the most recent API call.
    /// </summary>
    private void HandleMessageStart(JsonElement evt)
    {
        // Structure: { "type": "message_start", "message": { "model": "...", "usage": { ... } } }
        if (!evt.TryGetProperty("message", out var msg))
            return;

        // Extract model name from message_start (available immediately when API call starts)
        var model = msg.TryGetProperty("model", out var modelProp) ? modelProp.GetString() ?? "" : "";

        if (msg.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it))
                _lastMsgInputTokens = it.GetInt32();
            if (usage.TryGetProperty("cache_read_input_tokens", out var cr))
                _lastMsgCacheReadTokens = cr.GetInt32();
            if (usage.TryGetProperty("cache_creation_input_tokens", out var cc))
                _lastMsgCacheCreationTokens = cc.GetInt32();

            // Reset output — will be filled by message_delta
            _lastMsgOutputTokens = 0;

            DiagnosticLogger.Log("MESSAGE_START_USAGE",
                $"model={model} input={_lastMsgInputTokens} cache_read={_lastMsgCacheReadTokens} cache_create={_lastMsgCacheCreationTokens}");
        }

        if (!string.IsNullOrEmpty(model) || _lastMsgInputTokens > 0)
        {
            // Capture values for deferred invocation
            var m = model;
            var inp = _lastMsgInputTokens;
            var cr2 = _lastMsgCacheReadTokens;
            var cc2 = _lastMsgCacheCreationTokens;
            _deferredEvents.Add(() => OnMessageStarted?.Invoke(m, inp, cr2, cc2));
        }
    }

    /// <summary>
    /// Extracts output token count from a message_delta streaming event.
    /// </summary>
    private void HandleMessageDelta(JsonElement evt)
    {
        // Structure: { "type": "message_delta", "usage": { "output_tokens": N } }
        if (!evt.TryGetProperty("usage", out var usage))
            return;

        if (usage.TryGetProperty("output_tokens", out var ot))
            _lastMsgOutputTokens = ot.GetInt32();
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
            _deferredEvents.Add(() => OnTextBlockStart?.Invoke());
            return;
        }

        if (blockType == "thinking")
            return; // Recognized; deltas handled in HandleContentBlockDelta

        if (blockType == "tool_use")
        {
            var toolName = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var toolUseId = block.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";

            _currentToolName = toolName;
            _currentToolUseId = toolUseId;
            _toolInputBuffer.Clear();

            // FIX: use GetRawText() consistently — ToString() strips quotes for strings,
            // giving inconsistent JSON to event consumers
            var inputStr = "";
            if (block.TryGetProperty("input", out var inp))
            {
                inputStr = inp.GetRawText();
                if (inp.ValueKind == JsonValueKind.Object && inp.EnumerateObject().Any())
                    _toolInputBuffer.Append(inputStr);
            }

            var capturedInput = inputStr;
            _deferredEvents.Add(() => OnToolUseStarted?.Invoke(toolName, toolUseId, capturedInput));
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
            var textStr = text.GetString() ?? string.Empty;
            _deferredEvents.Add(() => OnTextDelta?.Invoke(textStr));
        }
        else if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out var thinking))
        {
            var thinkingStr = thinking.GetString() ?? string.Empty;
            _deferredEvents.Add(() => OnThinkingDelta?.Invoke(thinkingStr));
        }
        else if (deltaType == "input_json_delta" && delta.TryGetProperty("partial_json", out var pj))
        {
            // Fix #6: skip appending if buffer exceeds safety limit
            if (_toolInputBuffer.Length < MaxToolInputBufferSize)
                _toolInputBuffer.Append(pj.GetString() ?? "");
            else
                // FIX: log truncation so silent data loss is observable in diagnostics
                DiagnosticLogger.Log("TOOL_INPUT_TRUNCATED",
                    $"tool={_currentToolName} buffer={_toolInputBuffer.Length} limit={MaxToolInputBufferSize}");
        }
        // signature_delta is intentionally ignored — internal verification, no user content
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
            var toolName = _currentToolName;
            var toolUseId = _currentToolUseId ?? "";
            var input = _toolInputBuffer.ToString();
            _deferredEvents.Add(() => OnToolUseStarted?.Invoke(toolName, toolUseId, input));
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
        {
            var path = filePath;
            _deferredEvents.Add(() => OnFileChanged?.Invoke(path));
        }
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
        // FIX: use empty JSON object instead of default(JsonElement) to avoid Undefined ValueKind
        var input = request.TryGetProperty("input", out var inp)
            ? inp.Clone()
            : s_emptyObject;

        _deferredEvents.Add(() => OnControlRequest?.Invoke(requestId, toolName, toolUseId, input));
    }

    private void HandleResult(JsonElement root)
    {
        // Log full result JSON for Ctx% diagnostics
        DiagnosticLogger.Log("RESULT_RAW", root.GetRawText());

        string? sessionId = null;
        string? model = null;
        int inputTokens = 0, outputTokens = 0, cacheRead = 0, cacheCreation = 0;
        int contextWindow = 0;

        if (root.TryGetProperty("session_id", out var sid))
            sessionId = sid.GetString();

        // Extract model and contextWindow from modelUsage
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
            DiagnosticLogger.Log("RESULT_MODEL_USAGE", mu.GetRawText());
        }
        else
        {
            DiagnosticLogger.Log("RESULT_MODEL_USAGE", "MISSING");
        }

        // Extract token usage
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

            DiagnosticLogger.Log("RESULT_USAGE",
                $"input={inputTokens} output={outputTokens} cache_read={cacheRead} cache_create={cacheCreation}");
        }
        else
        {
            DiagnosticLogger.Log("RESULT_USAGE", "MISSING");
        }

        // Try alternative token locations (in case CLI puts them elsewhere)
        if (inputTokens == 0 && root.TryGetProperty("input_tokens", out var altIt))
            inputTokens = altIt.GetInt32();
        if (outputTokens == 0 && root.TryGetProperty("output_tokens", out var altOt))
            outputTokens = altOt.GetInt32();

        if (sessionId is not null)
            _sessionId = sessionId;

        DiagnosticLogger.Log("RESULT_FINAL",
            $"model={model} input={inputTokens} output={outputTokens} window={contextWindow} session={sessionId}");
        DiagnosticLogger.Log("RESULT_PER_CALL",
            $"lastCall: input={_lastMsgInputTokens} cache_read={_lastMsgCacheReadTokens} " +
            $"cache_create={_lastMsgCacheCreationTokens} output={_lastMsgOutputTokens}");

        var resultData = new ResultData(
            sessionId, model, inputTokens, outputTokens, cacheRead, cacheCreation, contextWindow,
            _lastMsgInputTokens, _lastMsgCacheReadTokens, _lastMsgCacheCreationTokens, _lastMsgOutputTokens);

        _deferredEvents.Add(() => OnCompleted?.Invoke(resultData));

        // Reset per-call counters for the next turn
        ResetPerCallUsage();
    }
}
