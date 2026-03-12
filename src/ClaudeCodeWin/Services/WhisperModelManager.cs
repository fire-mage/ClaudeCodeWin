using System.IO;
using System.Net.Http;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Manages Whisper GGML model files: download, verify, delete.
/// Models stored in %AppData%/ClaudeCodeWin/models/
/// </summary>
public class WhisperModelManager
{
    private static readonly string ModelsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin", "models");

    // HuggingFace URLs for ggml models
    private static readonly Dictionary<string, (string Url, long ApproxBytes)> Models = new()
    {
        ["tiny"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin", 75_000_000),
        ["base"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin", 148_000_000),
        ["small"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin", 488_000_000),
    };

    public static string[] AvailableModels => [.. Models.Keys];

    public static string GetModelDisplayName(string model) => model switch
    {
        "tiny" => "Tiny (~75 MB) — fast, basic quality",
        "base" => "Base (~150 MB) — good balance",
        "small" => "Small (~500 MB) — best quality",
        _ => model
    };

    public static long GetModelApproxSize(string model) =>
        Models.TryGetValue(model, out var info) ? info.ApproxBytes : 0;

    public static string GetModelPath(string model) =>
        Path.Combine(ModelsDir, $"ggml-{model}.bin");

    public static bool IsModelDownloaded(string model) =>
        File.Exists(GetModelPath(model));

    /// <summary>
    /// Download a model with progress reporting.
    /// Returns true on success.
    /// </summary>
    public static async Task<bool> DownloadModelAsync(
        string model,
        Action<long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!Models.TryGetValue(model, out var info))
            return false;

        Directory.CreateDirectory(ModelsDir);
        var targetPath = GetModelPath(model);
        var tempPath = targetPath + ".tmp";

        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(30);

            using var response = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? info.ApproxBytes;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

            var buffer = new byte[81920];
            long downloaded = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                onProgress?.Invoke(downloaded, totalBytes);
            }

            await fileStream.FlushAsync(ct);
            fileStream.Close();

            // Rename temp to final
            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tempPath, targetPath);

            return true;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(tempPath);
            return false;
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    public static void DeleteModel(string model)
    {
        var path = GetModelPath(model);
        TryDeleteFile(path);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
