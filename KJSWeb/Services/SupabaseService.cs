using KJSWeb.Models;
using Supabase;
using Supabase.Postgrest;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace KJSWeb.Services;

public class SupabaseService
{
    private readonly Supabase.Client _client;
    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;
    private readonly string _serviceKey;

    public SupabaseService(IConfiguration config)
    {
        _supabaseUrl = config["Supabase:Url"]!;
        _supabaseKey = config["Supabase:Key"]!;
        _serviceKey = config["Supabase:ServiceKey"] ?? _supabaseKey;
        
        var options = new SupabaseOptions
        {
            AutoConnectRealtime = true
        };

        _client = new Supabase.Client(_supabaseUrl, _supabaseKey, options);
    }

    // ──────────────────────────────────────────────
    //  SUBSCRIPTION METHODS (use service role key)
    // ──────────────────────────────────────────────

    public async Task<bool> CreateSubscriptionAsync(Subscription sub)
    {
        using var http = new HttpClient();
        var payload = JsonSerializer.Serialize(new
        {
            user_id = sub.UserId,
            plan = sub.Plan,
            amount_usd = sub.AmountUsd,
            amount_btc = sub.AmountBtc,
            btc_address = sub.BtcAddress,
            status = sub.Status
        });

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/rest/v1/subscriptions");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        request.Headers.Add("Prefer", "return=representation");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<Subscription?> GetSubscriptionByAddressAsync(string btcAddress)
    {
        using var http = new HttpClient();
        var encoded = Uri.EscapeDataString(btcAddress);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/subscriptions?btc_address=eq.{encoded}&select=*");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        var subs = JsonSerializer.Deserialize<List<SubscriptionDto>>(json);
        if (subs == null || subs.Count == 0) return null;

        return MapDtoToSubscription(subs[0]);
    }

    public async Task<bool> ActivateSubscriptionAsync(string btcAddress, string txid, int durationDays)
    {
        using var http = new HttpClient();
        var encoded = Uri.EscapeDataString(btcAddress);

        var now = DateTime.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            status = "active",
            txid = txid,
            paid_at = now.ToString("o"),
            expires_at = now.AddDays(durationDays).ToString("o")
        });

        var request = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"{_supabaseUrl}/rest/v1/subscriptions?btc_address=eq.{encoded}");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateSubscriptionStatusAsync(string btcAddress, string status)
    {
        using var http = new HttpClient();
        var encoded = Uri.EscapeDataString(btcAddress);

        var payload = JsonSerializer.Serialize(new { status });
        var request = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"{_supabaseUrl}/rest/v1/subscriptions?btc_address=eq.{encoded}");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<Subscription?> GetActiveSubscriptionAsync(string userId)
    {
        using var http = new HttpClient();
        var encoded = Uri.EscapeDataString(userId);
        var now = Uri.EscapeDataString(DateTime.UtcNow.ToString("o"));

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/subscriptions?user_id=eq.{encoded}&status=eq.active&expires_at=gt.{now}&select=*&order=expires_at.desc&limit=1");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        var subs = JsonSerializer.Deserialize<List<SubscriptionDto>>(json);
        if (subs == null || subs.Count == 0) return null;

        return MapDtoToSubscription(subs[0]);
    }

    public async Task<Subscription?> GetSubscriptionByIdAsync(string id)
    {
        using var http = new HttpClient();
        var encoded = Uri.EscapeDataString(id);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/subscriptions?id=eq.{encoded}&select=*");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        var subs = JsonSerializer.Deserialize<List<SubscriptionDto>>(json);
        if (subs == null || subs.Count == 0) return null;

        return MapDtoToSubscription(subs[0]);
    }

    // DTO for JSON deserialization from Supabase REST
    private class SubscriptionDto
    {
        public string id { get; set; } = "";
        public string user_id { get; set; } = "";
        public string plan { get; set; } = "";
        public decimal amount_usd { get; set; }
        public decimal? amount_btc { get; set; }
        public string btc_address { get; set; } = "";
        public string? txid { get; set; }
        public string status { get; set; } = "";
        public string? created_at { get; set; }
        public string? paid_at { get; set; }
        public string? expires_at { get; set; }
    }

    private static Subscription MapDtoToSubscription(SubscriptionDto dto) => new()
    {
        Id = dto.id,
        UserId = dto.user_id,
        Plan = dto.plan,
        AmountUsd = dto.amount_usd,
        AmountBtc = dto.amount_btc,
        BtcAddress = dto.btc_address,
        Txid = dto.txid,
        Status = dto.status,
        CreatedAt = DateTime.TryParse(dto.created_at, out var ca) ? ca : DateTime.MinValue,
        PaidAt = DateTime.TryParse(dto.paid_at, out var pa) ? pa : null,
        ExpiresAt = DateTime.TryParse(dto.expires_at, out var ea) ? ea : null
    };

    // ──────────────────────────────────────────────
    //  EXISTING POST/CATEGORY METHODS (unchanged)
    // ──────────────────────────────────────────────

    public async Task<(List<Post> Posts, int TotalCount)> GetLatestPostsAsync(int page = 1, int pageSize = 24)
    {
        var from = (page - 1) * pageSize;
        var to = from + pageSize - 1;

        var builder = _client.From<Post>();
        builder.Order("created_at", Constants.Ordering.Descending);
        builder.Range(from, to);
        
        var response = await builder.Get();

        // Get the total count via a direct REST call (bypasses SDK Get() ambiguity)
        var total = await GetTotalPostCountAsync();

        return (response.Models, total);
    }

    public async Task<(List<Post> Posts, int TotalCount)> GetPostsByCategoryAsync(string category, int page = 1, int pageSize = 24)
    {
        var from = (page - 1) * pageSize;
        var to = from + pageSize - 1;

        var builder = _client.From<Post>();
        builder.Filter("categories", Constants.Operator.Contains, new List<string> { category });
        builder.Order("created_at", Constants.Ordering.Descending);
        builder.Range(from, to);
        
        var response = await builder.Get();

        // Count for this category
        var total = await GetPostCountByCategoryAsync(category);

        return (response.Models, total);
    }

    public async Task<List<Category>> GetCategoriesAsync()
    {
        var builder = _client.From<Category>();
        builder.Order("name", Constants.Ordering.Ascending);
        
        var response = await builder.Get();
            
        return response.Models;
    }

    public async Task<Post?> GetPostByIdAsync(string id)
    {
        var response = await _client.From<Post>()
            .Filter("id", Constants.Operator.Equals, id)
            .Single();

        return response;
    }

    /// <summary>
    /// Get total post count via a direct REST API call with Prefer: count=exact
    /// This bypasses the SDK's Get() naming collision with IConfiguration.
    /// </summary>
    private async Task<int> GetTotalPostCountAsync()
    {
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Head, $"{_supabaseUrl}/rest/v1/posts?select=id");
        request.Headers.Add("apikey", _supabaseKey);
        request.Headers.Add("Authorization", $"Bearer {_supabaseKey}");
        request.Headers.Add("Prefer", "count=exact");

        var response = await http.SendAsync(request);

        if (response.Content.Headers.TryGetValues("Content-Range", out var values))
        {
            var range = values.FirstOrDefault();
            if (range != null && range.Contains("/"))
            {
                var totalStr = range.Split('/').Last();
                if (totalStr != "*" && int.TryParse(totalStr, out var count))
                    return count;
            }
        }
        return 0;
    }

    private async Task<int> GetPostCountByCategoryAsync(string category)
    {
        using var http = new HttpClient();
        var encodedCategory = Uri.EscapeDataString($"{{\"{category}\"}}");
        var request = new HttpRequestMessage(HttpMethod.Head, 
            $"{_supabaseUrl}/rest/v1/posts?select=id&categories=cs.{encodedCategory}");
        request.Headers.Add("apikey", _supabaseKey);
        request.Headers.Add("Authorization", $"Bearer {_supabaseKey}");
        request.Headers.Add("Prefer", "count=exact");

        var response = await http.SendAsync(request);

        if (response.Content.Headers.TryGetValues("Content-Range", out var values))
        {
            var range = values.FirstOrDefault();
            if (range != null && range.Contains("/"))
            {
                var totalStr = range.Split('/').Last();
                if (totalStr != "*" && int.TryParse(totalStr, out var count))
                    return count;
            }
        }
        return 0;
    }
}
