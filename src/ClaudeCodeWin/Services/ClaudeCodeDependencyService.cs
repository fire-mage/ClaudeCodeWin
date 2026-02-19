using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ClaudeCodeWin.Services;

public record DependencyStatus(bool IsInstalled, string? ExePath);

public class ClaudeCodeDependencyService
{
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
    /// Install Claude Code CLI via the official Windows installer script.
    /// </summary>
    public async Task<bool> InstallAsync(Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Starting Claude Code CLI installation...");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; irm https://claude.ai/install.ps1 | iex\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            onProgress?.Invoke("PowerShell process started (PID: " + process.Id + ")");

            // Stream output — stderr prefixed with [ERR] for visibility
            var outputTask = ReadStreamAsync(process.StandardOutput, onProgress);
            var errorTask = ReadStreamAsync(process.StandardError, msg => onProgress?.Invoke("[ERR] " + msg));

            // Wait up to 5 minutes
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                onProgress?.Invoke("Installation timed out after 5 minutes.");
                return false;
            }

            await Task.WhenAll(outputTask, errorTask);

            onProgress?.Invoke($"PowerShell exited with code {process.ExitCode}");

            if (process.ExitCode != 0)
            {
                onProgress?.Invoke($"Installer FAILED (exit code {process.ExitCode}).");
                return false;
            }

            // Verify installation
            onProgress?.Invoke("Verifying installation...");
            onProgress?.Invoke($"Checking PATH for 'claude'...");
            var pathExe = await TryGetVersionAsync("claude");
            onProgress?.Invoke(pathExe is not null ? $"Found in PATH: {pathExe}" : "Not found in PATH");

            onProgress?.Invoke($"Checking native location: {NativePath}");
            var nativeExists = File.Exists(NativePath);
            onProgress?.Invoke(nativeExists ? "Native binary exists" : "Native binary NOT found");

            if (nativeExists)
            {
                var nativeOk = await TryGetVersionAsync(NativePath);
                onProgress?.Invoke(nativeOk is not null ? "Native binary works!" : "Native binary exists but failed to run");
            }

            var status = await CheckAsync();
            if (status.IsInstalled)
            {
                onProgress?.Invoke($"Claude Code CLI installed successfully at: {status.ExePath}");
                return true;
            }

            onProgress?.Invoke("Installation completed but claude CLI was not found anywhere.");
            onProgress?.Invoke("Expected locations:");
            onProgress?.Invoke($"  PATH: claude");
            onProgress?.Invoke($"  Native: {NativePath}");
            return false;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Installation failed: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                onProgress?.Invoke($"  Inner: {ex.InnerException.Message}");
            return false;
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
    /// The CLI will open a browser for OAuth authentication.
    /// Returns when the terminal process exits.
    /// </summary>
    public async Task<bool> LaunchLoginAsync(string? claudeExePath = null)
    {
        var exe = claudeExePath ?? (File.Exists(NativePath) ? NativePath : "claude");

        try
        {
            // Launch claude in a visible cmd window — it will show TUI and open browser for OAuth
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

    private static readonly string MinGitDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeWin", "MinGit");

    private static readonly string MinGitExe = Path.Combine(MinGitDir, "cmd", "git.exe");

    /// <summary>
    /// Check if Git for Windows is installed (system-wide or local MinGit).
    /// </summary>
    public bool IsGitInstalled()
    {
        // 1. Check local MinGit first
        if (File.Exists(MinGitExe) && TryRunGit(MinGitExe))
            return true;

        // 2. Check system PATH
        return TryRunGit("git");
    }

    /// <summary>
    /// Get the path to git executable (local MinGit or system).
    /// </summary>
    public string? ResolveGitExePath()
    {
        if (File.Exists(MinGitExe))
            return MinGitExe;
        if (TryRunGit("git"))
            return "git";
        return null;
    }

    /// <summary>
    /// Download and install MinGit (portable Git for Windows) to local AppData.
    /// Uses GitHub API to get the latest release dynamically.
    /// </summary>
    public async Task<bool> InstallGitAsync(Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Fetching latest MinGit release info...");

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "ClaudeCodeWin");
            http.Timeout = TimeSpan.FromMinutes(5);

            // Get latest release from GitHub API
            var apiUrl = "https://api.github.com/repos/git-for-windows/git/releases/latest";
            var releaseJson = await http.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(releaseJson);

            // Find MinGit-*-64-bit.zip asset
            var suffix = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64
                ? "MinGit-" : "MinGit-";
            var archSuffix = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64
                ? "-arm64.zip" : "-64-bit.zip";

            string? downloadUrl = null;
            string? assetName = null;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("MinGit-") && name.EndsWith(archSuffix) && !name.Contains("busybox"))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    assetName = name;
                    break;
                }
            }

            if (downloadUrl is null)
            {
                onProgress?.Invoke("Could not find MinGit download URL from GitHub releases.");
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

            onProgress?.Invoke("Extracting MinGit...");

            // Clean old MinGit if exists
            if (Directory.Exists(MinGitDir))
                Directory.Delete(MinGitDir, true);

            Directory.CreateDirectory(MinGitDir);
            ZipFile.ExtractToDirectory(tempZip, MinGitDir);

            // Cleanup temp zip
            try { File.Delete(tempZip); } catch { }

            // Verify
            if (File.Exists(MinGitExe) && TryRunGit(MinGitExe))
            {
                onProgress?.Invoke("Git installed successfully!");
                return true;
            }

            onProgress?.Invoke("Extraction completed but git.exe was not found.");
            return false;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Git installation failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryRunGit(string gitPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = gitPath,
                Arguments = "--version",
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

    private static async Task ReadStreamAsync(StreamReader reader, Action<string>? onProgress)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                onProgress?.Invoke(line);
        }
    }
}
