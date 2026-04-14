using System.Text.Json;

namespace KJSWeb.Services;

public class ExeIoService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    private readonly string _apiBaseUrl;
    private readonly ILogger<ExeIoService> _logger;

    public ExeIoService(IConfiguration config, IHttpClientFactory httpClientFactory, ILogger<ExeIoService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = config["ExeIo:ApiKey"] ?? "";
        _apiBaseUrl = config["ExeIo:ApiBaseUrl"] ?? "https://exe.io/api";
        _logger = logger;
    }

    /// <summary>
    /// Registers the given URL with exe.io and returns the shortened monetization link.
    /// Returns null if the API call fails or no API key is configured.
    /// </summary>
    public async Task<string?> GenerateLinkAsync(string destinationUrl)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogWarning("ExeIo:ApiKey is not configured. Skipping link generation.");
            return null;
        }

        try
        {
            using var http = _httpClientFactory.CreateClient();
            var requestUrl = $"{_apiBaseUrl}?api={Uri.EscapeDataString(_apiKey)}&url={Uri.EscapeDataString(destinationUrl)}";
            var response = await http.GetAsync(requestUrl);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("exe.io API returned {StatusCode} for URL: {Url}", response.StatusCode, destinationUrl);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.TryGetProperty("status", out var statusProp)
                ? GetStringValue(statusProp) : null;

            if (status == "success" &&
                doc.RootElement.TryGetProperty("shortenedUrl", out var urlProp))
            {
                return GetStringValue(urlProp);
            }

            // API returned {"status":"error","message":"..."}
            var message = doc.RootElement.TryGetProperty("message", out var msgProp)
                ? GetStringValue(msgProp) : json;
            _logger.LogWarning("exe.io API error for {Url}: {Message}", destinationUrl, message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate exe.io link for: {Url}", destinationUrl);
            return null;
        }
    }

    // exe.io API returns values as either plain strings or single-element arrays
    private static string? GetStringValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Array  => el.EnumerateArray().FirstOrDefault().GetString(),
        JsonValueKind.String => el.GetString(),
        _                    => el.GetRawText()
    };
}
