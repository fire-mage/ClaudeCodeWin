using System.Diagnostics;
using System.IO;
using System.Text;

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
