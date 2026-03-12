using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Manages automatic project onboarding via a background Claude CLI session.
/// Follows the PlannerService pattern: separate CLI instance, event-based lifecycle.
/// </summary>
public class OnboardingService
{
    private readonly ConcurrentDictionary<string, OnboardingSession> _sessions = new();
    private string? _claudeExePath;

    private readonly ProjectRegistryService _projectRegistry;
    private readonly ScriptService _scriptService;
    private readonly TaskRunnerService _taskRunnerService;
    private readonly KnowledgeBaseService _kbService;

    /// <summary>Stops all running onboarding sessions (call on app shutdown).</summary>
    public void StopAll()
    {
        foreach (var kvp in _sessions)
        {
            if (_sessions.TryRemove(kvp.Key, out var session))
            {
                try
                {
                    session.TimeoutCts.Cancel();
                    session.TimeoutCts.Dispose();
                    session.Cli.StopSession();
                }
                catch { /* shutdown — swallow */ }
            }
        }
    }

    /// <summary>Fires when onboarding completes. Args: projectPath, summary text.</summary>
    public event Action<string, string>? OnOnboardingCompleted;

    /// <summary>Fires on error. Args: projectPath, error message.</summary>
    public event Action<string, string>? OnOnboardingError;

    /// <summary>Fires with progress text. Args: projectPath, progress text.</summary>
    public event Action<string, string>? OnOnboardingProgress;

    public OnboardingService(
        ProjectRegistryService projectRegistry,
        ScriptService scriptService,
        TaskRunnerService taskRunnerService,
        KnowledgeBaseService kbService)
    {
        _projectRegistry = projectRegistry;
        _scriptService = scriptService;
        _taskRunnerService = taskRunnerService;
        _kbService = kbService;
    }

    public void Configure(string claudeExePath)
    {
        _claudeExePath = claudeExePath;
    }

    /// <summary>
    /// Check if a project needs onboarding and start it if so.
    /// Returns true if onboarding was started.
    /// Must be called from UI thread (via Dispatcher.BeginInvoke).
    /// </summary>
    public bool TryStartOnboarding(string projectPath)
    {
        if (string.IsNullOrEmpty(_claudeExePath)) return false;
        if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath)) return false;

        var project = _projectRegistry.GetProject(projectPath);
        if (project is null) return false;
        // Recovery: if status is InProgress but no active session exists (app crashed), reset to None
        if (project.OnboardingStatus == OnboardingStatus.InProgress)
        {
            if (_sessions.ContainsKey(projectPath)) return false; // still running
            _projectRegistry.UpdateOnboardingStatus(projectPath, OnboardingStatus.None);
        }
        else if (project.OnboardingStatus is OnboardingStatus.Completed or OnboardingStatus.Skipped)
            return false;

        // Already running?
        if (_sessions.ContainsKey(projectPath)) return false;

        StartOnboarding(projectPath, project);
        return true;
    }

    public bool IsOnboarding(string projectPath) => _sessions.ContainsKey(projectPath);

    public void SkipOnboarding(string projectPath)
    {
        _projectRegistry.UpdateOnboardingStatus(projectPath, OnboardingStatus.Skipped);
    }

    private void StartOnboarding(string projectPath, ProjectInfo project)
    {
        var cli = CreateCliService(projectPath);
        var session = new OnboardingSession
        {
            Cli = cli,
            ProjectPath = projectPath,
            ProjectName = project.Name
        };

        // TryAdd is the single concurrency gate — status update only after winning the race
        if (!_sessions.TryAdd(projectPath, session))
        {
            cli.StopSession();
            return;
        }

        _projectRegistry.UpdateOnboardingStatus(projectPath, OnboardingStatus.InProgress);
        OnOnboardingProgress?.Invoke(projectPath, $"Onboarding project {project.Name}...");

        WireEvents(session);

        var claudeMdExists = File.Exists(Path.Combine(projectPath, "CLAUDE.md"));
        var message = OnboardingPrompts.BuildOnboardingMessage(project.Name, project.TechStack, claudeMdExists);

        try
        {
            cli.SendMessage(message);

            // Safety timeout: kill session if CLI hangs (5 minutes).
            // Cancelled when session completes normally to prevent killing a new session for the same project.
            _ = Task.Delay(TimeSpan.FromMinutes(5), session.TimeoutCts.Token).ContinueWith(_ =>
            {
                if (_sessions.TryRemove(projectPath, out var timedOut))
                {
                    timedOut.TimeoutCts.Dispose();
                    timedOut.Cli.StopSession();
                    _projectRegistry.UpdateOnboardingStatus(projectPath, OnboardingStatus.None);
                    OnOnboardingError?.Invoke(projectPath, $"Onboarding timed out for {project.Name}");
                }
            }, CancellationToken.None, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            if (_sessions.TryRemove(projectPath, out var failed))
                failed.Cli.StopSession();
            _projectRegistry.UpdateOnboardingStatus(projectPath, OnboardingStatus.None);
            OnOnboardingError?.Invoke(projectPath, $"Failed to start onboarding: {ex.Message}");
        }
    }

    private ClaudeCliService CreateCliService(string projectPath)
    {
        return new ClaudeCliService
        {
            ClaudeExePath = _claudeExePath ?? "claude",
            WorkingDirectory = projectPath,
            SystemPrompt = OnboardingPrompts.OnboardingSystemPrompt,
            DangerouslySkipPermissions = false,
            ModelOverride = OnboardingPrompts.OnboardingModelId
        };
    }

    private void WireEvents(OnboardingSession session)
    {
        var cli = session.Cli;
        var projectPath = session.ProjectPath;

        cli.OnTextDelta += text =>
        {
            session.AppendText(text);
        };

        cli.OnCompleted += result =>
        {
            var fullText = session.GetResponseText();

            // Only apply result if we won the race (timeout may have already cleaned up)
            if (!_sessions.TryRemove(projectPath, out var removed))
                return;

            removed.TimeoutCts.Cancel(); // cancel the safety timeout
            removed.TimeoutCts.Dispose();
            removed.Cli.StopSession();

            try
            {
                ApplyOnboardingResult(projectPath, session.ProjectName, fullText);
            }
            catch (Exception ex)
            {
                // Ensure status is updated even if ApplyOnboardingResult throws,
                // otherwise the project stays stuck as InProgress forever
                _projectRegistry.UpdateOnboardingStatus(projectPath, OnboardingStatus.Completed);
                OnOnboardingError?.Invoke(projectPath, $"Failed to apply onboarding: {ex.Message}");
            }
        };

        cli.OnError += error =>
        {
            if (_sessions.TryRemove(projectPath, out var removed))
            {
                removed.TimeoutCts.Cancel(); // cancel the safety timeout
                removed.TimeoutCts.Dispose();
                removed.Cli.StopSession();
            }

            _projectRegistry.UpdateOnboardingStatus(projectPath, OnboardingStatus.None);
            OnOnboardingError?.Invoke(projectPath, $"Onboarding error: {error}");
        };

        // Allow read-only tools for project scanning, with path validation.
        // Note: Path.GetFullPath does not resolve symlinks — this is defense-in-depth,
        // primary security is DangerouslySkipPermissions=false (CLI's own permission system).
        var normalizedRoot = Path.GetFullPath(projectPath);
        cli.OnControlRequest += (requestId, toolName, toolUseId, input) =>
        {
            if (toolName is not ("Read" or "Glob" or "Grep"))
            {
                cli.SendControlResponse(requestId, "deny", toolUseId: toolUseId,
                    errorMessage: $"Tool '{toolName}' not permitted during onboarding");
                return;
            }

            // Check glob/pattern params for path traversal
            if (HasPathTraversal(input))
            {
                cli.SendControlResponse(requestId, "deny", toolUseId: toolUseId,
                    errorMessage: "Path traversal in glob/pattern not permitted during onboarding");
                return;
            }

            // Validate that tool paths stay within the project directory
            var pathToCheck = GetToolPath(input);
            if (pathToCheck != null)
            {
                try
                {
                    var fullPath = Path.GetFullPath(pathToCheck, normalizedRoot);
                    if (!fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        cli.SendControlResponse(requestId, "deny", toolUseId: toolUseId,
                            errorMessage: "Path outside project directory not permitted during onboarding");
                        return;
                    }
                }
                catch
                {
                    cli.SendControlResponse(requestId, "deny", toolUseId: toolUseId,
                        errorMessage: "Invalid path");
                    return;
                }
            }

            cli.SendControlResponse(requestId, "allow", toolUseId: toolUseId);
        };
    }

    private void ApplyOnboardingResult(string projectPath, string projectName, string fullText)
    {
        var json = JsonBlockExtractor.Extract(fullText, "description");
        if (json is null)
        {
            _projectRegistry.UpdateOnboardingStatus(projectPath, OnboardingStatus.Completed);
            OnOnboardingError?.Invoke(projectPath, "Onboarding completed but no valid JSON found in response.");
            return;
        }

        OnboardingResult result;
        try
        {
            result = JsonSerializer.Deserialize<OnboardingResult>(json, JsonDefaults.ReadOptions)
                     ?? throw new Exception("Deserialization returned null");
        }
        catch (Exception ex)
        {
            _projectRegistry.UpdateOnboardingStatus(projectPath, OnboardingStatus.Completed);
            OnOnboardingError?.Invoke(projectPath, $"Failed to parse onboarding JSON: {ex.Message}");
            return;
        }

        var summary = new StringBuilder();

        // 1. Update project description
        if (!string.IsNullOrEmpty(result.Description))
        {
            _projectRegistry.UpdateDescription(projectPath, result.Description);
            summary.AppendLine($"Description: {result.Description}");
        }

        // 2. Save proposed CLAUDE.md for user review (not written directly to repo)
        if (!string.IsNullOrEmpty(result.ClaudeMdContent))
        {
            var claudeMdPath = Path.Combine(projectPath, "CLAUDE.md");
            if (!File.Exists(claudeMdPath))
            {
                var proposedPath = Path.Combine(projectPath, "CLAUDE.md.proposed");
                File.WriteAllText(proposedPath, result.ClaudeMdContent);
                summary.AppendLine("Proposed CLAUDE.md saved — review and rename to CLAUDE.md to apply");
            }
        }

        // 3. Merge scripts
        if (result.Scripts.Count > 0)
        {
            var scriptDefs = result.Scripts.Select(s => new ScriptDefinition
            {
                Name = s.Name,
                Prompt = s.Prompt,
                HotKey = s.HotKey
            }).ToList();

            var added = _scriptService.MergeScripts(scriptDefs);
            if (added > 0)
                summary.AppendLine($"Added {added} script(s)");
        }

        // 4. Merge tasks (with dangerous command filtering)
        if (result.Tasks.Count > 0)
        {
            var taskDefs = result.Tasks
                .Where(t => !IsDangerousCommand(t.Command))
                .Select(t => new TaskDefinition
                {
                    Name = $"[AI] {t.Name}",
                    Command = t.Command,
                    Project = t.Project ?? projectName,
                    ConfirmBeforeRun = true, // always force confirmation for LLM-generated tasks
                    AiGenerated = true
                }).ToList();

            var added = _taskRunnerService.MergeTasks(taskDefs);
            if (added > 0)
                summary.AppendLine($"Added {added} task(s)");
        }

        // 5. Create KB articles
        if (result.KbArticles.Count > 0)
        {
            var created = 0;
            foreach (var article in result.KbArticles)
            {
                if (string.IsNullOrEmpty(article.Id) || string.IsNullOrEmpty(article.Content)
                    || !KnowledgeBaseService.IsValidArticleId(article.Id))
                    continue;

                var entry = new KnowledgeBaseEntry
                {
                    Id = article.Id,
                    Date = DateTime.Now,
                    Source = "claude",
                    Tags = article.Tags ?? [],
                    WhenToRead = article.WhenToRead ?? "",
                    File = $"{article.Id}.md"
                };

                if (_kbService.SaveEntry(projectPath, entry, article.Content))
                    created++;
            }

            if (created > 0)
                summary.AppendLine($"Created {created} KB article(s)");
        }

        // 6. Mark as completed
        _projectRegistry.UpdateOnboardingStatus(projectPath, OnboardingStatus.Completed);

        var summaryText = summary.Length > 0
            ? $"Onboarding complete for {projectName}:\n{summary}"
            : $"Onboarding complete for {projectName} (no changes applied).";

        OnOnboardingCompleted?.Invoke(projectPath, summaryText);
    }

    /// <summary>
    /// Best-effort blocklist of dangerous patterns in LLM-generated shell commands.
    /// Not a security boundary — ConfirmBeforeRun=true is the primary defense.
    /// </summary>
    private static readonly Regex[] DangerousPatterns =
    [
        new(@"\brm\s+(-\w*[rf]|--force|--recursive)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bcurl\b.*(\|\s*(ba)?sh|\$\()", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bwget\b.*(\|\s*(ba)?sh|\$\()", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bpowershell\b.*(-enc|-encodedcommand)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\biex\b.*\(.*downloadstring", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(mkfs|dd\s+if=|format\s+[a-z]:)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bchmod\s+(777|a\+rwx)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@">\s*(/etc/|C:\\Windows\\)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(nc|ncat|netcat)\b.*-[le]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\beval\b.*\$\(", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bbase64\s+(-d|--decode)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static bool IsDangerousCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return true;
        return DangerousPatterns.Any(p => p.IsMatch(command));
    }

    private static string? GetToolPath(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        // Read tool uses "file_path", Glob/Grep use "path"
        if (input.TryGetProperty("file_path", out var fp) && fp.ValueKind == JsonValueKind.String)
            return fp.GetString();
        if (input.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
            return p.GetString();
        return null;
    }

    /// <summary>Checks glob param for path traversal or absolute path escape. "pattern" is regex, not a path.</summary>
    private static bool HasPathTraversal(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return false;
        // Only check "glob" — "pattern" is a regex search string for Grep, not a file path
        foreach (var propName in new[] { "glob" })
        {
            if (input.TryGetProperty(propName, out var val) && val.ValueKind == JsonValueKind.String)
            {
                var s = val.GetString();
                if (s == null) continue;
                if (s.Contains("..")) return true;
                // Block absolute paths that could escape project directory
                if (Path.IsPathRooted(s)) return true;
            }
        }
        return false;
    }

    private class OnboardingSession
    {
        public ClaudeCliService Cli { get; init; } = null!;
        public string ProjectPath { get; init; } = "";
        public string ProjectName { get; init; } = "";
        public CancellationTokenSource TimeoutCts { get; } = new();
        private readonly object _responseLock = new();
        private readonly StringBuilder _response = new();

        public void AppendText(string text)
        {
            lock (_responseLock) _response.Append(text);
        }

        public string GetResponseText()
        {
            lock (_responseLock) return _response.ToString();
        }
    }
}
