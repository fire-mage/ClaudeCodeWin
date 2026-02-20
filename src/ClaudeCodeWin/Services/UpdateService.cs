using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace ClaudeCodeWin.Services;

public record VersionInfo(
    string Version,
    string DownloadUrl,
    string Sha256,
    string? ReleaseNotes
);

public record VersionManifest(
    string Version,
    Dictionary<string, VersionAsset> Assets,
    string? ReleaseNotes,
    string? PublishedAt
);

public record VersionAsset(
    string Url,
    string Sha256
);

public class UpdateService
{
    private const string BaseUrl = "https://mainfish.s3.eu-central-1.amazonaws.com/admin/claudecodewin";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private System.Threading.Timer? _timer;

    public event Action<VersionInfo>? OnUpdateAvailable;
    public event Action<int>? OnDownloadProgress; // percentage 0-100
    public event Action<string>? OnUpdateReady; // path to downloaded file
    public event Action<string>? OnError;

    public string CurrentVersion { get; }

    /// <summary>"stable" or "beta"</summary>
    public string UpdateChannel { get; set; } = "stable";

    private string VersionUrl => UpdateChannel == "beta"
        ? $"{BaseUrl}/version-beta.json"
        : $"{BaseUrl}/version.json";

    public UpdateService()
    {
        CurrentVersion = typeof(UpdateService).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
    }

    public void StartPeriodicCheck()
    {
        // Check once at startup (after 5 second delay), then every 4 hours
        _timer = new System.Threading.Timer(
            _ => _ = CheckForUpdateAsync(),
            null,
            TimeSpan.FromSeconds(5),
            CheckInterval);
    }

    public void StopPeriodicCheck()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public async Task<VersionInfo?> CheckForUpdateAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(VersionUrl).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<VersionManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (manifest is null) return null;

            if (!IsNewerVersion(manifest.Version, CurrentVersion))
                return null;

            var archKey = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "win-arm64"
                : "win-x64";

            if (!manifest.Assets.TryGetValue(archKey, out var asset))
                return null;

            var info = new VersionInfo(manifest.Version, asset.Url, asset.Sha256, manifest.ReleaseNotes);
            OnUpdateAvailable?.Invoke(info);
            return info;
        }
        catch
        {
            // Silent — don't bother user if update check fails
            return null;
        }
    }

    public async Task DownloadAndApplyAsync(VersionInfo info)
    {
        try
        {
            var updatesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeCodeWin", "updates");
            Directory.CreateDirectory(updatesDir);

            var fileName = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath);
            var downloadPath = Path.Combine(updatesDir, fileName);

            // Download with progress
            using var response = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var fileStream = File.Create(downloadPath);

            var buffer = new byte[81920];
            long totalRead = 0;
            int lastReportedPercent = -1;

            while (true)
            {
                var read = await contentStream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) break;

                await fileStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                totalRead += read;

                if (totalBytes > 0)
                {
                    var percent = (int)(totalRead * 100 / totalBytes);
                    if (percent != lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        OnDownloadProgress?.Invoke(percent);
                    }
                }
            }

            await fileStream.FlushAsync().ConfigureAwait(false);
            fileStream.Close();

            // Verify SHA256 (skip if publisher did not provide a hash)
            if (!string.IsNullOrEmpty(info.Sha256))
            {
                var hash = await ComputeSha256Async(downloadPath).ConfigureAwait(false);
                if (!string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(downloadPath);
                    OnError?.Invoke("SHA256 mismatch — download corrupted. Please try again.");
                    return;
                }
            }

            OnUpdateReady?.Invoke(downloadPath);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Download failed: {ex.Message}");
        }
    }

    public static void ApplyUpdate(string downloadedExePath)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe)) return;

        var pid = Environment.ProcessId;
        var updatesDir = Path.GetDirectoryName(downloadedExePath)!;
        var cmdPath = Path.Combine(updatesDir, "update.cmd");

        // Write update script
        var script = $"""
            @echo off
            :wait
            tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto wait
            )
            copy /Y "{downloadedExePath}" "{currentExe}" >nul
            if errorlevel 1 (
                echo Update failed. Press any key to exit.
                pause >nul
                exit /b 1
            )
            REM Flush Windows icon cache so the new embedded icon appears immediately
            ie4uinit.exe -show >nul 2>nul
            REM Touch shortcut files to force Explorer to refresh cached icons
            for %%s in (
                "%USERPROFILE%\Desktop\ClaudeCodeWin.lnk"
                "%APPDATA%\Microsoft\Windows\Start Menu\Programs\ClaudeCodeWin\ClaudeCodeWin.lnk"
                "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\ClaudeCodeWin.lnk"
            ) do (
                if exist %%s copy /b %%s+,, %%s >nul 2>nul
            )
            start "" "{currentExe}"
            del "{downloadedExePath}" >nul 2>nul
            del "%~f0" >nul 2>nul
            """;

        File.WriteAllText(cmdPath, script);

        // Launch update script and exit
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{cmdPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });

        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexStringLower(hashBytes);
    }

    internal static bool IsNewerVersion(string remote, string local)
    {
        if (Version.TryParse(remote, out var remoteVer) && Version.TryParse(local, out var localVer))
            return remoteVer > localVer;
        return false;
    }
}
