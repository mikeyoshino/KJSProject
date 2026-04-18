using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KJSWeb.Services;

public class CryptomusService
{
    private readonly string _merchantId;
    private readonly string _paymentApiKey;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CryptomusService> _logger;

    private const string ApiBase = "https://api.cryptomus.com/v1";

    public CryptomusService(IConfiguration config, IHttpClientFactory httpClientFactory, ILogger<CryptomusService> logger)
    {
        _merchantId = config["Cryptomus:MerchantId"] ?? "";
        _paymentApiKey = config["Cryptomus:PaymentApiKey"] ?? "";
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Creates a Cryptomus payment invoice.
    /// Returns (uuid, paymentUrl) on success, null on failure.
    /// </summary>
    public async Task<(string Uuid, string PaymentUrl)?> CreateInvoiceAsync(
        decimal amount,
        string orderId,
        string callbackUrl,
        string successUrl)
    {
        var body = new
        {
            amount = amount.ToString("0.00"),
            currency = "USD",
            order_id = orderId,
            url_callback = callbackUrl,
            url_success = successUrl,
            is_payment_multiple = false,
            lifetime = 3600
        };

        var json = JsonSerializer.Serialize(body);
        var sign = ComputeSign(json);

        using var http = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/payment");
        request.Headers.Add("merchant", _merchantId);
        request.Headers.Add("sign", sign);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await http.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Cryptomus CreateInvoice failed: {Status} {Body}", response.StatusCode, responseBody);
                return null;
            }

            using var doc = JsonDocument.Parse(responseBody);
            var result = doc.RootElement.GetProperty("result");
            var uuid = result.GetProperty("uuid").GetString() ?? "";
            var url = result.GetProperty("url").GetString() ?? "";

            if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(url))
            {
                _logger.LogWarning("Cryptomus returned empty uuid or url: {Body}", responseBody);
                return null;
            }

            return (uuid, url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cryptomus CreateInvoice threw an exception");
            return null;
        }
    }

    /// <summary>
    /// Verifies the webhook signature from Cryptomus.
    /// Algorithm: MD5(base64(json_with_sign_null) + paymentApiKey)
    /// </summary>
    public bool VerifyWebhookSignature(string rawBody, out JsonDocument? parsed)
    {
        parsed = null;
        try
        {
            // Parse into a mutable node tree so we can null out the sign field
            var node = JsonNode.Parse(rawBody)?.AsObject();
            if (node == null) return false;

            var receivedSign = node["sign"]?.GetValue<string>();
            if (string.IsNullOrEmpty(receivedSign)) return false;

            // Set sign to null for hash computation
            node["sign"] = null;
            var jsonWithoutSign = node.ToJsonString();

            var expectedSign = ComputeSign(jsonWithoutSign);
            if (!string.Equals(expectedSign, receivedSign, StringComparison.OrdinalIgnoreCase))
                return false;

            // Re-parse original body for caller to use
            parsed = JsonDocument.Parse(rawBody);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cryptomus webhook signature verification failed");
            return false;
        }
    }

    /// <summary>
    /// Computes MD5(base64(json) + paymentApiKey) as a lowercase hex string.
    /// </summary>
    private string ComputeSign(string json)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var input = base64 + _paymentApiKey;
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
