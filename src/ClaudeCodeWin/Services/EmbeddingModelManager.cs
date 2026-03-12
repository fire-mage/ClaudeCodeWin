using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Manages the ONNX embedding model (all-MiniLM-L6-v2) for vector memory.
/// Model stored in %LocalAppData%/ClaudeCodeWin/models/ (not AppData — avoids OneDrive sync).
/// </summary>
public static class EmbeddingModelManager
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(30) };

    private static readonly string ModelsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeWin", "models");

    private const string ModelFileName = "embedding-minilm-v2.onnx";
    private const string TokenizerFileName = "embedding-tokenizer.json";

    private const string ModelUrl =
        "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
    private const string TokenizerUrl =
        "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/tokenizer.json";

    private const long ModelApproxBytes = 86_000_000;    // ~82 MB
    private const long TokenizerApproxBytes = 800_000;   // ~780 KB

    // SHA256 hashes for integrity verification (null = skip check, e.g. tokenizer varies by version)
    private const string? ModelSha256 = null; // Set after first verified download
    private const string? TokenizerSha256 = null;

    public static string GetModelPath() => Path.Combine(ModelsDir, ModelFileName);
    public static string GetTokenizerPath() => Path.Combine(ModelsDir, TokenizerFileName);
    public static long GetTotalApproxBytes() => ModelApproxBytes + TokenizerApproxBytes;

    public static bool IsModelDownloaded() =>
        File.Exists(GetModelPath()) && File.Exists(GetTokenizerPath());

    /// <summary>
    /// Check if there is enough free memory to load the ONNX model (~200 MB working set).
    /// </summary>
    public static bool HasSufficientMemory(long minFreeBytes = 500_000_000)
    {
        try
        {
            var memInfo = GC.GetGCMemoryInfo();
            return memInfo.TotalAvailableMemoryBytes > minFreeBytes;
        }
        catch
        {
            return true; // Assume OK if we can't check
        }
    }

    /// <summary>
    /// Download the ONNX model and tokenizer with progress reporting.
    /// Progress reports combined bytes for both files.
    /// Returns true on success.
    /// </summary>
    public static async Task<bool> DownloadAsync(
        Action<long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelsDir);
        var totalBytes = ModelApproxBytes + TokenizerApproxBytes;
        long totalDownloaded = 0;

        // Download tokenizer first (small file)
        var tokenizerPath = GetTokenizerPath();
        if (!File.Exists(tokenizerPath))
        {
            var success = await DownloadFileAsync(
                TokenizerUrl, tokenizerPath, TokenizerApproxBytes, TokenizerSha256,
                (downloaded, _) =>
                {
                    totalDownloaded = downloaded;
                    onProgress?.Invoke(totalDownloaded, totalBytes);
                }, ct);
            if (!success) return false;
        }
        totalDownloaded = TokenizerApproxBytes;
        onProgress?.Invoke(totalDownloaded, totalBytes);

        // Download model (large file)
        var modelPath = GetModelPath();
        if (!File.Exists(modelPath))
        {
            var success = await DownloadFileAsync(
                ModelUrl, modelPath, ModelApproxBytes, ModelSha256,
                (downloaded, _) =>
                {
                    onProgress?.Invoke(totalDownloaded + downloaded, totalBytes);
                }, ct);
            if (!success) return false;
        }

        onProgress?.Invoke(totalBytes, totalBytes);
        return true;
    }

    public static void DeleteModel()
    {
        TryDeleteFile(GetModelPath());
        TryDeleteFile(GetTokenizerPath());
    }

    private static async Task<bool> DownloadFileAsync(
        string url, string targetPath, long approxBytes, string? expectedSha256,
        Action<long, long>? onProgress,
        CancellationToken ct)
    {
        var tempPath = targetPath + ".tmp";
        try
        {
            using var response = await SharedHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength ?? approxBytes;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

            var buffer = new byte[81920];
            long downloaded = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                onProgress?.Invoke(downloaded, contentLength);
            }

            await fileStream.FlushAsync(ct);
            fileStream.Close();

            // Verify integrity if hash is known
            if (expectedSha256 != null)
            {
                await using var hashStream = File.OpenRead(tempPath);
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, ct));
                if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    hashStream.Close();
                    TryDeleteFile(tempPath);
                    return false;
                }
            }

            // Atomic rename
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

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
