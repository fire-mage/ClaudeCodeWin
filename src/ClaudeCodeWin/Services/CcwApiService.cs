using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeCodeWin.Services;

public class CcwApiService
{
    private const string BaseUrl = "https://admin.qr4k.com/api/ccw";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<(bool success, string? error)> SubmitFeatureRequestAsync(string email, string description, string appVersion)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                email,
                description,
                app_version = appVersion
            });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"{BaseUrl}/feature-requests", content).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return (true, null);

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var error = TryExtractError(body) ?? $"Server error: {(int)response.StatusCode}";
            return (false, error);
        }
        catch (Exception ex)
        {
            return (false, $"Connection error: {ex.Message}");
        }
    }

    public async Task<(ActivationResult? result, string? error)> ActivateCodeAsync(string code)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { code });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"{BaseUrl}/activate", content).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var result = JsonSerializer.Deserialize<ActivationResult>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return (result, null);
            }

            var error = TryExtractError(body) ?? $"Server error: {(int)response.StatusCode}";
            return (null, error);
        }
        catch (Exception ex)
        {
            return (null, $"Connection error: {ex.Message}");
        }
    }

    private static string? TryExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var errorProp))
                return errorProp.GetString();
        }
        catch { }
        return null;
    }
}

public class ActivationResult
{
    [JsonPropertyName("features")]
    public List<string> Features { get; set; } = [];

    [JsonPropertyName("valid_until")]
    public DateTime ValidUntil { get; set; }
}
