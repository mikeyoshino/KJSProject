using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using RapidgatorProxy.Api.Configuration;

namespace RapidgatorProxy.Api.Services;

public class RapidgatorApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RapidgatorSettings _settings;
    private readonly ILogger<RapidgatorApiClient> _logger;
    private string? _sessionId;
    private DateTime _sessionExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    public RapidgatorApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<RapidgatorSettings> settings,
        ILogger<RapidgatorApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    private HttpClient CreateClient() => _httpClientFactory.CreateClient("RapidgatorProxy");

    public async Task<string> GetSessionIdAsync(CancellationToken ct = default)
    {
        if (_sessionId != null && DateTime.UtcNow < _sessionExpiry)
            return _sessionId;

        await _loginLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_sessionId != null && DateTime.UtcNow < _sessionExpiry)
                return _sessionId;

            var url = $"{_settings.ApiBaseUrl}/user/login?login={Uri.EscapeDataString(_settings.Username)}&password={Uri.EscapeDataString(_settings.Password)}";

            using var client = CreateClient();
            var response = await client.GetStringAsync(url, ct);
            var json = JObject.Parse(response);

            var sid = json["response"]?["session_id"]?.ToString();
            if (string.IsNullOrEmpty(sid))
                throw new InvalidOperationException($"Rapidgator login failed: {response}");

            _sessionId = sid;
            _sessionExpiry = DateTime.UtcNow.AddMinutes(50);
            _logger.LogInformation("Rapidgator login successful, session valid until {Expiry}", _sessionExpiry);
            return _sessionId;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async Task<(string downloadUrl, string fileName, long fileSize)> GetDownloadLinkAsync(
        string rapidgatorUrl, CancellationToken ct = default)
    {
        var sessionId = await GetSessionIdAsync(ct);
        var url = $"{_settings.ApiBaseUrl}/file/download?sid={sessionId}&url={Uri.EscapeDataString(rapidgatorUrl)}";

        using var client = CreateClient();
        var response = await client.GetStringAsync(url, ct);
        var json = JObject.Parse(response);

        var downloadUrl = json["response"]?["download_url"]?.ToString()
            ?? throw new InvalidOperationException($"No download URL in response: {response}");
        var fileName = json["response"]?["filename"]?.ToString() ?? "unknown";
        var fileSize = json["response"]?["file_size"]?.Value<long>() ?? 0;

        return (downloadUrl, fileName, fileSize);
    }

    public HttpClient CreateDownloadClient() => CreateClient();
}
