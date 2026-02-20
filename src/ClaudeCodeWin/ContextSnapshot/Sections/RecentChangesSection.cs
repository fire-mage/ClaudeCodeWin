using System.Diagnostics;
using ClaudeCodeWin.ContextSnapshot.Models;

namespace ClaudeCodeWin.ContextSnapshot.Sections;

public class RecentChangesSection : ISnapshotSection
{
    public string Title => "Recent Changes";

    public void Generate(MarkdownBuilder md, List<AnalysisResult> results, string basePath)
    {
        if (!IsGitRepo(basePath))
            return;

        md.Header(2, "9. Recent Changes");

        var branch = RunGit(basePath, "rev-parse --abbrev-ref HEAD");
        if (!string.IsNullOrWhiteSpace(branch))
            md.Line($"**Branch:** `{branch.Trim()}`");

        var log = RunGit(basePath, "log --oneline -15 --no-decorate");
        if (!string.IsNullOrWhiteSpace(log))
        {
            md.Line();
            md.Line("**Last 15 commits:**");
            md.CodeBlock(log.Trim());
        }

        var diffStat = RunGit(basePath, "diff --stat HEAD~5 HEAD");
        if (!string.IsNullOrWhiteSpace(diffStat))
        {
            md.Line("**Files changed (last 5 commits):**");
            md.CodeBlock(diffStat.Trim());
        }
    }

    private bool IsGitRepo(string basePath)
    {
        var result = RunGit(basePath, "rev-parse --is-inside-work-tree");
        return result?.Trim() == "true";
    }

    private string? RunGit(string workingDir, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
