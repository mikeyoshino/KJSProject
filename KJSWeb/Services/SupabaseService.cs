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
        _workerBaseUrl = config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
        _b2PublicBaseUrl = config["B2:PublicBaseUrl"]?.TrimEnd('/') ?? "";
        
        var options = new SupabaseOptions
        {
            AutoConnectRealtime = false
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

    public async Task<Subscription?> GetPendingSubscriptionByUserIdAsync(string userId)
    {
        using var http = _httpClientFactory.CreateClient();
        var encoded = Uri.EscapeDataString(userId);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/subscriptions?user_id=eq.{encoded}&status=eq.pending&select=*&order=created_at.desc&limit=1");
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
        public string email { get; set; } = "";
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
        Email = dto.email,
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

    // ──────────────────────────────────────────────
    //  CRM ADMIN METHODS
    // ──────────────────────────────────────────────

    public async Task<(List<Subscription> Items, int Total)> GetAllSubscriptionsAsync(int page, int pageSize, string? status = null)
    {
        using var http = _httpClientFactory.CreateClient();
        var from = (page - 1) * pageSize;
        var to   = from + pageSize - 1;

        var url = $"{_supabaseUrl}/rest/v1/subscriptions?select=*&order=created_at.desc";
        if (!string.IsNullOrEmpty(status))
            url += $"&status=eq.{Uri.EscapeDataString(status)}";

        // Count
        var countReq = new HttpRequestMessage(HttpMethod.Head, url);
        countReq.Headers.Add("apikey", _serviceKey);
        countReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        countReq.Headers.Add("Prefer", "count=exact");
        var countResp = await http.SendAsync(countReq);
        var total = 0;
        if (countResp.Content.Headers.TryGetValues("Content-Range", out var cv))
        {
            var r = cv.FirstOrDefault();
            if (r != null && r.Contains("/") && r.Split('/').Last() is var s && s != "*")
                int.TryParse(s, out total);
        }

        // Data
        var dataUrl = url + $"&offset={from}&limit={pageSize}";
        var dataReq = new HttpRequestMessage(HttpMethod.Get, dataUrl);
        dataReq.Headers.Add("apikey", _serviceKey);
        dataReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        var dataResp = await http.SendAsync(dataReq);
        if (!dataResp.IsSuccessStatusCode) return (new(), total);

        var json = await dataResp.Content.ReadAsStringAsync();
        var dtos = JsonSerializer.Deserialize<List<SubscriptionDto>>(json) ?? new();
        return (dtos.Select(MapDtoToSubscription).ToList(), total);
    }

    public async Task<List<Subscription>> GetSubscriptionsByUserIdAsync(string userId)
    {
        using var http = _httpClientFactory.CreateClient();
        var encoded = Uri.EscapeDataString(userId);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/subscriptions?user_id=eq.{encoded}&select=*&order=created_at.desc");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new();

        var json = await response.Content.ReadAsStringAsync();
        var dtos = JsonSerializer.Deserialize<List<SubscriptionDto>>(json) ?? new();
        return dtos.Select(MapDtoToSubscription).ToList();
    }

    public async Task<bool> GrantSubscriptionAdminAsync(string userId, string email, string plan, int days)
    {
        var now = DateTime.UtcNow;
        var existingActive = await GetActiveSubscriptionAsync(userId);
        var baseDate = existingActive?.ExpiresAt.HasValue == true && existingActive.ExpiresAt.Value > now
            ? existingActive.ExpiresAt.Value
            : now;

        using var http = _httpClientFactory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            user_id    = userId,
            email      = email,
            plan       = plan,
            amount_usd = 0m,
            btc_address = $"admin-grant-{Guid.NewGuid():N}",
            status     = "active",
            paid_at    = now.ToString("o"),
            expires_at = baseDate.AddDays(days).ToString("o")
        });

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/rest/v1/subscriptions");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        request.Headers.Add("Prefer", "return=representation");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RevokeSubscriptionAsync(string id)
    {
        using var http = _httpClientFactory.CreateClient();
        var encoded = Uri.EscapeDataString(id);
        var payload = JsonSerializer.Serialize(new { status = "revoked" });
        var request = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"{_supabaseUrl}/rest/v1/subscriptions?id=eq.{encoded}");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ExtendSubscriptionAsync(string id, int days)
    {
        var sub = await GetSubscriptionByIdAsync(id);
        if (sub == null) return false;

        var base_ = sub.ExpiresAt.HasValue && sub.ExpiresAt.Value > DateTime.UtcNow
            ? sub.ExpiresAt.Value
            : DateTime.UtcNow;

        using var http = _httpClientFactory.CreateClient();
        var encoded = Uri.EscapeDataString(id);
        var payload = JsonSerializer.Serialize(new
        {
            expires_at = base_.AddDays(days).ToString("o"),
            status = "active"
        });
        var request = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"{_supabaseUrl}/rest/v1/subscriptions?id=eq.{encoded}");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> BanUserAsync(string userId, string email, string? reason, string adminEmail)
    {
        using var http = _httpClientFactory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            user_id   = userId,
            email     = email,
            reason    = reason ?? "",
            banned_by = adminEmail
        });
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/rest/v1/banned_users");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        request.Headers.Add("Prefer", "return=representation,resolution=merge-duplicates");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UnbanUserAsync(string userId)
    {
        using var http = _httpClientFactory.CreateClient();
        var encoded = Uri.EscapeDataString(userId);
        var request = new HttpRequestMessage(HttpMethod.Delete,
            $"{_supabaseUrl}/rest/v1/banned_users?user_id=eq.{encoded}");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");

        var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<List<string>> GetBannedUserIdsAsync()
    {
        using var http = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/banned_users?select=user_id");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new();

        var json = await response.Content.ReadAsStringAsync();
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json) ?? new();
        return rows.Select(r => r.TryGetValue("user_id", out var v) ? v : "")
                   .Where(v => !string.IsNullOrEmpty(v))
                   .ToList();
    }

    public async Task<List<BannedUser>> GetBannedUsersAsync()
    {
        using var http = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/banned_users?select=*&order=banned_at.desc");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<BannedUser>>(json) ?? new();
    }

    public async Task<CrmStats> GetCrmStatsAsync()
    {
        using var http = _httpClientFactory.CreateClient();
        var now      = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nowEnc   = Uri.EscapeDataString(now.ToString("o"));
        var msEnc    = Uri.EscapeDataString(monthStart.ToString("o"));

        // Active subs count
        var activeReq = new HttpRequestMessage(HttpMethod.Head,
            $"{_supabaseUrl}/rest/v1/subscriptions?status=eq.active&expires_at=gt.{nowEnc}&select=id");
        activeReq.Headers.Add("apikey", _serviceKey);
        activeReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        activeReq.Headers.Add("Prefer", "count=exact");
        var activeResp = await http.SendAsync(activeReq);
        var activeCount = ParseCount(activeResp);

        // New this month
        var newReq = new HttpRequestMessage(HttpMethod.Head,
            $"{_supabaseUrl}/rest/v1/subscriptions?created_at=gte.{msEnc}&select=id");
        newReq.Headers.Add("apikey", _serviceKey);
        newReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        newReq.Headers.Add("Prefer", "count=exact");
        var newResp = await http.SendAsync(newReq);
        var newCount = ParseCount(newResp);

        // Revenue this month
        var revReq = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/subscriptions?status=eq.active&paid_at=gte.{msEnc}&select=amount_usd");
        revReq.Headers.Add("apikey", _serviceKey);
        revReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        var revResp = await http.SendAsync(revReq);
        var revenueThisMonth = 0m;
        if (revResp.IsSuccessStatusCode)
        {
            var revJson = await revResp.Content.ReadAsStringAsync();
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(revJson) ?? new();
            revenueThisMonth = rows.Sum(r => r.TryGetValue("amount_usd", out var v) ? v.GetDecimal() : 0m);
        }

        // Banned count
        var banReq = new HttpRequestMessage(HttpMethod.Head,
            $"{_supabaseUrl}/rest/v1/banned_users?select=user_id");
        banReq.Headers.Add("apikey", _serviceKey);
        banReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        banReq.Headers.Add("Prefer", "count=exact");
        var banResp = await http.SendAsync(banReq);
        var bannedCount = ParseCount(banResp);

        return new CrmStats
        {
            ActiveSubscribers  = activeCount,
            NewThisMonth       = newCount,
            RevenueThisMonth   = revenueThisMonth,
            BannedUsers        = bannedCount
        };
    }

    public async Task<List<Subscription>> GetRecentSubscriptionsAsync(int limit = 10)
    {
        using var http = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/subscriptions?select=*&order=created_at.desc&limit={limit}");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new();

        var json = await response.Content.ReadAsStringAsync();
        var dtos = JsonSerializer.Deserialize<List<SubscriptionDto>>(json) ?? new();
        return dtos.Select(MapDtoToSubscription).ToList();
    }

    public async Task<bool> IsUserBannedAsync(string userId)
    {
        using var http = _httpClientFactory.CreateClient();
        var encoded = Uri.EscapeDataString(userId);
        var request = new HttpRequestMessage(HttpMethod.Head,
            $"{_supabaseUrl}/rest/v1/banned_users?user_id=eq.{encoded}&select=user_id");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        request.Headers.Add("Prefer", "count=exact");
        var response = await http.SendAsync(request);
        return ParseCount(response) > 0;
    }

    private static int ParseCount(HttpResponseMessage response)
    {
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
    //  SUPPORT TICKET METHODS
    // ──────────────────────────────────────────────

    public async Task<SupportTicket?> CreateTicketAsync(
        string userId, string email, string title,
        TicketCategory category, string description)
    {
        using var http = _httpClientFactory.CreateClient();

        // Create the ticket
        var ticketPayload = JsonSerializer.Serialize(new
        {
            id         = Guid.NewGuid().ToString(),
            user_id    = userId,
            user_email = email,
            title      = title,
            category   = TicketCategoryToString(category),
            status     = "open"
        });

        var ticketReq = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/rest/v1/support_tickets");
        ticketReq.Headers.Add("apikey", _serviceKey);
        ticketReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        ticketReq.Headers.Add("Prefer", "return=representation");
        ticketReq.Content = new StringContent(ticketPayload, Encoding.UTF8, "application/json");

        var ticketResp = await http.SendAsync(ticketReq);
        if (!ticketResp.IsSuccessStatusCode)
        {
            var errBody = await ticketResp.Content.ReadAsStringAsync();
            Console.Error.WriteLine($"[CreateTicketAsync] {(int)ticketResp.StatusCode} — {errBody}");
            return null;
        }

        var ticketJson = await ticketResp.Content.ReadAsStringAsync();
        var tickets = JsonSerializer.Deserialize<List<TicketDto>>(ticketJson);
        if (tickets == null || tickets.Count == 0) return null;

        var ticket = MapDtoToTicket(tickets[0]);

        // Add the first message
        var msgPayload = JsonSerializer.Serialize(new
        {
            id        = Guid.NewGuid().ToString(),
            ticket_id = ticket.Id,
            sender_id = userId,
            is_admin  = false,
            message   = description
        });

        var msgReq = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/rest/v1/ticket_messages");
        msgReq.Headers.Add("apikey", _serviceKey);
        msgReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        msgReq.Headers.Add("Prefer", "return=minimal");
        msgReq.Content = new StringContent(msgPayload, Encoding.UTF8, "application/json");

        await http.SendAsync(msgReq);

        return ticket;
    }

    public async Task<(List<SupportTicket> Items, int Total)> GetUserTicketsAsync(
        string userId, int page = 1, int pageSize = 20)
    {
        using var http = _httpClientFactory.CreateClient();
        var encoded = Uri.EscapeDataString(userId);
        var from    = (page - 1) * pageSize;

        var baseUrl = $"{_supabaseUrl}/rest/v1/support_tickets?user_id=eq.{encoded}&select=*&order=created_at.desc";

        // Count
        var countReq = new HttpRequestMessage(HttpMethod.Head, baseUrl);
        countReq.Headers.Add("apikey", _serviceKey);
        countReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        countReq.Headers.Add("Prefer", "count=exact");
        var countResp = await http.SendAsync(countReq);
        var total = ParseCount(countResp);

        // Data
        var dataReq = new HttpRequestMessage(HttpMethod.Get, baseUrl + $"&offset={from}&limit={pageSize}");
        dataReq.Headers.Add("apikey", _serviceKey);
        dataReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        var dataResp = await http.SendAsync(dataReq);
        if (!dataResp.IsSuccessStatusCode) return (new(), total);

        var json = await dataResp.Content.ReadAsStringAsync();
        var dtos = JsonSerializer.Deserialize<List<TicketDto>>(json) ?? new();
        return (dtos.Select(MapDtoToTicket).ToList(), total);
    }

    public async Task<(List<SupportTicket> Items, int Total)> GetAllTicketsAsync(
        int page = 1, int pageSize = 20, TicketStatus? status = null)
    {
        using var http = _httpClientFactory.CreateClient();
        var from = (page - 1) * pageSize;

        var baseUrl = $"{_supabaseUrl}/rest/v1/support_tickets?select=*&order=created_at.desc";
        if (status.HasValue)
            baseUrl += $"&status=eq.{Uri.EscapeDataString(TicketStatusToString(status.Value))}";

        // Count
        var countReq = new HttpRequestMessage(HttpMethod.Head, baseUrl);
        countReq.Headers.Add("apikey", _serviceKey);
        countReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        countReq.Headers.Add("Prefer", "count=exact");
        var countResp = await http.SendAsync(countReq);
        var total = ParseCount(countResp);

        // Data
        var dataReq = new HttpRequestMessage(HttpMethod.Get, baseUrl + $"&offset={from}&limit={pageSize}");
        dataReq.Headers.Add("apikey", _serviceKey);
        dataReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        var dataResp = await http.SendAsync(dataReq);
        if (!dataResp.IsSuccessStatusCode) return (new(), total);

        var json = await dataResp.Content.ReadAsStringAsync();
        var dtos = JsonSerializer.Deserialize<List<TicketDto>>(json) ?? new();
        return (dtos.Select(MapDtoToTicket).ToList(), total);
    }

    public async Task<SupportTicket?> GetTicketByIdAsync(string id)
    {
        using var http = _httpClientFactory.CreateClient();
        var encoded = Uri.EscapeDataString(id);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/support_tickets?id=eq.{encoded}&select=*");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        var dtos = JsonSerializer.Deserialize<List<TicketDto>>(json);
        if (dtos == null || dtos.Count == 0) return null;

        return MapDtoToTicket(dtos[0]);
    }

    public async Task<List<TicketMessage>> GetTicketMessagesAsync(string ticketId)
    {
        using var http = _httpClientFactory.CreateClient();
        var encoded = Uri.EscapeDataString(ticketId);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/ticket_messages?ticket_id=eq.{encoded}&select=*&order=created_at.asc");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new();

        var json = await response.Content.ReadAsStringAsync();
        var dtos = JsonSerializer.Deserialize<List<TicketMessageDto>>(json) ?? new();
        return dtos.Select(MapDtoToTicketMessage).ToList();
    }

    public async Task<bool> AddTicketMessageAsync(
        string ticketId, string senderId, string message, bool isAdmin)
    {
        using var http = _httpClientFactory.CreateClient();
        var now = DateTime.UtcNow;

        // Insert message
        var msgPayload = JsonSerializer.Serialize(new
        {
            id        = Guid.NewGuid().ToString(),
            ticket_id = ticketId,
            sender_id = senderId,
            is_admin  = isAdmin,
            message   = message
        });

        var msgReq = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/rest/v1/ticket_messages");
        msgReq.Headers.Add("apikey", _serviceKey);
        msgReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        msgReq.Headers.Add("Prefer", "return=minimal");
        msgReq.Content = new StringContent(msgPayload, Encoding.UTF8, "application/json");

        var msgResp = await http.SendAsync(msgReq);
        if (!msgResp.IsSuccessStatusCode)
        {
            var errBody = await msgResp.Content.ReadAsStringAsync();
            Console.Error.WriteLine($"[AddTicketMessageAsync] {(int)msgResp.StatusCode} — {errBody}");
            return false;
        }

        // Update last_reply_at on the ticket
        var encoded = Uri.EscapeDataString(ticketId);
        var patchPayload = JsonSerializer.Serialize(new
        {
            last_reply_at = now.ToString("o"),
            updated_at    = now.ToString("o")
        });

        var patchReq = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"{_supabaseUrl}/rest/v1/support_tickets?id=eq.{encoded}");
        patchReq.Headers.Add("apikey", _serviceKey);
        patchReq.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        patchReq.Content = new StringContent(patchPayload, Encoding.UTF8, "application/json");

        await http.SendAsync(patchReq);

        return true;
    }

    public async Task<bool> UpdateTicketStatusAsync(string ticketId, TicketStatus newStatus)
    {
        using var http = _httpClientFactory.CreateClient();
        var encoded = Uri.EscapeDataString(ticketId);
        var payload = JsonSerializer.Serialize(new
        {
            status     = TicketStatusToString(newStatus),
            updated_at = DateTime.UtcNow.ToString("o")
        });

        var request = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"{_supabaseUrl}/rest/v1/support_tickets?id=eq.{encoded}");
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    // ── Ticket DTOs ────────────────────────────────

    private class TicketDto
    {
        public string  id           { get; set; } = "";
        public string  user_id      { get; set; } = "";
        public string  user_email   { get; set; } = "";
        public string  title        { get; set; } = "";
        public string  category     { get; set; } = "other";
        public string  status       { get; set; } = "open";
        public string? created_at   { get; set; }
        public string? updated_at   { get; set; }
        public string? last_reply_at { get; set; }
    }

    private class TicketMessageDto
    {
        public string  id         { get; set; } = "";
        public string  ticket_id  { get; set; } = "";
        public string  sender_id  { get; set; } = "";
        public bool    is_admin   { get; set; }
        public string  message    { get; set; } = "";
        public string? created_at { get; set; }
    }

    private static SupportTicket MapDtoToTicket(TicketDto dto) => new()
    {
        Id           = dto.id,
        UserId       = dto.user_id,
        UserEmail    = dto.user_email,
        Title        = dto.title,
        CategoryRaw  = dto.category,
        StatusRaw    = dto.status,
        CreatedAt    = DateTime.TryParse(dto.created_at,    out var ca) ? ca : DateTime.UtcNow,
        UpdatedAt    = DateTime.TryParse(dto.updated_at,    out var ua) ? ua : DateTime.UtcNow,
        LastReplyAt  = DateTime.TryParse(dto.last_reply_at, out var lr) ? lr : null
    };

    private static TicketMessage MapDtoToTicketMessage(TicketMessageDto dto) => new()
    {
        Id        = dto.id,
        TicketId  = dto.ticket_id,
        SenderId  = dto.sender_id,
        IsAdmin   = dto.is_admin,
        Message   = dto.message,
        CreatedAt = DateTime.TryParse(dto.created_at, out var ca) ? ca : DateTime.UtcNow
    };

    // DB stores status as lowercase, with InProgress as "in_progress"
    private static string TicketStatusToString(TicketStatus s) => s switch
    {
        TicketStatus.Open       => "open",
        TicketStatus.InProgress => "in_progress",
        TicketStatus.Resolved   => "resolved",
        _                       => "open"
    };

    // DB stores category as lowercase
    private static string TicketCategoryToString(TicketCategory c) => c switch
    {
        TicketCategory.Payment  => "payment",
        TicketCategory.Download => "download",
        TicketCategory.Account  => "account",
        TicketCategory.Other    => "other",
        _                       => "other"
    };

}

public class BannedUser
{
    [System.Text.Json.Serialization.JsonPropertyName("user_id")]
    public string UserId   { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("email")]
    public string Email    { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("reason")]
    public string? Reason  { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("banned_at")]
    public string? BannedAt { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("banned_by")]
    public string? BannedBy { get; set; }
}

public class CrmStats
{
    public int     ActiveSubscribers { get; set; }
    public int     NewThisMonth      { get; set; }
    public decimal RevenueThisMonth  { get; set; }
    public int     BannedUsers       { get; set; }
}

