using System.Diagnostics;

namespace ClaudeCodeWin.Services;

public class GitService
{
    public (string? Branch, int DirtyCount) GetStatus(string? workingDir)
    {
        if (string.IsNullOrEmpty(workingDir))
            return (null, 0);

        var branch = RunGit("rev-parse --abbrev-ref HEAD", workingDir);
        if (branch is null)
            return (null, 0);

        var porcelain = RunGit("status --porcelain", workingDir);
        var dirtyCount = string.IsNullOrEmpty(porcelain)
            ? 0
            : porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        return (branch.Trim(), dirtyCount);
    }

    public string? RunGit(string arguments, string? workingDir)
    {
        if (string.IsNullOrEmpty(workingDir))
            return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
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
