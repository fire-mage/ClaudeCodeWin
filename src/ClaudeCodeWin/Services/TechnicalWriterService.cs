using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Background service that monitors Claude's chat responses and extracts
/// useful project knowledge into the Knowledge Base.
/// Follows the PlannerService pattern: separate CLI instance, event-based lifecycle.
/// </summary>
public class TechnicalWriterService
{
    private readonly KnowledgeBaseService _kbService;
    private string? _claudeExePath;

    // Accumulated text tagged per-project to avoid cross-project contamination
    private readonly object _bufferLock = new();
    private readonly object _timerLock = new();
    private readonly Dictionary<string, StringBuilder> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Timers.Timer _debounceTimer;
    private int _isProcessing; // 0 = idle, 1 = processing (atomic via Interlocked)
    private volatile bool _stopped; // prevents timer restart after Flush/Shutdown
    private volatile bool _shutdownCalled; // permanent — prevents any timer access after Dispose

    private const int DebounceSeconds = 30;
    private const int MinTextLengthToProcess = 500;

    /// <summary>Fires when KB articles are updated. Called from ThreadPool — callers must dispatch to UI thread. Args: projectPath, summary.</summary>
    public event Action<string, string>? OnArticlesUpdated;

    public TechnicalWriterService(KnowledgeBaseService kbService)
    {
        _kbService = kbService;
        _debounceTimer = new System.Timers.Timer(DebounceSeconds * 1000)
        {
            AutoReset = false
        };
        _debounceTimer.Elapsed += (_, _) =>
        {
            _ = ProcessAccumulatedTextAsync();
        };
    }

    public void Configure(string claudeExePath)
    {
        _claudeExePath = claudeExePath;
    }

    /// <summary>
    /// Feed completed assistant response text for later processing.
    /// Each call is tagged with the source project's working directory.
    /// </summary>
    public void AccumulateText(string workingDirectory, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(workingDirectory)) return;

        lock (_bufferLock)
        {
            if (!_buffers.TryGetValue(workingDirectory, out var sb))
            {
                sb = new StringBuilder();
                _buffers[workingDirectory] = sb;
            }
            sb.AppendLine(text);
            sb.AppendLine("---");
        }

        // Reset debounce timer (skip if stopped by Flush/Shutdown)
        lock (_timerLock)
        {
            if (!_stopped)
            {
                _debounceTimer.Stop();
                _debounceTimer.Start();
            }
        }
    }

    /// <summary>
    /// Stops the debounce timer and clears buffers (call on app shutdown).
    /// </summary>
    public void Shutdown()
    {
        if (_shutdownCalled) return; // idempotent — safe to call from multiple tabs
        _shutdownCalled = true;
        lock (_timerLock)
        {
            _stopped = true;
            _debounceTimer.Stop();
            _debounceTimer.Dispose();
        }
        lock (_bufferLock) _buffers.Clear();
    }

    /// <summary>
    /// Force immediate processing (e.g., on session end).
    /// Resets _stopped so accumulation resumes for new project context.
    /// </summary>
    public void Flush()
    {
        lock (_timerLock)
        {
            _stopped = true;
            _debounceTimer.Stop();
        }
        _ = ProcessAccumulatedTextAsync().ContinueWith(t =>
        {
            if (_shutdownCalled) return; // timer already disposed — do not touch
            bool hasBufferedData;
            lock (_timerLock) _stopped = false;
            lock (_bufferLock) hasBufferedData = _buffers.Any(kv => kv.Value.Length >= MinTextLengthToProcess);
            // If text was accumulated while stopped, kick the timer so it's not orphaned
            if (hasBufferedData)
            {
                lock (_timerLock)
                {
                    if (!_stopped && !_shutdownCalled)
                    {
                        _debounceTimer.Stop();
                        _debounceTimer.Start();
                    }
                }
            }
            if (t.IsFaulted) DiagnosticLogger.Log("TECH_WRITER_FLUSH_ERROR", t.Exception?.Message ?? "");
        }, TaskScheduler.Default);
    }

    private async Task ProcessAccumulatedTextAsync()
    {
        // Atomic check-and-set to prevent concurrent processing
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
            return;

        // Snapshot and clear all buffers under lock
        Dictionary<string, string> snapshots;
        lock (_bufferLock)
        {
            snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dir, sb) in _buffers)
            {
                if (sb.Length >= MinTextLengthToProcess)
                {
                    snapshots[dir] = sb.ToString();
                    sb.Clear();
                }
            }

            // Remove empty buffers
            foreach (var key in _buffers.Where(kv => kv.Value.Length == 0).Select(kv => kv.Key).ToList())
                _buffers.Remove(key);
        }

        if (snapshots.Count == 0)
        {
            Interlocked.Exchange(ref _isProcessing, 0);
            return;
        }

        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (workingDir, text) in snapshots)
            {
                if (string.IsNullOrEmpty(_claudeExePath)) continue;
                await RunWriterSessionAsync(workingDir, text);
                processed.Add(workingDir);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("TECH_WRITER_ERROR", ex.Message);

            // Restore unprocessed snapshots back to buffers to avoid data loss
            lock (_bufferLock)
            {
                foreach (var (dir, text) in snapshots)
                {
                    if (processed.Contains(dir)) continue;
                    if (!_buffers.TryGetValue(dir, out var sb))
                    {
                        sb = new StringBuilder();
                        _buffers[dir] = sb;
                    }
                    sb.Insert(0, text); // prepend old text before any new accumulations
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isProcessing, 0);
        }
    }

    private async Task RunWriterSessionAsync(string workingDir, string accumulatedText)
    {
        var entries = _kbService.LoadEntries(workingDir);
        var indexJson = JsonSerializer.Serialize(entries, JsonDefaults.Options);
        var userMessage = OnboardingPrompts.BuildTechnicalWriterMessage(accumulatedText, indexJson);

        var cli = new ClaudeCliService
        {
            ClaudeExePath = _claudeExePath ?? "claude",
            WorkingDirectory = workingDir,
            SystemPrompt = OnboardingPrompts.TechnicalWriterSystemPrompt,
            DangerouslySkipPermissions = false,
            ModelOverride = OnboardingPrompts.OnboardingModelId
        };

        var response = new StringBuilder();
        var responseLock = new object();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        cli.OnTextDelta += t => { lock (responseLock) response.Append(t); };

        cli.OnCompleted += result =>
        {
            try
            {
                string text;
                lock (responseLock) text = response.ToString();
                ApplyWriterResult(workingDir, text);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("TECH_WRITER_APPLY_ERROR", ex.Message);
            }
            finally
            {
                cli.StopSession();
                tcs.TrySetResult();
            }
        };

        cli.OnError += error =>
        {
            DiagnosticLogger.Log("TECH_WRITER_CLI_ERROR", error);
            cli.StopSession();
            tcs.TrySetResult();
        };

        // Deny all tool use — the writer only needs to produce JSON output
        cli.OnControlRequest += (requestId, toolName, toolUseId, _) =>
        {
            cli.SendControlResponse(requestId, "deny", toolUseId: toolUseId);
        };

        cli.SendMessage(userMessage);

        // Wait up to 3 minutes; force-stop on timeout (no thread pool thread blocked)
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            DiagnosticLogger.Log("TECH_WRITER_TIMEOUT", workingDir);
            cli.StopSession();
        }
    }

    private void ApplyWriterResult(string workingDir, string fullText)
    {
        var json = JsonBlockExtractor.Extract(fullText, "articles");
        if (json is null) return;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("articles", out var articles) || articles.GetArrayLength() == 0)
            return;

        var created = 0;
        var updated = 0;

        foreach (var article in articles.EnumerateArray())
        {
            try
            {
                if (!article.TryGetProperty("action", out var actionEl) ||
                    !article.TryGetProperty("id", out var idEl) ||
                    !article.TryGetProperty("content", out var contentEl))
                    continue;

                var action = actionEl.GetString();
                var id = idEl.GetString();
                var content = contentEl.GetString();

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(content)
                    || !KnowledgeBaseService.IsValidArticleId(id))
                    continue;

                if (action == "create")
                {
                    var whenToRead = article.TryGetProperty("whenToRead", out var wtr) ? wtr.GetString() ?? "" : "";
                    var tags = new List<string>();
                    if (article.TryGetProperty("tags", out var tagsEl))
                    {
                        foreach (var tag in tagsEl.EnumerateArray())
                        {
                            var tagStr = tag.GetString();
                            if (!string.IsNullOrEmpty(tagStr))
                                tags.Add(tagStr);
                        }
                    }

                    var entry = new KnowledgeBaseEntry
                    {
                        Id = id,
                        Date = DateTime.Now,
                        Source = "claude",
                        Tags = tags,
                        WhenToRead = whenToRead,
                        File = $"{id}.md"
                    };

                    _kbService.SaveEntry(workingDir, entry, content);
                    created++;
                }
                else if (action == "update")
                {
                    var existingEntries = _kbService.LoadEntries(workingDir);
                    var existing = existingEntries.FirstOrDefault(e => e.Id == id);
                    if (existing is not null)
                    {
                        existing.Date = DateTime.Now;
                        _kbService.SaveEntry(workingDir, existing, content);
                        updated++;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("TECH_WRITER_ARTICLE_ERROR", ex.Message);
            }
        }

        if (created > 0 || updated > 0)
        {
            var summary = $"Technical Writer: {(created > 0 ? $"created {created}" : "")}{(created > 0 && updated > 0 ? ", " : "")}{(updated > 0 ? $"updated {updated}" : "")} KB article(s)";
            OnArticlesUpdated?.Invoke(workingDir, summary);
        }
    }
}
