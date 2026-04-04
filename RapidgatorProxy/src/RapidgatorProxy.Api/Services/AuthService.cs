using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using RapidgatorProxy.Api.Configuration;

namespace RapidgatorProxy.Api.Services;

public class AuthService
{
    private readonly SupabaseSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IOptions<SupabaseSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<AuthService> logger)
    {
        _settings = settings.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Validates a Supabase JWT by calling the Supabase Auth API.
    /// Returns (isValid, userId) tuple.
    /// </summary>
    public async Task<(bool isValid, string? userId)> ValidateTokenAsync(string jwt)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient("Supabase");
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_settings.Url}/auth/v1/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Headers.Add("apikey", _settings.Key);

            var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Supabase auth returned {StatusCode}", response.StatusCode);
                return (false, null);
            }

            var body = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(body);
            var userId = json["id"]?.ToString();

            return (userId != null, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Supabase auth validation failed");
            return (false, null);
        }
    }
}
