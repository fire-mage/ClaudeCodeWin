using System.Text.Json;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.Tests;

public class StreamJsonParserTests
{
    private readonly StreamJsonParser _parser = new();

    [Fact]
    public void SystemMessage_FiresOnSessionStarted()
    {
        string? sid = null;
        string? model = null;
        List<string>? tools = null;
        _parser.OnSessionStarted += (s, m, t) => { sid = s; model = m; tools = t; };

        _parser.ProcessLine("""{"type":"system","session_id":"abc123","model":"claude-sonnet","tools":["Read","Write"]}""");

        Assert.Equal("abc123", sid);
        Assert.Equal("claude-sonnet", model);
        Assert.Equal(["Read", "Write"], tools);
        Assert.Equal("abc123", _parser.SessionId);
    }

    [Fact]
    public void SystemMessage_WithToolObjects_ParsesNames()
    {
        List<string>? tools = null;
        _parser.OnSessionStarted += (_, _, t) => tools = t;

        _parser.ProcessLine("""{"type":"system","session_id":"s1","tools":[{"name":"Read"},{"name":"Write"}]}""");

        Assert.Equal(["Read", "Write"], tools);
    }

    [Fact]
    public void StreamEvent_ContentBlockStart_Text_FiresOnTextBlockStart()
    {
        bool fired = false;
        _parser.OnTextBlockStart += () => fired = true;

        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"text"}}}""");

        Assert.True(fired);
    }

    [Fact]
    public void StreamEvent_ContentBlockStart_ToolUse_FiresOnToolUseStarted()
    {
        string? name = null;
        string? id = null;
        _parser.OnToolUseStarted += (n, i, _) => { name = n; id = i; };

        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"tool_use","name":"Read","id":"tu1","input":{}}}}""");

        Assert.Equal("Read", name);
        Assert.Equal("tu1", id);
    }

    [Fact]
    public void StreamEvent_ContentBlockDelta_TextDelta_FiresOnTextDelta()
    {
        string? text = null;
        _parser.OnTextDelta += t => text = t;

        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hello"}}}""");

        Assert.Equal("Hello", text);
    }

    [Fact]
    public void StreamEvent_ContentBlockDelta_InputJsonDelta_AccumulatesInput()
    {
        string? finalInput = null;
        int callCount = 0;
        _parser.OnToolUseStarted += (_, _, input) => { finalInput = input; callCount++; };

        // Start a tool use
        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"tool_use","name":"Read","id":"tu1","input":{}}}}""");
        // Send partial JSON deltas
        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{\"file"}}}""");
        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"_path\":\"/tmp/test.txt\"}"}}}""");
        // Stop — fires OnToolUseStarted with complete input
        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"content_block_stop"}}""");

        Assert.Equal(2, callCount); // Once at start, once at stop
        Assert.Contains("/tmp/test.txt", finalInput);
    }

    [Fact]
    public void StreamEvent_ContentBlockStop_WriteTool_FiresOnFileChanged()
    {
        string? changedPath = null;
        _parser.OnFileChanged += path => changedPath = path;

        // Start a Write tool use
        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"tool_use","name":"Write","id":"tu1","input":{}}}}""");
        // Send input with file_path
        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{\"file_path\":\"/tmp/out.txt\",\"content\":\"hello\"}"}}}""");
        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"content_block_stop"}}""");

        Assert.Equal("/tmp/out.txt", changedPath);
    }

    [Fact]
    public void Result_FiresOnCompleted_WithResultData()
    {
        ResultData? result = null;
        _parser.OnCompleted += r => result = r;

        _parser.ProcessLine("""{"type":"result","session_id":"sess1","usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":10,"cache_creation_input_tokens":5},"modelUsage":{"claude-sonnet":{"contextWindow":200000}}}""");

        Assert.NotNull(result);
        Assert.Equal("sess1", result.SessionId);
        Assert.Equal("claude-sonnet", result.Model);
        Assert.Equal(100, result.InputTokens);
        Assert.Equal(50, result.OutputTokens);
        Assert.Equal(10, result.CacheReadTokens);
        Assert.Equal(5, result.CacheCreationTokens);
        Assert.Equal(200000, result.ContextWindow);
        Assert.Equal("sess1", _parser.SessionId);
    }

    [Fact]
    public void ControlRequest_FiresOnControlRequest()
    {
        string? requestId = null;
        string? toolName = null;
        _parser.OnControlRequest += (rid, tn, _, _) => { requestId = rid; toolName = tn; };

        _parser.ProcessLine("""{"type":"control_request","request_id":"req1","request":{"subtype":"can_use_tool","tool_name":"ExitPlanMode","tool_use_id":"tu1","input":{}}}""");

        Assert.Equal("req1", requestId);
        Assert.Equal("ExitPlanMode", toolName);
    }

    [Fact]
    public void AssistantMessage_ToolUse_WriteTool_FiresOnFileChanged()
    {
        string? changedPath = null;
        _parser.OnFileChanged += path => changedPath = path;

        _parser.ProcessLine("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"/src/main.cs","old_string":"a","new_string":"b"}}]}}""");

        Assert.Equal("/src/main.cs", changedPath);
    }

    [Fact]
    public void UserMessage_ToolResult_FiresOnToolResult()
    {
        string? toolName = null;
        string? content = null;
        _parser.OnToolResult += (tn, _, c) => { toolName = tn; content = c; };

        // After content_block_stop, _currentToolName is cleared, so tool_result uses "Unknown"
        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"tool_use","name":"Read","id":"tu1","input":{}}}}""");
        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"content_block_stop"}}""");

        _parser.ProcessLine("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu1","content":"file contents here"}]}}""");

        Assert.Equal("Unknown", toolName);
        Assert.Equal("file contents here", content);
    }

    [Fact]
    public void UserMessage_ToolResult_ArrayContent_ConcatenatesText()
    {
        string? content = null;
        _parser.OnToolResult += (_, _, c) => content = c;

        _parser.ProcessLine("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu1","content":[{"text":"part1"},{"text":"part2"}]}]}}""");

        Assert.Equal("part1part2", content);
    }

    [Fact]
    public void InvalidJson_NoCrash_NoEvents()
    {
        bool anyFired = false;
        _parser.OnTextDelta += _ => anyFired = true;
        _parser.OnCompleted += _ => anyFired = true;
        _parser.OnSessionStarted += (_, _, _) => anyFired = true;

        _parser.ProcessLine("not json at all {{{");
        _parser.ProcessLine("");
        _parser.ProcessLine("{invalid}");

        Assert.False(anyFired);
    }

    [Fact]
    public void UnknownType_NoCrash()
    {
        bool anyFired = false;
        _parser.OnTextDelta += _ => anyFired = true;

        _parser.ProcessLine("""{"type":"unknown_future_type","data":"something"}""");

        Assert.False(anyFired);
    }

    [Fact]
    public void LegacyContentBlockStart_Text_FiresOnTextBlockStart()
    {
        bool fired = false;
        _parser.OnTextBlockStart += () => fired = true;

        _parser.ProcessLine("""{"type":"content_block_start","content_block":{"type":"text"}}""");

        Assert.True(fired);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        _parser.ProcessLine("""{"type":"system","session_id":"abc","tools":[]}""");
        Assert.Equal("abc", _parser.SessionId);

        _parser.Reset();

        Assert.Null(_parser.SessionId);
    }

    [Fact]
    public void MessageStart_ExtractsPerCallUsage_IntoResultData()
    {
        ResultData? result = null;
        _parser.OnCompleted += r => result = r;

        // Simulate message_start with per-call usage (first API call in the turn)
        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"message_start","message":{"usage":{"input_tokens":50,"cache_read_input_tokens":20000,"cache_creation_input_tokens":100}}}}""");

        // Simulate a second message_start (next API call after tool use) — should overwrite
        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"message_start","message":{"usage":{"input_tokens":60,"cache_read_input_tokens":21000,"cache_creation_input_tokens":200}}}}""");

        // message_delta with output tokens
        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"message_delta","usage":{"output_tokens":500}}}""");

        // Result event with aggregated values (sum of both calls)
        _parser.ProcessLine("""{"type":"result","session_id":"s1","usage":{"input_tokens":110,"output_tokens":900,"cache_read_input_tokens":41000,"cache_creation_input_tokens":300},"modelUsage":{"claude-sonnet":{"contextWindow":200000}}}""");

        Assert.NotNull(result);

        // Aggregated values (sum of all API calls)
        Assert.Equal(110, result.InputTokens);
        Assert.Equal(900, result.OutputTokens);
        Assert.Equal(41000, result.CacheReadTokens);
        Assert.Equal(300, result.CacheCreationTokens);

        // Per-call values (from the LAST message_start/delta — second API call)
        Assert.Equal(60, result.LastCallInputTokens);
        Assert.Equal(21000, result.LastCallCacheReadTokens);
        Assert.Equal(200, result.LastCallCacheCreationTokens);
        Assert.Equal(500, result.LastCallOutputTokens);
    }

    [Fact]
    public void MessageStart_NoUsage_PerCallFieldsStayZero()
    {
        ResultData? result = null;
        _parser.OnCompleted += r => result = r;

        // message_start without usage (older CLI version)
        _parser.ProcessLine("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg1"}}}""");

        _parser.ProcessLine("""{"type":"result","session_id":"s1","usage":{"input_tokens":10,"output_tokens":20},"modelUsage":{"claude-sonnet":{"contextWindow":200000}}}""");

        Assert.NotNull(result);
        Assert.Equal(0, result.LastCallInputTokens);
        Assert.Equal(0, result.LastCallCacheReadTokens);
        Assert.Equal(0, result.LastCallCacheCreationTokens);
        Assert.Equal(0, result.LastCallOutputTokens);
    }
}
