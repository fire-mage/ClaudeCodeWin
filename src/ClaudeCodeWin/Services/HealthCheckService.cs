using System.Diagnostics;
using System.IO;
using System.Net.Http;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class HealthCheckService
{
    private readonly ClaudeCodeDependencyService _deps;

    public HealthCheckService(ClaudeCodeDependencyService deps)
    {
        _deps = deps;
    }

    public async Task<List<HealthCheckResult>> RunAllChecksAsync(string? workingDirectory)
    {
        var tasks = new List<Task<HealthCheckResult>>
        {
            CheckClaudeCliAsync(),
            CheckAuthenticationAsync(),
            CheckGitAsync(),
            CheckGhCliAsync(),
            CheckNodeAsync(),
            CheckWorkingDirectoryAsync(workingDirectory),
            CheckInternetAsync(),
        };

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private async Task<HealthCheckResult> CheckClaudeCliAsync()
    {
        try
        {
            var status = await _deps.CheckAsync();
            if (!status.IsInstalled)
                return new("Claude CLI", HealthStatus.Error, "Not found");

            var version = await GetVersionAsync(status.ExePath!);
            var detail = version is not null
                ? $"{version}  ({status.ExePath})"
                : status.ExePath!;
            return new("Claude CLI", HealthStatus.OK, detail);
        }
        catch (Exception ex)
        {
            return new("Claude CLI", HealthStatus.Error, ex.Message);
        }
    }

    private Task<HealthCheckResult> CheckAuthenticationAsync()
    {
        try
        {
            var ok = _deps.IsAuthenticated();
            return Task.FromResult(ok
                ? new HealthCheckResult("Authentication", HealthStatus.OK, "Authenticated (OAuth token present)")
                : new HealthCheckResult("Authentication", HealthStatus.Error, "Not authenticated — no OAuth token found"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult("Authentication", HealthStatus.Error, ex.Message));
        }
    }

    private Task<HealthCheckResult> CheckGitAsync()
    {
        try
        {
            var gitExe = ClaudeCodeDependencyService.FindGitExe();
            if (gitExe is not null)
            {
                var version = GetVersionSync(gitExe);
                var detail = version is not null ? $"{version}  ({gitExe})" : gitExe;
                return Task.FromResult(new HealthCheckResult("Git", HealthStatus.OK, detail));
            }

            var inPath = _deps.IsGitInstalled();
            return Task.FromResult(inPath
                ? new HealthCheckResult("Git", HealthStatus.OK, "Found in PATH")
                : new HealthCheckResult("Git", HealthStatus.Warning, "Not found — some features may not work"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult("Git", HealthStatus.Error, ex.Message));
        }
    }

    private Task<HealthCheckResult> CheckGhCliAsync()
    {
        try
        {
            var ghPath = _deps.ResolveGhExePath();
            if (ghPath is not null)
            {
                var version = GetVersionSync(ghPath);
                var detail = version is not null ? $"{version}  ({ghPath})" : ghPath;
                return Task.FromResult(new HealthCheckResult("GitHub CLI (gh)", HealthStatus.OK, detail));
            }

            return Task.FromResult(new HealthCheckResult("GitHub CLI (gh)", HealthStatus.Warning,
                "Not found — GitHub integration unavailable"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult("GitHub CLI (gh)", HealthStatus.Error, ex.Message));
        }
    }

    private Task<HealthCheckResult> CheckNodeAsync()
    {
        try
        {
            var version = GetVersionSync("node");
            if (version is not null)
                return Task.FromResult(new HealthCheckResult("Node.js", HealthStatus.OK, version));

            return Task.FromResult(new HealthCheckResult("Node.js", HealthStatus.Warning,
                "Not found — npm-based CLI fallback unavailable"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult("Node.js", HealthStatus.Error, ex.Message));
        }
    }

    private Task<HealthCheckResult> CheckWorkingDirectoryAsync(string? workingDirectory)
    {
        try
        {
            if (string.IsNullOrEmpty(workingDirectory))
                return Task.FromResult(new HealthCheckResult("Working Directory", HealthStatus.Warning, "Not set"));

            if (!Directory.Exists(workingDirectory))
                return Task.FromResult(new HealthCheckResult("Working Directory", HealthStatus.Error,
                    $"Does not exist: {workingDirectory}"));

            var hasGit = Directory.Exists(Path.Combine(workingDirectory, ".git"));
            var detail = hasGit
                ? $"{workingDirectory}  (git repo)"
                : $"{workingDirectory}  (no .git)";

            return Task.FromResult(new HealthCheckResult("Working Directory",
                hasGit ? HealthStatus.OK : HealthStatus.Warning, detail));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult("Working Directory", HealthStatus.Error, ex.Message));
        }
    }

    private static async Task<HealthCheckResult> CheckInternetAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://api.anthropic.com");
            using var response = await http.SendAsync(request);
            return new("Internet", HealthStatus.OK, $"Connected (api.anthropic.com → {(int)response.StatusCode})");
        }
        catch (TaskCanceledException)
        {
            return new("Internet", HealthStatus.Error, "Timeout — api.anthropic.com not reachable");
        }
        catch (Exception ex)
        {
            return new("Internet", HealthStatus.Error, $"Not connected: {ex.Message}");
        }
    }

    private static async Task<string?> GetVersionAsync(string exePath)
    {
        try
        {
            var isCmdFile = exePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                         || exePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = isCmdFile ? "cmd.exe" : exePath,
                Arguments = isCmdFile ? $"/c \"\"{exePath}\" --version\"" : "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // Clear CLAUDECODE to allow nested CLI execution
            psi.Environment["CLAUDECODE"] = "";

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);

            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch { } }

            var clean = ClaudeCodeDependencyService.StripAnsi(output).Trim();
            return string.IsNullOrEmpty(clean) ? null : clean;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetVersionSync(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            var clean = ClaudeCodeDependencyService.StripAnsi(output).Trim();
            return string.IsNullOrEmpty(clean) ? null : clean;
        }
        catch
        {
            return null;
        }
    }
}
