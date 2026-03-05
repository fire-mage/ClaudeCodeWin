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

    private async Task<HealthCheckResult> CheckGitAsync()
    {
        try
        {
            var gitExe = ClaudeCodeDependencyService.FindGitExe();
            if (gitExe is not null)
            {
                var version = GetVersionSync(gitExe);
                var detail = version is not null ? $"{version}  ({gitExe})" : gitExe;
                return new HealthCheckResult("Git", HealthStatus.OK, detail);
            }

            var inPath = await _deps.IsGitInstalledAsync();
            return inPath
                ? new HealthCheckResult("Git", HealthStatus.OK, "Found in PATH")
                : new HealthCheckResult("Git", HealthStatus.Warning, "Not found — some features may not work");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult("Git", HealthStatus.Error, ex.Message);
        }
    }

    private async Task<HealthCheckResult> CheckGhCliAsync()
    {
        try
        {
            var ghPath = await _deps.ResolveGhExePathAsync();
            if (ghPath is not null)
            {
                var version = GetVersionSync(ghPath);
                var detail = version is not null ? $"{version}  ({ghPath})" : ghPath;
                return new HealthCheckResult("GitHub CLI (gh)", HealthStatus.OK, detail);
            }

            return new HealthCheckResult("GitHub CLI (gh)", HealthStatus.Warning,
                "Not found — GitHub integration unavailable");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult("GitHub CLI (gh)", HealthStatus.Error, ex.Message);
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

    // Fix: static HttpClient to prevent socket exhaustion from per-call creation.
    // FIX (WARNING #2): PooledConnectionLifetime forces periodic DNS re-resolution.
    private static readonly HttpClient InternetCheckHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    private static async Task<HealthCheckResult> CheckInternetAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://api.anthropic.com");
            using var response = await InternetCheckHttp.SendAsync(request);
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
            // FIX: Drain stderr async to prevent deadlock when stderr buffer fills.
            // Await after stdout to observe any I/O exceptions (avoids UnobservedTaskException).
            var stderrTask = process.StandardError.ReadToEndAsync();
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            try { await stderrTask; } catch { }

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

            // FIX: Read stderr async to prevent deadlock — sequential ReadToEnd hangs
            // if stderr buffer fills before stdout EOF
            var stderrTask = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            // FIX (WARNING #3): catch exceptions from Wait() to prevent UnobservedTaskException on GC
            // if the task faults or times out
            try { stderrTask.Wait(5000); } catch { }
            if (!process.WaitForExit(5000))
                return null;

            var clean = ClaudeCodeDependencyService.StripAnsi(output).Trim();
            return string.IsNullOrEmpty(clean) ? null : clean;
        }
        catch
        {
            return null;
        }
    }
}
