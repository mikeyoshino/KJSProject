using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RgToB2Migrator.Configuration;
using RgToB2Migrator.Models;
using System.Text;

namespace RgToB2Migrator.Services;

public class SupabaseMigrationService
{
    private readonly string _supabaseUrl;
    private readonly string _serviceKey;
    private readonly ILogger<SupabaseMigrationService> _logger;

    public SupabaseMigrationService(
        IOptions<SupabaseSettings> settings,
        ILogger<SupabaseMigrationService> logger)
    {
        _supabaseUrl = settings.Value.Url;
        _serviceKey = settings.Value.ServiceKey;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────
    //  Fetch pending rows
    // ──────────────────────────────────────────────────────────

    public async Task<List<MigrationPostsRow>> FetchPendingPostsAsync(
        int batchSize = 50, CancellationToken ct = default)
    {
        try
        {
            var rows = await FetchPendingAsync("posts", batchSize, ct);
            var result = rows.Select(MapPostsRow).ToList();
            _logger.LogInformation("Fetched {Count} pending posts", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch pending posts");
            throw;
        }
    }

    public async Task<List<MigrationAsianScandalRow>> FetchPendingAsianScandalPostsAsync(
        int batchSize = 50, CancellationToken ct = default)
    {
        if (batchSize <= 0) return [];

        try
        {
            var rows = await FetchPendingAsync("asianscandal_posts", batchSize, ct);
            var result = rows.Select(MapAsianScandalRow).ToList();
            _logger.LogInformation("Fetched {Count} pending asianscandal posts", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch pending asianscandal posts");
            throw;
        }
    }

    private async Task<List<JObject>> FetchPendingAsync(string table, int batchSize, CancellationToken ct)
    {
        using var http = CreateHttpClient();

        var url = $"{_supabaseUrl}/rest/v1/{table}" +
                  $"?download_status=eq.pending" +
                  $"&original_rapidgator_url=neq.{{}}" +
                  $"&order=created_at.asc" +
                  $"&limit={batchSize}" +
                  $"&select=id,original_rapidgator_url,our_download_link,download_status";

        var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {table} failed {(int)response.StatusCode}: {body}");

        return JArray.Parse(body).Cast<JObject>().ToList();
    }

    // ──────────────────────────────────────────────────────────
    //  Status updates
    // ──────────────────────────────────────────────────────────

    public Task MarkPendingAsync(Guid id, string tableName, CancellationToken ct = default)
    {
        _logger.LogDebug("Reset post {Id} to pending in {Table}", id, tableName);
        return PatchAsync(tableName, id, new { download_status = "pending" }, ct);
    }

    public Task MarkProcessingAsync(Guid id, string tableName, CancellationToken ct = default)
    {
        _logger.LogDebug("Marked post {Id} as processing in {Table}", id, tableName);
        return PatchAsync(tableName, id, new { download_status = "processing" }, ct);
    }

    public Task MarkDoneAsync(Guid id, string tableName, List<string> urls, CancellationToken ct = default)
    {
        _logger.LogInformation("Marked post {Id} as done in {Table}", id, tableName);
        return PatchAsync(tableName, id, new { download_status = "done", our_download_link = urls }, ct);
    }

    public Task MarkFailedAsync(Guid id, string tableName, CancellationToken ct = default)
    {
        _logger.LogWarning("Marked post {Id} as failed in {Table}", id, tableName);
        return PatchAsync(tableName, id, new { download_status = "failed" }, ct);
    }

    public async Task ResetStuckProcessingRowsAsync(CancellationToken ct = default)
    {
        await ResetStuckInTableAsync("posts", ct);
        await ResetStuckInTableAsync("asianscandal_posts", ct);
        
        await ResetBrokenDoneRowsAsync("posts", ct);
        await ResetBrokenDoneRowsAsync("asianscandal_posts", ct);
    }

    private async Task ResetBrokenDoneRowsAsync(string table, CancellationToken ct)
    {
        using var http = CreateHttpClient();
        var payload = JsonConvert.SerializeObject(new { download_status = "pending" });

        // Reset rows where status=done but our_download_link is empty [], contains an empty string [""], or is NULL
        // In PostgREST/Supabase, we use eq.{} (empty array), eq.{""} (array with empty string), or is.null
        string[] filters = { "our_download_link=eq.{}", "our_download_link=eq.{{\"\"}}", "our_download_link=is.null" };

        foreach (var filter in filters)
        {
            var request = new HttpRequestMessage(HttpMethod.Patch,
                $"{_supabaseUrl}/rest/v1/{table}?download_status=eq.done&{filter}")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                _logger.LogInformation("Reset broken 'done' rows in {Table} using filter {Filter}", table, filter);
            else
                _logger.LogDebug("No broken 'done' rows found in {Table} for filter {Filter} (Status: {Status})", 
                    table, filter, response.StatusCode);
        }
    }

    private async Task ResetStuckInTableAsync(string table, CancellationToken ct)
    {
        var oneHourAgo = Uri.EscapeDataString(DateTime.UtcNow.AddHours(-1).ToString("o"));

        using var http = CreateHttpClient();
        var payload = JsonConvert.SerializeObject(new { download_status = "pending" });

        var request = new HttpRequestMessage(HttpMethod.Patch,
            $"{_supabaseUrl}/rest/v1/{table}?download_status=eq.processing&created_at=lt.{oneHourAgo}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            _logger.LogInformation("Reset stuck processing rows in {Table}", table);
        else
            _logger.LogWarning("Could not reset stuck rows in {Table}: {Status}", table, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────

    private async Task PatchAsync(string table, Guid id, object payload, CancellationToken ct)
    {
        using var http = CreateHttpClient();
        var json = JsonConvert.SerializeObject(payload);

        var request = new HttpRequestMessage(HttpMethod.Patch,
            $"{_supabaseUrl}/rest/v1/{table}?id=eq.{id}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"PATCH {table} failed {(int)response.StatusCode}: {body}");
        }
    }

    private HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("apikey", _serviceKey);
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_serviceKey}");
        return http;
    }

    // ──────────────────────────────────────────────────────────
    //  Thumbnail migration
    // ──────────────────────────────────────────────────────────

    public async Task<List<MigrationPostsRow>> FetchPendingThumbnailPostsAsync(
        int batchSize = 50, CancellationToken ct = default)
    {
        if (batchSize <= 0) return [];
        return await FetchPendingThumbnailsFromTableAsync<MigrationPostsRow>(
            "posts", batchSize, MapPostsRow, ct);
    }

    public async Task<List<MigrationAsianScandalRow>> FetchPendingThumbnailAsianScandalPostsAsync(
        int batchSize = 50, CancellationToken ct = default)
    {
        if (batchSize <= 0) return [];
        return await FetchPendingThumbnailsFromTableAsync<MigrationAsianScandalRow>(
            "asianscandal_posts", batchSize, MapAsianScandalRow, ct);
    }

    private async Task<List<T>> FetchPendingThumbnailsFromTableAsync<T>(
        string table, int batchSize, Func<JObject, T> mapper, CancellationToken ct)
    {
        using var http = CreateHttpClient();

        // thumbnail_url is not null AND does not start with "posts/" (still an external URL)
        var url = $"{_supabaseUrl}/rest/v1/{table}" +
                  $"?thumbnail_url=not.is.null" +
                  $"&thumbnail_url=not.like.posts%2F%25" +
                  $"&order=created_at.asc" +
                  $"&limit={batchSize}" +
                  $"&select=id,thumbnail_url";

        var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {table} thumbnail fetch failed {(int)response.StatusCode}: {body}");

        var rows = JArray.Parse(body).Cast<JObject>().Select(mapper).ToList();
        _logger.LogInformation("Fetched {Count} pending thumbnail posts from {Table}", rows.Count, table);
        return rows;
    }

    public Task UpdateThumbnailUrlAsync(Guid id, string tableName, string b2Key, CancellationToken ct = default)
    {
        _logger.LogInformation("Updating thumbnail_url for post {Id} in {Table} → {Key}", id, tableName, b2Key);
        return PatchAsync(tableName, id, new { thumbnail_url = b2Key }, ct);
    }

    private static MigrationPostsRow MapPostsRow(JObject o) => new()
    {
        Id = Guid.Parse(o["id"]!.ToString()),
        OriginalRapidgatorUrls = o["original_rapidgator_url"]?.ToObject<List<string>>() ?? [],
        OurDownloadLink = o["our_download_link"]?.ToObject<List<string>>() ?? [],
        DownloadStatus = o["download_status"]?.ToString() ?? "pending",
        ThumbnailUrl = o["thumbnail_url"]?.ToString()
    };

    private static MigrationAsianScandalRow MapAsianScandalRow(JObject o) => new()
    {
        Id = Guid.Parse(o["id"]!.ToString()),
        OriginalRapidgatorUrls = o["original_rapidgator_url"]?.ToObject<List<string>>() ?? [],
        OurDownloadLink = o["our_download_link"]?.ToObject<List<string>>() ?? [],
        DownloadStatus = o["download_status"]?.ToString() ?? "pending",
        ThumbnailUrl = o["thumbnail_url"]?.ToString()
    };
}
