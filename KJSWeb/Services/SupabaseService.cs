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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;
    private readonly string _serviceKey;
    private readonly string _workerBaseUrl;
    private readonly string _b2PublicBaseUrl;

    public SupabaseService(IConfiguration config, IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _supabaseUrl = config["Supabase:Url"]!;
        _supabaseKey = config["Supabase:Key"]!;
        _serviceKey = config["Supabase:ServiceKey"] ?? _supabaseKey;
        _workerBaseUrl = config["CloudflareWorker:WorkerBaseUrl"]?.TrimEnd('/') ?? "";
        _b2PublicBaseUrl = config["B2:PublicBaseUrl"]?.TrimEnd('/') ?? "";
        
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
        using var http = _httpClientFactory.CreateClient();
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
        using var http = _httpClientFactory.CreateClient();
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
        // Look up the subscription being activated to get the user_id
        var sub = await GetSubscriptionByAddressAsync(btcAddress);
        if (sub == null) return false;

        var now = DateTime.UtcNow;

        // If the user already has an active subscription with time remaining, extend from its expiry
        // so they don't lose unused days when renewing or upgrading.
        var existingActive = await GetActiveSubscriptionAsync(sub.UserId);
        var baseDate = (existingActive?.ExpiresAt.HasValue == true && existingActive.ExpiresAt.Value > now)
            ? existingActive.ExpiresAt.Value
            : now;

        using var http = _httpClientFactory.CreateClient();
        var encoded = Uri.EscapeDataString(btcAddress);
        var payload = JsonSerializer.Serialize(new
        {
            status = "active",
            txid = txid,
            paid_at = now.ToString("o"),
            expires_at = baseDate.AddDays(durationDays).ToString("o")
        });

        var request = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"{_supabaseUrl}/rest/v1/subscriptions?btc_address=eq.{encoded}");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return false;

        // Mark the old subscription as superseded so it no longer shows up as active
        if (existingActive != null && existingActive.BtcAddress != btcAddress)
            await UpdateSubscriptionStatusAsync(existingActive.BtcAddress, "superseded");

        return true;
    }

    public async Task<bool> UpdateSubscriptionStatusAsync(string btcAddress, string status)
    {
        using var http = _httpClientFactory.CreateClient();
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
        using var http = _httpClientFactory.CreateClient();
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
        using var http = _httpClientFactory.CreateClient();
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

    public async Task<(List<Post> Posts, int TotalCount)> GetLatestPostsAsync(int page = 1, int pageSize = 24, PostSource? source = null)
    {
        var from = (page - 1) * pageSize;
        var to = from + pageSize - 1;

        var builder = _client.From<Post>();
        if (source.HasValue)
            builder.Filter("source_name", Constants.Operator.Equals, source.Value.ToString());
        builder.Filter("status", Constants.Operator.Equals, "published");
        builder.Order("created_at", Constants.Ordering.Descending);
        builder.Range(from, to);

        var response = await builder.Get();
        var total = await GetTotalPostCountAsync(source);

        return (response.Models ?? new(), total);
    }

    public async Task<List<Post>> GetPopularPostsAsync(int limit = 6, PostSource? source = null, string period = "week")
    {
        var since = period switch
        {
            "month" => DateTime.UtcNow.AddMonths(-1),
            "year"  => DateTime.UtcNow.AddYears(-1),
            _       => DateTime.UtcNow.AddDays(-7),
        };

        var builder = _client.From<Post>();
        if (source.HasValue)
            builder.Filter("source_name", Constants.Operator.Equals, source.Value.ToString());
        builder.Filter("status", Constants.Operator.Equals, "published");
        builder.Filter("created_at", Constants.Operator.GreaterThanOrEqual, since.ToString("o"));
        builder.Order("view_count", Constants.Ordering.Descending);
        builder.Range(0, limit - 1);

        var response = await builder.Get();
        return response.Models ?? new();
    }

    public async Task<(List<Post> Posts, int TotalCount)> GetPostsByCategoryAsync(string category, int page = 1, int pageSize = 24)
    {
        var from = (page - 1) * pageSize;
        var to = from + pageSize - 1;

        var builder = _client.From<Post>();
        builder.Filter("categories", Constants.Operator.Contains, new List<string> { category });
        builder.Filter("status", Constants.Operator.Equals, "published");
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
    private async Task<int> GetTotalPostCountAsync(PostSource? source = null)
    {
        using var http = _httpClientFactory.CreateClient();
        var url = $"{_supabaseUrl}/rest/v1/posts?select=id&status=eq.published";
        if (source.HasValue)
            url += $"&source_name=eq.{source.Value}";
        var request = new HttpRequestMessage(HttpMethod.Head, url);
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
        using var http = _httpClientFactory.CreateClient();
        var encodedCategory = Uri.EscapeDataString($"{{\"{category}\"}}");
        var request = new HttpRequestMessage(HttpMethod.Head, 
            $"{_supabaseUrl}/rest/v1/posts?select=id&status=eq.published&categories=cs.{encodedCategory}");
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

    // ──────────────────────────────────────────────
    //  JGIRL METHODS
    // ──────────────────────────────────────────────

    public async Task<(List<JGirlPost> Posts, int TotalCount)> GetJGirlPostsAsync(int page = 1, int pageSize = 24, string? source = null)
    {
        var from = (page - 1) * pageSize;
        var to   = from + pageSize - 1;

        var builder = _client.From<JGirlPost>();
        if (!string.IsNullOrEmpty(source))
            builder.Filter("source", Constants.Operator.Equals, source);
        builder.Filter("status", Constants.Operator.Equals, "published");
        builder.Order("created_at", Constants.Ordering.Descending);
        builder.Range(from, to);

        var response = await builder.Get();
        var total    = await GetJGirlTotalCountAsync(source);
        return (response.Models ?? new(), total);
    }

    public async Task<JGirlPost?> GetJGirlPostByIdAsync(string id)
    {
        var response = await _client.From<JGirlPost>()
            .Filter("id", Constants.Operator.Equals, id)
            .Single();
        return response;
    }

    public async Task<List<string>> GetJGirlSourcesAsync()
    {
        using var http = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/jgirl_posts?select=source&status=eq.published&order=source.asc&limit=1000");
        request.Headers.Add("apikey", _supabaseKey);
        request.Headers.Add("Authorization", $"Bearer {_supabaseKey}");

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new();

        var json = await response.Content.ReadAsStringAsync();
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json);
        if (rows == null) return new();

        return rows
            .Select(r => r.TryGetValue("source", out var s) ? s : "")
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .OrderBy(s => s)
            .ToList();
    }

    private async Task<int> GetJGirlTotalCountAsync(string? source = null)
    {
        using var http = _httpClientFactory.CreateClient();
        var url = $"{_supabaseUrl}/rest/v1/jgirl_posts?select=id&status=eq.published";
        if (!string.IsNullOrEmpty(source))
            url += $"&source=eq.{Uri.EscapeDataString(source)}";

        var request = new HttpRequestMessage(HttpMethod.Head, url);
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

    public async Task<List<Post>> SearchPostsAsync(string query, int limit = 12)
    {
        using var http = _httpClientFactory.CreateClient();
        var q   = Uri.EscapeDataString($"*{query}*");
        var url = $"{_supabaseUrl}/rest/v1/posts" +
                  $"?select=id,title,thumbnail_url,source_name,created_at,categories,view_count" +
                  $"&status=eq.published" +
                  $"&or=(title.ilike.{q},content_html.ilike.{q})" +
                  $"&order=created_at.desc" +
                  $"&limit={limit}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("apikey", _supabaseKey);
        request.Headers.Add("Authorization", $"Bearer {_supabaseKey}");

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new();

        var json = await response.Content.ReadAsStringAsync();
        var dtos = JsonSerializer.Deserialize<List<PostSearchDto>>(json);
        if (dtos == null) return new();

        return dtos.Select(d => new Post
        {
            Id           = Guid.TryParse(d.id, out var g) ? g : Guid.Empty,
            Title        = d.title,
            ThumbnailUrl = d.thumbnail_url ?? "",
            SourceNameRaw = d.source_name ?? "Buzz69",
            CreatedAt    = DateTime.TryParse(d.created_at, out var dt) ? dt : DateTime.UtcNow,
            Categories   = d.categories?.ToList() ?? new(),
            ViewCount    = d.view_count,
        }).ToList();
    }

    private class PostSearchDto
    {
        public string id           { get; set; } = "";
        public string title        { get; set; } = "";
        public string? thumbnail_url { get; set; }
        public string? source_name { get; set; }
        public string? created_at  { get; set; }
        public string[]? categories { get; set; }
        public long view_count     { get; set; }
    }

    public async Task IncrementViewCountAsync(Guid postId, string table)
    {
        using var http = _httpClientFactory.CreateClient();
        var payload = JsonSerializer.Serialize(new { p_id = postId, p_table = table });
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/rest/v1/rpc/increment_view_count");
        request.Headers.Add("apikey", _supabaseKey);
        request.Headers.Add("Authorization", $"Bearer {_supabaseKey}");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        await http.SendAsync(request);
    }

}

