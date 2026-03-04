using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Infrastructure;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Launches a full project review: discovers modules via CLI, then creates
/// a BacklogFeature with one phase per module for the Team pipeline to execute.
/// </summary>
public class ProjectReviewService
{
    private readonly BacklogService _backlogService;
    private readonly object _lock = new();
    private string? _claudeExePath;
    private ClaudeCliService? _discoveryCli;
    private System.Timers.Timer? _discoveryTimeout;
    private volatile bool _cancelled;

    public event Action<string>? OnStatusUpdate; // progress messages
    public event Action<string>? OnCompleted;    // featureId when done
    public event Action<string>? OnError;        // error message

    public bool IsRunning { get; private set; }

    public ProjectReviewService(BacklogService backlogService)
    {
        _backlogService = backlogService;
    }

    public void Configure(string claudeExePath)
    {
        _claudeExePath = claudeExePath;
    }

    public void ClearEvents()
    {
        OnStatusUpdate = null;
        OnCompleted = null;
        OnError = null;
    }

    /// <summary>
    /// Start a full project review. Launches a CLI session to discover modules,
    /// then creates a BacklogFeature with phases for each module.
    /// </summary>
    public void StartFullReview(string projectPath)
    {
        lock (_lock)
        {
            if (IsRunning) return;
            IsRunning = true;
        }

        var cli = new ClaudeCliService
        {
            ClaudeExePath = _claudeExePath ?? "claude",
            WorkingDirectory = projectPath,
            SystemPrompt = BuildDiscoverySystemPrompt(),
            DangerouslySkipPermissions = true,
            ModelOverride = TeamPrompts.TeamModelId
        };

        _cancelled = false;
        lock (_lock) { _discoveryCli = cli; }

        var responseBuilder = new StringBuilder();

        cli.OnTextDelta += text => { lock (responseBuilder) responseBuilder.Append(text); };

        cli.OnCompleted += _ =>
        {
            StopTimeout();
            if (_cancelled) return;
            lock (_lock) { _discoveryCli = null; }

            string response;
            lock (responseBuilder) { response = responseBuilder.ToString(); }
            try
            {
                var modules = ParseDiscoveryResponse(response);
                if (modules.Count == 0)
                {
                    OnError?.Invoke("Discovery returned no modules. The project may be empty or unsupported.");
                    return;
                }

                var featureId = CreateReviewFeature(projectPath, modules);
                OnCompleted?.Invoke(featureId);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Failed to parse discovery response: {ex.Message}");
            }
            finally
            {
                lock (_lock) { IsRunning = false; }
            }
        };

        cli.OnError += error =>
        {
            StopTimeout();
            if (_cancelled) return;
            lock (_lock)
            {
                _discoveryCli = null;
                IsRunning = false;
            }
            OnError?.Invoke($"Discovery CLI error: {error}");
        };

        // Start a 15-minute timeout for discovery
        _discoveryTimeout = new System.Timers.Timer(15 * 60 * 1000) { AutoReset = false };
        _discoveryTimeout.Elapsed += (_, _) => Cancel();
        _discoveryTimeout.Start();

        OnStatusUpdate?.Invoke("Analyzing project structure...");
        cli.SendMessage(BuildDiscoveryPrompt(projectPath));
    }

    public void Cancel()
    {
        StopTimeout();
        ClaudeCliService? cli;
        lock (_lock)
        {
            cli = _discoveryCli;
            if (cli == null) return;
            _cancelled = true; // prevent callbacks from double-firing
            _discoveryCli = null;
            IsRunning = false;
        }
        cli.StopSession();
        OnError?.Invoke("Full Project Review was cancelled.");
    }

    private void StopTimeout()
    {
        var t = _discoveryTimeout;
        _discoveryTimeout = null;
        t?.Stop();
        t?.Dispose();
    }

    private static string BuildDiscoverySystemPrompt()
    {
        return """
            You are a project structure analyzer. Your job is to examine a codebase
            and split it into logical modules suitable for sequential code review.

            You have access to Read, Glob, and Grep tools to explore the project.
            Use them to understand the folder structure, file types, and module boundaries.

            ## Rules
            - Split into 3-15 modules (fewer for small projects, more for large ones)
            - Each module should be reviewable in one session (ideally 10-40 files)
            - Group related files together (e.g. a service + its model + its tests)
            - Consider folder structure, namespaces, and functional areas
            - Exclude non-code files (images, binaries, node_modules, bin/obj, etc.)

            ## Tool restrictions
            - ONLY use Read, Glob, and Grep tools — these are read-only and safe
            - NEVER use Bash, Write, Edit, or any tool that modifies files or runs commands
            - Your job is analysis only — do not change anything in the project

            ## Windows Safety
            NEVER use /dev/null in Bash commands. On Windows, use 2>&1 or || true instead.
            """;
    }

    private static string BuildDiscoveryPrompt(string projectPath)
    {
        var projectName = System.IO.Path.GetFileName(projectPath.TrimEnd('\\', '/'));
        return $$"""
            Analyze the project "{{projectName}}" in the current directory.
            Split it into logical modules for a comprehensive code review.

            Explore the project structure using Glob and Read tools first,
            then return ONLY a JSON block (no other text after it):

            ```json
            [
              {
                "title": "Module Name",
                "path": "relative/path/to/module",
                "files": ["file1.cs", "file2.cs"],
                "description": "Brief description of what this module does"
              }
            ]
            ```

            Important:
            - Use relative paths from the project root
            - Include all reviewable source files (not configs, not binaries)
            - Order modules from most critical (core logic) to least critical (utilities)
            """;
    }

    private static List<ReviewModule> ParseDiscoveryResponse(string response)
    {
        // Look for ```json ... ``` block first (the prompt asks for this format)
        var fenceStart = response.IndexOf("```json", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var contentStart = response.IndexOf('\n', fenceStart);
            if (contentStart >= 0)
            {
                var fenceEnd = response.IndexOf("```", contentStart, StringComparison.Ordinal);
                if (fenceEnd > contentStart)
                {
                    var json = response[(contentStart + 1)..fenceEnd].Trim();
                    return JsonSerializer.Deserialize<List<ReviewModule>>(json, JsonDefaults.ReadOptions) ?? [];
                }
            }
        }

        // Fallback: look for JSON array of objects ([{...}])
        var jsonStart = response.IndexOf("[{", StringComparison.Ordinal);
        var jsonEnd = response.LastIndexOf("}]", StringComparison.Ordinal);

        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var json = response[jsonStart..(jsonEnd + 2)];
            try
            {
                return JsonSerializer.Deserialize<List<ReviewModule>>(json, JsonDefaults.ReadOptions) ?? [];
            }
            catch (JsonException) { /* invalid JSON span, fall through */ }
        }

        return [];
    }

    private string CreateReviewFeature(string projectPath, List<ReviewModule> modules)
    {
        var projectName = System.IO.Path.GetFileName(projectPath.TrimEnd('\\', '/'));
        var feature = _backlogService.AddFeature(projectPath,
            $"Full code review of the entire {projectName} project ({modules.Count} modules)");

        _backlogService.ModifyFeature(feature.Id, f =>
        {
            f.Title = $"Full Project Review — {projectName}";
            f.Priority = 50;
            f.Status = FeatureStatus.Queued;
            f.Phases = modules.Select((m, i) => new BacklogPhase
            {
                Order = i + 1,
                Title = $"Review: {m.Title}",
                Plan = BuildModuleReviewPlan(m),
                AcceptanceCriteria = "All identified bugs, security issues, and architectural problems are fixed. Code compiles successfully."
            }).ToList();
        });

        OnStatusUpdate?.Invoke($"Created review plan with {modules.Count} modules");
        return feature.Id;
    }

    private static string BuildModuleReviewPlan(ReviewModule module)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Perform a thorough code review of the [{module.Title}] module.");
        sb.AppendLine($"Module: {module.Description}");
        sb.AppendLine($"Path: {module.Path}");
        sb.AppendLine();
        sb.AppendLine("Files to review:");
        foreach (var file in module.Files ?? [])
            sb.AppendLine($"- {file}");
        sb.AppendLine();
        sb.AppendLine("""
            Review focus:
            1. Bugs and logic errors — find actual bugs, null reference risks, race conditions
            2. Security vulnerabilities — OWASP top 10, injection, insecure data handling
            3. Edge cases and error handling gaps — missing null checks, unhandled exceptions
            4. Architecture and design issues — SOLID violations, tight coupling, misplaced responsibilities
            5. Performance problems — N+1 queries, unnecessary allocations, blocking calls in async code

            For each issue found:
            - Fix it directly in the code
            - Add a brief comment above the fix explaining what was wrong

            Do NOT comment on: code style, naming preferences, minor formatting, or adding documentation.
            Focus only on substantive issues that affect correctness, security, or maintainability.
            """);
        return sb.ToString();
    }

    private class ReviewModule
    {
        public string Title { get; set; } = "";
        public string Path { get; set; } = "";
        public List<string> Files { get; set; } = [];
        public string Description { get; set; } = "";
    }
}
