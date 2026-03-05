using System.Diagnostics;
using System.IO;

namespace ClaudeCodeWin.Services;

public class GitService
{
    public (string? Branch, int DirtyCount, int UnpushedCount) GetStatus(string? workingDir)
    {
        if (string.IsNullOrEmpty(workingDir))
            return (null, 0, 0);

        var branch = RunGit("rev-parse --abbrev-ref HEAD", workingDir);
        if (branch is null)
            return (null, 0, 0);

        var porcelain = RunGit("status --porcelain", workingDir);
        var dirtyCount = string.IsNullOrEmpty(porcelain)
            ? 0
            : porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        // Count commits ahead of remote (unpushed)
        var unpushedCount = 0;
        var ahead = RunGit($"rev-list @{{u}}..HEAD --count", workingDir);
        if (ahead is not null && int.TryParse(ahead.Trim(), out var count))
            unpushedCount = count;

        return (branch.Trim(), dirtyCount, unpushedCount);
    }

    /// <summary>
    /// Commits a set of files with the given message. Returns (success, commitHash or error).
    /// Resets the staging area first to avoid committing unrelated pre-staged files.
    /// Uses a temp file for the commit message to avoid shell escaping issues.
    /// </summary>
    public (bool Success, string Message) CommitFiles(
        List<string> files, string commitMessage, string? workingDir)
    {
        if (string.IsNullOrEmpty(workingDir) || files.Count == 0)
            return (false, "No files or working directory");

        // Unstage only OUR files to ensure we don't pick up pre-staged changes
        // for these paths, while preserving any other files the user staged manually
        foreach (var file in files)
            RunGit($"reset HEAD -- \"{file}\"", workingDir);

        // Stage each file individually (handles both modified and untracked)
        foreach (var file in files)
        {
            RunGit($"add -- \"{file}\"", workingDir);
            // Silently skip files that don't exist (already deleted, etc.)
        }

        // Check if anything was staged
        var staged = RunGit("diff --cached --name-only", workingDir);
        if (string.IsNullOrWhiteSpace(staged))
            return (false, "No changes to commit (files may already be committed)");

        // Commit using temp file for message (safe for any characters)
        string? tempFile = null;
        try
        {
            tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile, commitMessage);
            var commitResult = RunGit($"commit -F \"{tempFile}\"", workingDir);
            if (commitResult is null)
                return (false, "Git commit failed");
        }
        finally
        {
            if (tempFile is not null && File.Exists(tempFile))
                File.Delete(tempFile);
        }

        var hash = RunGit("rev-parse --short HEAD", workingDir)?.Trim() ?? "unknown";
        return (true, hash);
    }

    /// <summary>
    /// Returns list of modified/untracked files as absolute paths.
    /// </summary>
    public List<string> GetChangedFiles(string? workingDir)
    {
        if (string.IsNullOrEmpty(workingDir))
            return [];

        var output = RunGit("status --porcelain", workingDir);
        if (string.IsNullOrEmpty(output))
            return [];

        var files = new List<string>();
        foreach (var line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;
            var relativePath = line.Substring(3).Trim().Trim('"');
            var arrowIdx = relativePath.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrowIdx >= 0)
                relativePath = relativePath.Substring(arrowIdx + 4);
            var absPath = Path.GetFullPath(Path.Combine(workingDir, relativePath));
            files.Add(absPath);
        }
        return files;
    }

    public string? RunGit(string arguments, string? workingDir)
    {
        if (string.IsNullOrEmpty(workingDir))
            return null;

        try
        {
            // Use git from PATH (full Git for Windows is installed system-wide)
            var gitExe = "git";

            var psi = new ProcessStartInfo
            {
                FileName = gitExe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir
            };

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            // FIX (WARNING #1): Read BOTH streams concurrently to prevent deadlock.
            // Previously stdout was read synchronously while stderr was async — if stdout
            // buffer filled, the process would block waiting for a consumer, and ReadToEnd()
            // would block waiting for EOF, causing a classic deadlock.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            // Sync-over-async: acceptable for short-lived git commands but can block threadpool.
            // TODO: consider making RunGit async if callers can be updated.
            try { Task.WhenAll(stdoutTask, stderrTask).Wait(15_000); } catch { }

            if (!process.WaitForExit(15_000))
                return null;

            var output = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : null;
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
