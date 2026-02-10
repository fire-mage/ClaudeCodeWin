using System.Diagnostics;
using System.IO;
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
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://claude.ai/install.ps1 | iex\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Stream output
            var outputTask = ReadStreamAsync(process.StandardOutput, onProgress);
            var errorTask = ReadStreamAsync(process.StandardError, onProgress);

            // Wait up to 5 minutes
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                onProgress?.Invoke("Installation timed out.");
                return false;
            }

            await Task.WhenAll(outputTask, errorTask);

            if (process.ExitCode != 0)
            {
                onProgress?.Invoke($"Installer exited with code {process.ExitCode}.");
                return false;
            }

            // Verify installation
            onProgress?.Invoke("Verifying installation...");
            var status = await CheckAsync();
            if (status.IsInstalled)
            {
                onProgress?.Invoke("Claude Code CLI installed successfully!");
                return true;
            }

            onProgress?.Invoke("Installation completed but claude CLI was not found. You may need to restart the application.");
            return false;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Installation failed: {ex.Message}");
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

    /// <summary>
    /// Check if Git for Windows is installed (required by Claude Code CLI on native Windows).
    /// </summary>
    public bool IsGitInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
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
