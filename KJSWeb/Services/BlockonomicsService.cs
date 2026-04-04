using System.Net.Http.Headers;
using System.Text.Json;

namespace KJSWeb.Services;

public class BlockonomicsService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _callbackSecret;

    public BlockonomicsService(IConfiguration config)
    {
        _apiKey = config["Blockonomics:ApiKey"]!;
        _callbackSecret = config["Blockonomics:CallbackSecret"] ?? "";
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://www.blockonomics.co/")
        };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    /// <summary>
    /// Generate a new unique BTC address for receiving payment.
    /// POST https://www.blockonomics.co/api/new_address
    /// </summary>
    public async Task<string?> GetNewAddressAsync()
    {
        var response = await _http.PostAsync("api/new_address", null);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Blockonomics new_address error: {response.StatusCode} - {error}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("address").GetString();
    }

    /// <summary>
    /// Get current BTC price in USD.
    /// GET https://www.blockonomics.co/api/price?currency=USD
    /// </summary>
    public async Task<decimal> GetBtcPriceAsync()
    {
        var response = await _http.GetAsync("api/price?currency=USD");
        if (!response.IsSuccessStatusCode) return 0;

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("price").GetDecimal();
    }

    /// <summary>Convert USD amount to BTC based on current price.</summary>
    public decimal ConvertUsdToBtc(decimal usd, decimal btcPrice)
    {
        if (btcPrice <= 0) return 0;
        return Math.Round(usd / btcPrice, 8);
    }

    /// <summary>Validate callback secret.</summary>
    public bool ValidateSecret(string? secret)
    {
        if (string.IsNullOrEmpty(_callbackSecret)) return true;
        return secret == _callbackSecret;
    }
}
