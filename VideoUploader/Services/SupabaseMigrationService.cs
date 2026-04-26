using VideoUploader.Configuration;
using VideoUploader.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace VideoUploader.Services;

public class SupabaseMigrationService
{
    private readonly string _url;
    private readonly string _serviceKey;
    private readonly ILogger<SupabaseMigrationService> _logger;

    public SupabaseMigrationService(
        IOptions<SupabaseSettings> settings,
        ILogger<SupabaseMigrationService> logger)
    {
        _url = settings.Value.Url;
        _serviceKey = settings.Value.ServiceKey;
        _logger = logger;
    }

    public async Task<List<PostRow>> FetchUnprocessedPostsAsync(int batchSize, CancellationToken ct = default)
    {
        using var http = CreateClient();
        var url = $"{_url}/rest/v1/posts" +
                  $"?is_streaming=eq.false" +
                  $"&our_download_link=neq.{{}}" +
                  $"&order=created_at.asc" +
                  $"&limit={batchSize}" +
                  $"&select=id,our_download_link";

        var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Supabase fetch failed {Status}: {Body}", response.StatusCode, body);
            return new();
        }

        var rows = JArray.Parse(body);
        var result = new List<PostRow>();
        foreach (var row in rows)
        {
            var id = row["id"]?.ToString();
            if (id == null) continue;

            var links = row["our_download_link"]?.ToObject<List<string>>() ?? new();
            result.Add(new PostRow { Id = Guid.Parse(id), OurDownloadLink = links });
        }

        _logger.LogInformation("Fetched {Count} unprocessed posts", result.Count);
        return result;
    }

    public async Task MarkStreamingDoneAsync(Guid postId, List<StreamVideo> videos, CancellationToken ct = default)
    {
        using var http = CreateClient();
        var url = $"{_url}/rest/v1/posts?id=eq.{postId}";

        var payload = new
        {
            is_streaming = true,
            stream_videos = videos
        };

        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.Add("Prefer", "return=minimal");

        var response = await http.PatchAsync(url, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Failed to update post {Id}: {Status} {Body}", postId, response.StatusCode, body);
            throw new Exception($"Supabase PATCH failed: {response.StatusCode}");
        }

        _logger.LogInformation("Marked post {Id} as streaming with {Count} video(s)", postId, videos.Count);
    }

    private HttpClient CreateClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("apikey", _serviceKey);
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_serviceKey}");
        return http;
    }
}
