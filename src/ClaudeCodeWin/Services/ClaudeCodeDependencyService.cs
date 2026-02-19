using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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

    private const string GcsBucket = "https://storage.googleapis.com/claude-code-dist-86c565f3-f756-42ad-8dfa-d59b1c096819/claude-code-releases";

    /// <summary>
    /// Install Claude Code CLI by downloading the native binary directly (no PowerShell).
    /// Mirrors the logic of the official install.ps1 but with full control and progress reporting.
    /// </summary>
    public async Task<bool> InstallAsync(Action<string>? onProgress = null)
    {
        void Log(string msg)
        {
            onProgress?.Invoke(msg);
            WriteInstallLog(msg);
        }

        Log("Starting Claude Code CLI installation...");

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "ClaudeCodeWin");
            http.Timeout = TimeSpan.FromMinutes(5);

            var platform = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64
                ? "win32-arm64" : "win32-x64";

            // Step 1: Get latest version
            Log("Fetching latest version...");
            var version = (await http.GetStringAsync($"{GcsBucket}/latest")).Trim();
            Log($"Latest version: {version}");

            // Step 2: Get manifest and checksum
            Log("Fetching manifest...");
            var manifestJson = await http.GetStringAsync($"{GcsBucket}/{version}/manifest.json");
            using var manifest = JsonDocument.Parse(manifestJson);
            var checksum = manifest.RootElement
                .GetProperty("platforms")
                .GetProperty(platform)
                .GetProperty("checksum")
                .GetString();

            if (string.IsNullOrEmpty(checksum))
            {
                Log($"Platform {platform} not found in manifest.");
                return false;
            }
            Log($"Expected checksum: {checksum[..16]}...");

            // Step 3: Download binary
            var downloadDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "downloads");
            Directory.CreateDirectory(downloadDir);

            var binaryPath = Path.Combine(downloadDir, $"claude-{version}-{platform}.exe");
            var binaryUrl = $"{GcsBucket}/{version}/{platform}/claude.exe";
            Log($"Downloading claude.exe ({platform}) from {binaryUrl}");

            using (var response = await http.GetAsync(binaryUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                Log($"Download started, size: {totalBytes / (1024 * 1024)}MB");

                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(binaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

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

            Log($"Download complete. File exists: {File.Exists(binaryPath)}, Size: {new FileInfo(binaryPath).Length}");

            // Step 4: Verify checksum
            Log("Verifying checksum...");
            var actualChecksum = await ComputeSha256Async(binaryPath);
            if (!string.Equals(actualChecksum, checksum, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Checksum mismatch! Expected: {checksum}, Got: {actualChecksum}");
                try { File.Delete(binaryPath); } catch { }
                return false;
            }
            Log("Checksum verified OK.");

            // Step 5: Run 'claude install' to set up launcher and shell integration
            Log($"Running: {binaryPath} install latest");
            var psi = new ProcessStartInfo
            {
                FileName = binaryPath,
                Arguments = "install latest",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // Claude Code requires git-bash — point it to our MinGit's bash.exe
            var localBash = Path.Combine(MinGitDir, "usr", "bin", "bash.exe");
            if (File.Exists(localBash))
            {
                psi.Environment["CLAUDE_CODE_GIT_BASH_PATH"] = localBash;
                Log($"Set CLAUDE_CODE_GIT_BASH_PATH={localBash}");
            }

            // Add MinGit to PATH for the install process
            var minGitCmd = Path.Combine(MinGitDir, "cmd");
            var minGitUsrBin = Path.Combine(MinGitDir, "usr", "bin");
            if (Directory.Exists(minGitCmd))
            {
                var currentPath = psi.Environment["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? "";
                psi.Environment["PATH"] = minGitCmd + ";" + minGitUsrBin + ";" + currentPath;
            }

            using var process = new Process { StartInfo = psi };
            process.Start();
            Log($"Installer process started (PID: {process.Id})");

            var outputTask = ReadStreamAsync(process.StandardOutput, msg => Log(msg));
            var errorTask = ReadStreamAsync(process.StandardError, msg => Log("[ERR] " + msg));

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                Log("Installer timed out after 2 minutes.");
                Log("Checking if binary was installed despite timeout...");
            }

            await Task.WhenAll(outputTask, errorTask);

            if (process.HasExited)
                Log($"Installer exited with code {process.ExitCode}");

            // Cleanup downloaded binary
            try { File.Delete(binaryPath); } catch { }

            // Step 6: Verify installation
            Log("Verifying installation...");
            var nativeExists = File.Exists(NativePath);
            Log(nativeExists ? $"Found: {NativePath}" : $"NOT found: {NativePath}");

            if (nativeExists)
            {
                var ok = await TryGetVersionAsync(NativePath);
                Log(ok is not null ? "Binary works!" : "Binary exists but failed to run");
            }

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

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
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

            // Create bash.exe from sh.exe (MinGit ships sh.exe which IS bash, just named differently)
            // Claude Code CLI requires bash.exe / git-bash for its install and runtime
            var shExe = Path.Combine(MinGitDir, "usr", "bin", "sh.exe");
            var bashExe = Path.Combine(MinGitDir, "usr", "bin", "bash.exe");
            if (File.Exists(shExe) && !File.Exists(bashExe))
            {
                File.Copy(shExe, bashExe);
                onProgress?.Invoke("Created bash.exe from sh.exe");
            }

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

    // ── GitHub CLI (gh) ──────────────────────────────────────────────

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
    /// Uses GitHub API to get the latest release dynamically.
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

            // Find gh_*_windows_amd64.zip (or arm64)
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

            // Clean old GhCli if exists
            if (Directory.Exists(GhCliDir))
                Directory.Delete(GhCliDir, true);

            // gh zip has a subfolder like "gh_2.87.0_windows_amd64/" — extract to temp, then move contents
            var tempExtract = Path.Combine(Path.GetTempPath(), "gh_extract_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempExtract);
            ZipFile.ExtractToDirectory(tempZip, tempExtract);

            // Find the single subfolder (e.g. gh_2.87.0_windows_amd64)
            var subDirs = Directory.GetDirectories(tempExtract);
            if (subDirs.Length == 1)
            {
                Directory.Move(subDirs[0], GhCliDir);
            }
            else
            {
                // Fallback: move everything
                Directory.Move(tempExtract, GhCliDir);
            }

            // Cleanup
            try { File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); } catch { }

            // Verify
            if (File.Exists(GhCliExe) && TryRunExe(GhCliExe, "--version"))
            {
                onProgress?.Invoke("GitHub CLI installed successfully!");
                return true;
            }

            onProgress?.Invoke("Extraction completed but gh.exe was not found.");
            onProgress?.Invoke($"Expected at: {GhCliExe}");

            // Debug: list what's in the directory
            if (Directory.Exists(GhCliDir))
            {
                onProgress?.Invoke($"Contents of {GhCliDir}:");
                foreach (var f in Directory.GetFiles(GhCliDir, "*", SearchOption.AllDirectories))
                    onProgress?.Invoke($"  {f}");
            }

            return false;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"GitHub CLI installation failed: {ex.Message}");
            return false;
        }
    }

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
