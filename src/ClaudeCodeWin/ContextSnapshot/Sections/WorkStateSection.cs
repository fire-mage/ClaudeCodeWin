using System.IO;
using System.Text.Json;
using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot.Sections;

public class WorkStateSection : ISnapshotSection
{
    private readonly string _stateFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public string Title => "Work State";

    public WorkStateSection(string stateFilePath)
    {
        _stateFilePath = stateFilePath;
    }

    public void Generate(MarkdownBuilder md, List<AnalysisResult> results, string basePath)
    {
        md.Header(2, "1. Work State");

        var fullPath = Path.GetFullPath(Path.Combine(basePath, _stateFilePath));
        if (!File.Exists(fullPath))
        {
            md.Line("*No state file found.*");
            return;
        }

        try
        {
            var json = File.ReadAllText(fullPath);
            var state = JsonSerializer.Deserialize<WorkState>(json, JsonOptions);
            if (state == null)
            {
                md.Line("*State file is empty.*");
                return;
            }

            if (!string.IsNullOrWhiteSpace(state.CurrentFocus))
                md.Line($"**Current Focus:** {state.CurrentFocus}");

            if (!string.IsNullOrWhiteSpace(state.SessionNotes))
                md.Line($"**Session Notes:** {state.SessionNotes}");

            md.Line($"**Last Updated:** {state.LastUpdated:yyyy-MM-dd HH:mm}");
            md.Line();

            if (state.PendingTasks.Count > 0)
            {
                md.Line("**Pending Tasks:**");
                foreach (var task in state.PendingTasks)
                    md.Line($"- [ ] {task}");
                md.Line();
            }

            if (state.RecentChanges.Count > 0)
            {
                md.Line("**Recent Changes:**");
                foreach (var change in state.RecentChanges.Take(5))
                {
                    var files = change.Files.Count > 0 ? $" ({string.Join(", ", change.Files)})" : "";
                    md.Line($"- [{change.Date}] {change.Description}{files}");
                }
                md.Line();
            }

            if (state.KnownIssues.Count > 0)
            {
                md.Line("**Known Issues:**");
                foreach (var issue in state.KnownIssues)
                    md.Line($"- {issue}");
                md.Line();
            }
        }
        catch
        {
            md.Line("*Error reading state file.*");
        }
    }
}
