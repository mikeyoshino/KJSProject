using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;
using VideoUploader.Configuration;
using VideoUploader.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VideoUploader.Services;

public class BunnyUploadService
{
    private readonly BunnySettings _settings;
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<BunnyUploadService> _logger;

    private const string ApiBase = "https://video.bunnycdn.com";

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm"
    };

    public BunnyUploadService(
        IOptions<BunnySettings> settings,
        IHttpClientFactory factory,
        ILogger<BunnyUploadService> logger)
    {
        _settings = settings.Value;
        _factory = factory;
        _logger = logger;
    }

    public async Task<List<StreamVideo>> UploadVideosAsync(string extractedDir, CancellationToken ct = default)
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

        var results = new List<StreamVideo>();
        foreach (var videoPath in videoFiles)
        {
            ct.ThrowIfCancellationRequested();
            var videoId = await UploadSingleAsync(videoPath, ct);
            if (videoId != null)
                results.Add(new StreamVideo { VideoId = videoId, Filename = Path.GetFileName(videoPath) });
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
        var sizeMb = new FileInfo(filePath).Length / 1024.0 / 1024.0;
        _logger.LogInformation("Uploading {File} ({Size:F1} MB) to Bunny.net", filename, sizeMb);

        try
        {
            var http = _factory.CreateClient("Bunny");

            // Step 1: create video record → get GUID
            var createUrl = $"{ApiBase}/library/{_settings.LibraryId}/videos";
            var createBody = new StringContent(
                $"{{\"title\":\"{filename}\"}}",
                Encoding.UTF8,
                "application/json");

            var createResp = await http.PostAsync(createUrl, createBody, ct);
            var createJson = await createResp.Content.ReadAsStringAsync(ct);

            if (!createResp.IsSuccessStatusCode)
            {
                _logger.LogError("Bunny create-video failed for {File}: {Status} {Body}",
                    filename, createResp.StatusCode, createJson);
                return null;
            }

            var guid = JObject.Parse(createJson)["guid"]?.ToString();
            if (string.IsNullOrEmpty(guid))
            {
                _logger.LogError("Bunny returned no guid for {File}: {Body}", filename, createJson);
                return null;
            }

            // Step 2: PUT raw bytes with known Content-Length
            var uploadUrl = $"{ApiBase}/library/{_settings.LibraryId}/videos/{guid}";
            var fileLength = new FileInfo(filePath).Length;
            await using var stream = File.OpenRead(filePath);

            var uploadContent = new StreamContent(stream);
            uploadContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            uploadContent.Headers.ContentLength = fileLength;

            var uploadRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = uploadContent
            };
            var uploadResp = await http.SendAsync(uploadRequest, ct);

            if (!uploadResp.IsSuccessStatusCode)
            {
                var uploadBody = await uploadResp.Content.ReadAsStringAsync(ct);
                _logger.LogError("Bunny upload failed for {File}: {Status} {Body}",
                    filename, uploadResp.StatusCode, uploadBody);
                return null;
            }

            _logger.LogInformation("Uploaded {File} → guid: {Guid}", filename, guid);
            return guid;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception uploading {File}", filePath);
            return null;
        }
    }
}
