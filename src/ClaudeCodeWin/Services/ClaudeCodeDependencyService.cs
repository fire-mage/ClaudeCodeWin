using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Principal;
using System.Text.Json;

namespace ClaudeCodeWin.Services;

public record DependencyStatus(bool IsInstalled, string? ExePath);

public class ClaudeCodeDependencyService
{
    private static readonly string InstallLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeWin", "install.log");

    private static void WriteInstallLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InstallLogPath)!);
            File.AppendAllText(InstallLogPath, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
        }
        catch { }
    }

    // ── Admin / UAC ─────────────────────────────────────────────────

    /// <summary>
    /// Check if the current process is running with administrator privileges.
    /// </summary>
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Relaunch the current application with administrator privileges (UAC prompt).
    /// Returns true if the elevated process was started, false if user declined UAC.
    /// </summary>
    public static bool RequestElevation()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            // User declined UAC or other error
            return false;
        }
    }

    // ── Claude Code CLI ─────────────────────────────────────────────

    private static readonly string NativePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "bin", "claude.exe");

    /// <summary>
    /// Check if Claude Code CLI is available on the system.
    /// </summary>
    public async Task<DependencyStatus> CheckAsync()
    {
        // 1. Try running 'claude --version' via PATH
        var pathExe = await TryGetVersionAsync("claude");
        if (pathExe is not null)
            return new DependencyStatus(true, pathExe);

        // 2. Try the native install location directly
        if (File.Exists(NativePath))
        {
            var nativeOk = await TryGetVersionAsync(NativePath);
            if (nativeOk is not null)
                return new DependencyStatus(true, NativePath);
        }

        return new DependencyStatus(false, null);
    }

    /// <summary>
    /// Install Claude Code CLI using the official installer script from claude.ai.
    /// Runs: irm https://claude.ai/install.ps1 | iex
    /// </summary>
    public async Task<bool> InstallAsync(Action<string>? onProgress = null)
    {
        void Log(string msg)
        {
            onProgress?.Invoke(msg);
            WriteInstallLog(msg);
        }

        Log("Starting Claude Code CLI installation (official installer)...");

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
            Log($"Installer started (PID: {process.Id})");

            var outputTask = ReadStreamAsync(process.StandardOutput, msg => Log(msg));
            var errorTask = ReadStreamAsync(process.StandardError, msg => Log("[ERR] " + msg));

            // The official installer downloads ~222MB + runs 'claude install'.
            // On slow connections this can take 10+ minutes.
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                Log("Installer timed out after 15 minutes.");
            }

            await Task.WhenAll(outputTask, errorTask);

            if (process.HasExited)
                Log($"Installer exited with code {process.ExitCode}");

            // Verify installation
            Log("Verifying installation...");
            var nativeExists = File.Exists(NativePath);
            Log(nativeExists ? $"Found: {NativePath}" : $"NOT found: {NativePath}");

            var status = await CheckAsync();
            if (status.IsInstalled)
            {
                Log($"Claude Code CLI installed successfully at: {status.ExePath}");
                return true;
            }

            Log("Installation completed but claude CLI was not found.");
            return false;
        }
        catch (Exception ex)
        {
            Log($"Installation failed: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                Log($"  Inner: {ex.InnerException.Message}");
            WriteInstallLog(ex.StackTrace ?? "no stack trace");
            return false;
        }
    }

    private static async Task ReadStreamAsync(StreamReader reader, Action<string>? onProgress)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                // Strip ANSI escape codes from TUI output
                var clean = System.Text.RegularExpressions.Regex.Replace(
                    line, @"\x1B\[[^@-~]*[@-~]|\x1B\].*?\x07|\x1B\[[\?]?\d*[a-zA-Z]", "").Trim();
                if (!string.IsNullOrWhiteSpace(clean))
                    onProgress?.Invoke(clean);
            }
        }
    }

    /// <summary>
    /// Resolve the full path to the claude executable.
    /// </summary>
    public string? ResolveExePath()
    {
        if (File.Exists(NativePath))
            return NativePath;
        return null; // Will use "claude" from PATH
    }

    private static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

    /// <summary>
    /// Check if user is authenticated (has valid OAuth token).
    /// </summary>
    public bool IsAuthenticated()
    {
        try
        {
            if (!File.Exists(CredentialsPath))
                return false;

            var json = File.ReadAllText(CredentialsPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.TryGetProperty("accessToken", out var token)
                && !string.IsNullOrEmpty(token.GetString()))
            {
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Launch claude CLI in a visible terminal for interactive login.
    /// </summary>
    public async Task<bool> LaunchLoginAsync(string? claudeExePath = null)
    {
        var exe = claudeExePath ?? (File.Exists(NativePath) ? NativePath : "claude");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{exe}\" & pause\"",
                UseShellExecute = true,
                CreateNoWindow = false,
            };

            using var process = Process.Start(psi);
            if (process is null) return false;

            await process.WaitForExitAsync();

            return IsAuthenticated();
        }
        catch
        {
            return false;
        }
    }

    // ── Git for Windows (full installer, silent) ────────────────────

    /// <summary>
    /// Check if Git for Windows is installed (system PATH).
    /// </summary>
    public bool IsGitInstalled()
    {
        return TryRunExe("git", "--version");
    }

    /// <summary>
    /// Download and install Git for Windows via silent installer.
    /// Requires administrator privileges.
    /// </summary>
    public async Task<bool> InstallGitAsync(Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Fetching latest Git for Windows release...");

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "ClaudeCodeWin");
            http.Timeout = TimeSpan.FromMinutes(10);

            // Get latest release from GitHub API
            var apiUrl = "https://api.github.com/repos/git-for-windows/git/releases/latest";
            var releaseJson = await http.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(releaseJson);

            // Find Git-*-64-bit.exe or Git-*-arm64.exe installer
            var archSuffix = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64
                ? "-arm64.exe" : "-64-bit.exe";

            string? downloadUrl = null;
            string? assetName = null;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("Git-") && name.EndsWith(archSuffix))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    assetName = name;
                    break;
                }
            }

            if (downloadUrl is null)
            {
                onProgress?.Invoke("Could not find Git installer from GitHub releases.");
                return false;
            }

            var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "unknown";
            onProgress?.Invoke($"Found {assetName} ({tagName})");

            // Download
            var tempExe = Path.Combine(Path.GetTempPath(), assetName!);
            onProgress?.Invoke($"Downloading {assetName}...");

            using (var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? 0;

                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;
                    if (totalBytes > 0)
                        onProgress?.Invoke($"Downloading... {downloaded / (1024 * 1024)}MB / {totalBytes / (1024 * 1024)}MB");
                }
            }

            // Run silent installer
            onProgress?.Invoke("Running Git installer (silent)...");
            var psi = new ProcessStartInfo
            {
                FileName = tempExe,
                Arguments = "/VERYSILENT /NORESTART /NOCANCEL /SP- /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /COMPONENTS=\"icons,ext\\reg\\shellhere,assoc,assoc_sh\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                onProgress?.Invoke("Failed to start Git installer.");
                return false;
            }

            onProgress?.Invoke($"Installer running (PID: {process.Id})...");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                onProgress?.Invoke("Git installer timed out after 5 minutes.");
                return false;
            }

            onProgress?.Invoke($"Installer exited with code {process.ExitCode}");

            // Cleanup
            try { File.Delete(tempExe); } catch { }

            // Verify — Git installer adds to system PATH, but our process may not see it yet.
            // Check known install locations directly.
            var gitExe = FindGitExe();
            if (gitExe is not null)
            {
                onProgress?.Invoke($"Git installed successfully at: {gitExe}");
                return true;
            }

            onProgress?.Invoke("Installer completed but git.exe was not found.");
            return false;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Git installation failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Find git.exe in known installation locations (for when PATH hasn't been refreshed yet).
    /// </summary>
    public static string? FindGitExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
            @"C:\Program Files\Git\cmd\git.exe",
            @"C:\Program Files (x86)\Git\cmd\git.exe",
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    // ── GitHub CLI (gh) ─────────────────────────────────────────────

    private static readonly string GhCliDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeWin", "GhCli");

    private static readonly string GhCliExe = Path.Combine(GhCliDir, "bin", "gh.exe");

    /// <summary>
    /// Check if GitHub CLI is installed (local portable or system-wide).
    /// </summary>
    public bool IsGhInstalled()
    {
        if (File.Exists(GhCliExe) && TryRunExe(GhCliExe, "--version"))
            return true;
        return TryRunExe("gh", "--version");
    }

    /// <summary>
    /// Get the path to gh executable (local portable or system).
    /// </summary>
    public string? ResolveGhExePath()
    {
        if (File.Exists(GhCliExe))
            return GhCliExe;
        if (TryRunExe("gh", "--version"))
            return "gh";
        return null;
    }

    /// <summary>
    /// Download and install portable GitHub CLI to local AppData.
    /// </summary>
    public async Task<bool> InstallGhAsync(Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Fetching latest GitHub CLI release info...");

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "ClaudeCodeWin");
            http.Timeout = TimeSpan.FromMinutes(5);

            var apiUrl = "https://api.github.com/repos/cli/cli/releases/latest";
            var releaseJson = await http.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(releaseJson);

            var archSuffix = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64
                ? "_windows_arm64.zip" : "_windows_amd64.zip";

            string? downloadUrl = null;
            string? assetName = null;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("gh_") && name.EndsWith(archSuffix))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    assetName = name;
                    break;
                }
            }

            if (downloadUrl is null)
            {
                onProgress?.Invoke("Could not find GitHub CLI download URL from GitHub releases.");
                return false;
            }

            var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "unknown";
            onProgress?.Invoke($"Found {assetName} ({tagName})");

            // Download
            var tempZip = Path.Combine(Path.GetTempPath(), assetName!);
            onProgress?.Invoke($"Downloading {assetName}...");

            using (var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? 0;

                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;
                    if (totalBytes > 0)
                        onProgress?.Invoke($"Downloading... {downloaded / (1024 * 1024)}MB / {totalBytes / (1024 * 1024)}MB");
                }
            }

            onProgress?.Invoke("Extracting GitHub CLI...");

            if (Directory.Exists(GhCliDir))
                Directory.Delete(GhCliDir, true);

            var tempExtract = Path.Combine(Path.GetTempPath(), "gh_extract_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempExtract);
            ZipFile.ExtractToDirectory(tempZip, tempExtract);

            var subDirs = Directory.GetDirectories(tempExtract);
            if (subDirs.Length == 1)
                Directory.Move(subDirs[0], GhCliDir);
            else
                Directory.Move(tempExtract, GhCliDir);

            try { File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); } catch { }

            if (File.Exists(GhCliExe) && TryRunExe(GhCliExe, "--version"))
            {
                onProgress?.Invoke("GitHub CLI installed successfully!");
                return true;
            }

            onProgress?.Invoke("Extraction completed but gh.exe was not found.");
            onProgress?.Invoke($"Expected at: {GhCliExe}");
            return false;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"GitHub CLI installation failed: {ex.Message}");
            return false;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static bool TryRunExe(string exePath, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> TryGetVersionAsync(string exePath)
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

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return null;
            }

            return process.ExitCode == 0 ? exePath : null;
        }
        catch
        {
            return null;
        }
    }

}
