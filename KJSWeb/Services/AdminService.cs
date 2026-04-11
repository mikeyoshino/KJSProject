using System.Text.Json;

namespace KJSWeb.Services;

public class AdminService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _supabaseUrl;
    private readonly string _serviceKey;

    public AdminService(IConfiguration config, IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _supabaseUrl = config["Supabase:Url"]!;
        _serviceKey  = config["Supabase:ServiceKey"] ?? config["Supabase:Key"]!;
    }

    public async Task<List<AdminUser>> SearchUsersAsync(string email)
    {
        using var http = _httpClientFactory.CreateClient();
        var encoded = Uri.EscapeDataString(email);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/auth/v1/admin/users?email={encoded}");
        AddAdminHeaders(request);

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new();

        var json = await response.Content.ReadAsStringAsync();
        // Supabase Admin API does substring matching — filter to exact email only
        return ParseUsers(json)
            .Where(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<AdminUser?> GetUserByIdAsync(string userId)
    {
        using var http = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/auth/v1/admin/users/{userId}");
        AddAdminHeaders(request);

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(json);
            return MapUser(doc.RootElement);
        }
        catch { return null; }
    }

    public async Task<List<AdminUser>> ListRecentUsersAsync(int limit = 5)
    {
        using var http = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_supabaseUrl}/auth/v1/admin/users?page=1&per_page={limit}&sort_by=created_at&sort_order=desc");
        AddAdminHeaders(request);

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new();

        var json = await response.Content.ReadAsStringAsync();
        return ParseUsers(json);
    }

    private void AddAdminHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("apikey", _serviceKey);
        request.Headers.Add("Authorization", $"Bearer {_serviceKey}");
    }

    private static List<AdminUser> ParseUsers(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Response is either { users: [...] } or directly [...]
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("users", out var usersEl))
                arr = usersEl;
            else if (root.ValueKind == JsonValueKind.Array)
                arr = root;
            else
                return new();

            return arr.EnumerateArray().Select(MapUser).Where(u => u != null).ToList()!;
        }
        catch { return new(); }
    }

    private static AdminUser MapUser(JsonElement el)
    {
        return new AdminUser
        {
            Id           = el.TryGetProperty("id", out var id)               ? id.GetString() ?? ""  : "",
            Email        = el.TryGetProperty("email", out var email)          ? email.GetString() ?? "" : "",
            CreatedAt    = el.TryGetProperty("created_at", out var ca)        && DateTime.TryParse(ca.GetString(), out var caDate)   ? caDate  : DateTime.MinValue,
            LastSignInAt = el.TryGetProperty("last_sign_in_at", out var lsia) && DateTime.TryParse(lsia.GetString(), out var lsDate) ? lsDate  : null,
        };
    }
}

public class AdminUser
{
    public string    Id           { get; set; } = "";
    public string    Email        { get; set; } = "";
    public DateTime  CreatedAt    { get; set; }
    public DateTime? LastSignInAt { get; set; }
}
