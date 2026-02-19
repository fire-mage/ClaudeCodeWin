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

    public string? RunGit(string arguments, string? workingDir)
    {
        if (string.IsNullOrEmpty(workingDir))
            return null;

        try
        {
            // Resolve git path: prefer local MinGit if system git not available
            var gitExe = "git";
            var minGitExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeCodeWin", "MinGit", "cmd", "git.exe");
            if (File.Exists(minGitExe))
                gitExe = minGitExe;

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

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
