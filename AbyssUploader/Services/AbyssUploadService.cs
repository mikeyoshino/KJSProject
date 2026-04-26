using AbyssUploader.Configuration;
using AbyssUploader.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace AbyssUploader.Services;

public class AbyssUploadService
{
    private readonly AbyssSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AbyssUploadService> _logger;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm"
    };

    public AbyssUploadService(
        IOptions<AbyssSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<AbyssUploadService> logger)
    {
        _settings = settings.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Recursively finds all video files under extractedDir and uploads each to Abyss.to.
    /// Returns list of {slug, filename} pairs. Returns empty list if no videos found.
    /// </summary>
    public async Task<List<AbyssVideo>> UploadVideosAsync(string extractedDir, CancellationToken ct = default)
    {
        var videoFiles = Directory
            .EnumerateFiles(extractedDir, "*", SearchOption.AllDirectories)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f)
            .ToList();

        if (videoFiles.Count == 0)
        {
            _logger.LogInformation("No video files found in {Dir}", extractedDir);
            return new();
        }

        _logger.LogInformation("Found {Count} video file(s) in {Dir}", videoFiles.Count, extractedDir);

        var results = new List<AbyssVideo>();
        foreach (var videoPath in videoFiles)
        {
            ct.ThrowIfCancellationRequested();
            var slug = await UploadSingleAsync(videoPath, ct);
            if (slug != null)
            {
                results.Add(new AbyssVideo
                {
                    Slug = slug,
                    Filename = Path.GetFileName(videoPath)
                });
            }
        }

        return results;
    }

    public static long GetVideoFilesSize(string extractedDir)
    {
        if (!Directory.Exists(extractedDir)) return 0;
        return Directory
            .EnumerateFiles(extractedDir, "*", SearchOption.AllDirectories)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
            .Sum(f => new FileInfo(f).Length);
    }

    private async Task<string?> UploadSingleAsync(string filePath, CancellationToken ct)
    {
        var filename = Path.GetFileName(filePath);
        _logger.LogInformation("Uploading {File} to Abyss.to", filename);

        try
        {
            using var http = _httpClientFactory.CreateClient("Abyss");

            await using var stream = File.OpenRead(filePath);
            using var content = new MultipartFormDataContent();
            using var fileContent = new StreamContent(stream);
            content.Add(fileContent, "file", filename);

            var uploadUrl = $"{_settings.ApiBaseUrl}/{_settings.ApiKey}";
            var response = await http.PostAsync(uploadUrl, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Abyss.to upload failed for {File}: {Status} {Body}", filename, response.StatusCode, body);
                return null;
            }

            var json = JObject.Parse(body);
            var slug = json["slug"]?.ToString();
            if (string.IsNullOrEmpty(slug))
            {
                _logger.LogError("Abyss.to returned no slug for {File}: {Body}", filename, body);
                return null;
            }

            _logger.LogInformation("Uploaded {File} → slug: {Slug}", filename, slug);
            return slug;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception uploading {File}", filePath);
            return null;
        }
    }
}
