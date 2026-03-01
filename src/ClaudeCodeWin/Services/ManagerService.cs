using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Manages a single Claude CLI session acting as a Team Manager.
/// The Manager has context about the backlog and can advise on priorities,
/// discuss ideas, and execute structured actions (reprioritize, cancel).
/// </summary>
public class ManagerService
{
    private ClaudeCliService? _cli;
    private string? _claudeExePath;
    private string? _workingDirectory;
    private readonly StringBuilder _currentResponse = new();
    private readonly object _responseLock = new();
    private bool _isBusy;

    public event Action<string>? OnTextDelta;
    public event Action<string>? OnCompleted; // full response text
    public event Action<ManagerAction>? OnActionParsed;
    public event Action<string>? OnError;

    public bool IsActive => _cli != null;
    public bool IsBusy => _isBusy;

    public void Configure(string claudeExePath, string? workingDirectory)
    {
        _claudeExePath = claudeExePath;
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// Start a Manager session with the given backlog context injected into the system prompt.
    /// </summary>
    public void StartSession(string systemPrompt)
    {
        if (_cli != null) return;

        _cli = new ClaudeCliService
        {
            ClaudeExePath = _claudeExePath ?? "claude",
            WorkingDirectory = _workingDirectory,
            SystemPrompt = systemPrompt,
            DangerouslySkipPermissions = false
        };

        _isBusy = false;
        WireEvents();
    }

    /// <summary>
    /// Send a user message to the Manager.
    /// </summary>
    public void SendMessage(string text)
    {
        if (_cli == null || _isBusy) return;
        lock (_responseLock) { _currentResponse.Clear(); }
        _isBusy = true;
        _cli.SendMessage(text);
    }

    /// <summary>
    /// Stop the Manager session.
    /// </summary>
    public void StopSession()
    {
        var cli = DetachCli();
        cli?.StopSession();
        lock (_responseLock) { _currentResponse.Clear(); }
        _isBusy = false;
    }

    /// <summary>
    /// Atomically detach the CLI instance and unsubscribe all event handlers.
    /// Thread-safe: only one caller wins the exchange.
    /// </summary>
    private ClaudeCliService? DetachCli()
    {
        var cli = Interlocked.Exchange(ref _cli, null);
        if (cli != null)
        {
            cli.OnTextDelta -= HandleTextDelta;
            cli.OnCompleted -= HandleCompleted;
            cli.OnError -= HandleError;
            cli.OnControlRequest -= HandleControlRequest;
        }
        return cli;
    }

    private void WireEvents()
    {
        if (_cli == null) return;

        _cli.OnTextDelta += HandleTextDelta;
        _cli.OnCompleted += HandleCompleted;
        _cli.OnError += HandleError;
        _cli.OnControlRequest += HandleControlRequest;
    }

    private void HandleTextDelta(string text)
    {
        lock (_responseLock) { _currentResponse.Append(text); }
        OnTextDelta?.Invoke(text);
    }

    private void HandleCompleted(ResultData result)
    {
        string fullText;
        lock (_responseLock) { fullText = _currentResponse.ToString(); _currentResponse.Clear(); }
        _isBusy = false;

        // Parse action blocks from the response
        var actions = ParseActions(fullText);
        foreach (var action in actions)
            OnActionParsed?.Invoke(action);

        OnCompleted?.Invoke(fullText);
    }

    private void HandleError(string error)
    {
        lock (_responseLock) { _currentResponse.Clear(); }
        _isBusy = false;
        var cli = DetachCli();
        cli?.StopSession(); // stop the CLI process
        OnError?.Invoke(error);
    }

    private void HandleControlRequest(string requestId, string toolName,
        string toolUseId, JsonElement input)
    {
        // Manager is read-only: allow exploration tools, deny writes
        var allowed = toolName is "Read" or "Glob" or "Grep"
            or "WebFetch" or "WebSearch";
        _cli?.SendControlResponse(requestId, allowed ? "allow" : "deny",
            toolUseId: toolUseId);
    }

    /// <summary>
    /// Parse ```action JSON blocks from Manager's response.
    /// </summary>
    internal static List<ManagerAction> ParseActions(string text)
    {
        var actions = new List<ManagerAction>();

        var searchFrom = 0;
        while (searchFrom < text.Length)
        {
            var start = text.IndexOf("```action", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;

            var contentStart = text.IndexOf('\n', start);
            if (contentStart < 0) break;
            contentStart++;

            var end = text.IndexOf("```", contentStart, StringComparison.Ordinal);
            if (end < 0) break;

            var json = text[contentStart..end].Trim();
            searchFrom = end + 3;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var typeStr = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (typeStr == null) continue;

                var action = new ManagerAction
                {
                    FeatureId = root.TryGetProperty("featureId", out var fid) ? fid.GetString() : null,
                    Reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null
                };

                if (typeStr.Equals("reprioritize", StringComparison.OrdinalIgnoreCase))
                {
                    action.Type = ManagerActionType.Reprioritize;
                    action.NewPriority = root.TryGetProperty("newPriority", out var np)
                        ? np.GetInt32() : null;
                }
                else if (typeStr.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    action.Type = ManagerActionType.Cancel;
                }
                else if (typeStr.Equals("suggest", StringComparison.OrdinalIgnoreCase))
                {
                    action.Type = ManagerActionType.Suggest;
                }
                else
                {
                    continue;
                }

                actions.Add(action);
            }
            catch (JsonException)
            {
                // Skip malformed action blocks
            }
        }

        return actions;
    }
}
