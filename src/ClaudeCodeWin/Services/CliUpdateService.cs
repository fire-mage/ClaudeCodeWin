using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeCodeWin.Services;

public record CliVersionInfo(string CurrentVersion, string LatestVersion);

public class CliUpdateService
{
    private const string NpmRegistryUrl = "https://registry.npmjs.org/@anthropic-ai/claude-code/latest";

    private static readonly string NativePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "bin", "claude.exe");

    private static readonly string BackupPath = NativePath + ".bak";

    private static readonly string CliRollbackMarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeWin", "updates", "cli-rollback.txt");

    // FIX (WARNING #2): PooledConnectionLifetime forces periodic DNS re-resolution.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Regex VersionRegex = new(@"(\d+\.\d+\.\d+)", RegexOptions.Compiled);

    private Timer? _timer;
    // Fix: use int with Interlocked for thread-safe check (timer callbacks run on threadpool)
    private int _isChecking;

    public string? CurrentCliVersion { get; private set; }
    public string? ExePath { get; set; }
    public HashSet<string> BlacklistedVersions { get; set; } = [];

    public event Action<CliVersionInfo>? OnCliUpdateAvailable;
    public event Action<string>? OnCliUpdateProgress;
    public event Action<string>? OnCliUpdateCompleted;
    public event Action<string>? OnCliUpdateFailed;

    /// <summary>
    /// Run "claude --version" and parse the version string.
    /// </summary>
    public async Task<string?> GetCurrentVersionAsync(string? exePath = null)
    {
        exePath ??= ExePath ?? "claude";
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
            using var process = new Process { StartInfo = psi };
            process.Start();

            // Fix: drain stderr async to prevent deadlock when stderr buffer fills
            // (same pattern as HealthCheckService.GetVersionAsync)
            var stderrTask = process.StandardError.ReadToEndAsync();
            var output = await process.StandardOutput.ReadToEndAsync();
            try { await stderrTask; } catch { }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0) return null;

            var version = ParseVersionString(output);
            if (version is not null)
                CurrentCliVersion = version;
            return version;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check npm registry for a newer CLI version.
    /// </summary>
    public async Task<CliVersionInfo?> CheckForUpdateAsync()
    {
        if (Interlocked.CompareExchange(ref _isChecking, 1, 0) != 0) return null;
        try
        {
            // Ensure we know the current version
            if (CurrentCliVersion is null)
            {
                await GetCurrentVersionAsync();
                if (CurrentCliVersion is null) return null;
            }

            var json = await Http.GetStringAsync(NpmRegistryUrl);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("version", out var versionEl))
                return null;

            var latestVersion = versionEl.GetString();
            if (latestVersion is null) return null;

            if (!UpdateService.IsNewerVersion(latestVersion, CurrentCliVersion))
                return null;

            if (BlacklistedVersions.Contains(latestVersion))
                return null;

            var info = new CliVersionInfo(CurrentCliVersion, latestVersion);
            OnCliUpdateAvailable?.Invoke(info);
            return info;
        }
        catch
        {
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _isChecking, 0);
        }
    }

    /// <summary>
    /// Start periodic update checks (first at 30s, then every 4 hours).
    /// </summary>
    public void StartPeriodicCheck()
    {
        _timer?.Dispose();
        _timer = new Timer(
            _ => _ = CheckForUpdateAsync(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(4));
    }

    public void StopPeriodicCheck()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Full update flow: backup → install → verify → rollback on failure.
    /// </summary>
    public async Task UpdateCliAsync(string expectedVersion)
    {
        try
        {
            // 1. Detect if npm-installed
            if (IsNpmInstalled())
            {
                OnCliUpdateFailed?.Invoke(
                    "Your CLI was installed via npm.\n" +
                    "Please update manually: npm update -g @anthropic-ai/claude-code");
                return;
            }

            // Fix: save old version before update, since GetCurrentVersionAsync overwrites CurrentCliVersion
            var previousVersion = CurrentCliVersion; // nullable — null means version was unknown

            // 2. Backup
            OnCliUpdateProgress?.Invoke("Backing up current CLI...");
            if (!BackupCurrentCli())
            {
                OnCliUpdateFailed?.Invoke("Failed to backup current CLI binary.");
                return;
            }
            OnCliUpdateProgress?.Invoke($"Backup created: {BackupPath}");

            // 3. Run official installer
            OnCliUpdateProgress?.Invoke("Running official installer (this may take a few minutes)...");
            var installOk = await RunInstallerAsync();

            if (!installOk)
            {
                OnCliUpdateProgress?.Invoke("Installer failed. Rolling back...");
                RollbackCli();
                WriteRollbackMarker(expectedVersion);
                OnCliUpdateFailed?.Invoke("CLI installer failed. Previous version restored.");
                return;
            }

            // 4. Verify
            OnCliUpdateProgress?.Invoke("Verifying update...");
            var newVersion = await GetCurrentVersionAsync();

            if (newVersion is null)
            {
                OnCliUpdateProgress?.Invoke("Verification failed — CLI not responding. Rolling back...");
                RollbackCli();
                WriteRollbackMarker(expectedVersion);
                OnCliUpdateFailed?.Invoke($"CLI update to v{expectedVersion} failed — could not verify. Previous version restored.");
                return;
            }

            // Fix: compare against previousVersion instead of CurrentCliVersion (which was already overwritten).
            // When previousVersion is null (was unknown), skip the "is newer" check — rely on expected version match.
            if (previousVersion is not null
                && !UpdateService.IsNewerVersion(newVersion, previousVersion)
                && newVersion != expectedVersion)
            {
                OnCliUpdateProgress?.Invoke($"Version mismatch: expected {expectedVersion}, got {newVersion}. Rolling back...");
                RollbackCli();
                WriteRollbackMarker(expectedVersion);
                OnCliUpdateFailed?.Invoke($"Version mismatch after update. Previous version restored.");
                return;
            }

            // 5. Success
            CurrentCliVersion = newVersion;
            CleanupBackup();
            OnCliUpdateProgress?.Invoke($"CLI updated to v{newVersion}");
            OnCliUpdateCompleted?.Invoke(newVersion);
        }
        catch (Exception ex)
        {
            RollbackCli();
            OnCliUpdateFailed?.Invoke($"Update error: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if a previous CLI update was rolled back. Returns the failed version, or null.
    /// </summary>
    public static string? CheckCliRollbackMarker()
    {
        try
        {
            if (!File.Exists(CliRollbackMarkerPath)) return null;
            var failedVersion = File.ReadAllText(CliRollbackMarkerPath).Trim();
            File.Delete(CliRollbackMarkerPath);
            return string.IsNullOrEmpty(failedVersion) ? null : failedVersion;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ParseVersionString(string output)
    {
        var match = VersionRegex.Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    private bool IsNpmInstalled()
    {
        var path = ExePath ?? "";
        return path.Contains("npm", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool BackupCurrentCli()
    {
        try
        {
            if (!File.Exists(NativePath)) return false;
            if (File.Exists(BackupPath))
                File.Delete(BackupPath);
            File.Copy(NativePath, BackupPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RollbackCli()
    {
        try
        {
            if (!File.Exists(BackupPath)) return;
            File.Copy(BackupPath, NativePath, overwrite: true);
            File.Delete(BackupPath);
        }
        catch
        {
            // Rollback failed — user will need to reinstall manually
        }
    }

    private static void CleanupBackup()
    {
        try
        {
            if (File.Exists(BackupPath))
                File.Delete(BackupPath);
        }
        catch { }
    }

    private static void WriteRollbackMarker(string version)
    {
        try
        {
            var dir = Path.GetDirectoryName(CliRollbackMarkerPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(CliRollbackMarkerPath, version);
        }
        catch { }
    }

    private async Task<bool> RunInstallerAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://claude.ai/install.ps1 | iex\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            OnCliUpdateProgress?.Invoke($"Installer started (PID: {process.Id})");

            // Forward output
            var outputTask = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                    OnCliUpdateProgress?.Invoke(line);
            });
            var errorTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                    OnCliUpdateProgress?.Invoke($"[ERR] {line}");
            });

            // Progress ticks
            var startTime = DateTime.Now;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var progressTask = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested && !process.HasExited)
                {
                    await Task.Delay(10_000, cts.Token).ConfigureAwait(false);
                    var elapsed = DateTime.Now - startTime;
                    OnCliUpdateProgress?.Invoke($"Installing... ({elapsed.Minutes}m {elapsed.Seconds}s elapsed)");
                }
            }, cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                OnCliUpdateProgress?.Invoke("Installer timed out after 15 minutes.");
                return false;
            }

            await cts.CancelAsync();
            try { await Task.WhenAll(outputTask, errorTask); } catch { }
            try { await progressTask; } catch { }

            OnCliUpdateProgress?.Invoke($"Installer exited with code {process.ExitCode}");

            // Verify binary exists
            return File.Exists(NativePath);
        }
        catch (Exception ex)
        {
            OnCliUpdateProgress?.Invoke($"Installer error: {ex.Message}");
            return false;
        }
    }
}
