# Bunny.net Video Streaming Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Abyss.to with Bunny.net Stream for video uploads, rename the AbyssUploader project to VideoUploader, and update the embedded player in KJSWeb.

**Architecture:** Two-step Bunny.net upload per file — POST to create video record (returns GUID), then PUT raw file bytes with known Content-Length. Pipeline (B2 download, extraction, Supabase update) is unchanged. KJSWeb reads `Bunny:LibraryId` from config and passes it to the carousel view.

**Tech Stack:** .NET 10, HttpClient (named client "Bunny" with AccessKey default header), Bunny.net Stream REST API (`https://video.bunnycdn.com`), Newtonsoft.Json, SharpCompress, AWSSDK.S3, Supabase REST PATCH.

---

## File Map

**Delete:**
- `AbyssUploader/Models/AbyssVideo.cs`
- `AbyssUploader/Services/AbyssUploadService.cs`
- `AbyssUploader/AbyssOrchestrator.cs`
- `KJSWeb/Models/AbyssVideo.cs`

**Rename (git mv):**
- `AbyssUploader/` → `VideoUploader/`
- `VideoUploader/AbyssUploader.csproj` → `VideoUploader/VideoUploader.csproj`

**Create:**
- `VideoUploader/Models/StreamVideo.cs`
- `VideoUploader/Services/BunnyUploadService.cs`
- `VideoUploader/VideoOrchestrator.cs`
- `KJSWeb/Models/StreamVideo.cs`

**Modify:**
- `KJSProject.sln` — update project name + path
- `VideoUploader/Configuration/AppSettings.cs` — BunnySettings replaces AbyssSettings
- `VideoUploader/appsettings.json` — Bunny section replaces AbyssUploader section
- `VideoUploader/Services/SupabaseMigrationService.cs` — field names + model type
- `VideoUploader/Services/B2DownloadService.cs` — namespace only
- `VideoUploader/Models/PostRow.cs` — namespace only
- `VideoUploader/Program.cs` — all references updated
- `KJSWeb/Models/Post.cs` — StreamVideos replaces AbyssVideos
- `KJSWeb/Views/Home/Details.cshtml` — Bunny embed URL + JS
- `KJSWeb/Controllers/HomeController.cs` — add ViewBag.BunnyLibraryId
- `KJSWeb/appsettings.json` — add Bunny:LibraryId

---

## Task 1: Rename project folder + csproj + update solution

**Files:**
- Rename: `AbyssUploader/` → `VideoUploader/`
- Rename: `VideoUploader/AbyssUploader.csproj` → `VideoUploader/VideoUploader.csproj`
- Modify: `KJSProject.sln`

- [ ] **Step 1: Rename the directory and csproj with git**

```bash
git mv AbyssUploader VideoUploader
git mv VideoUploader/AbyssUploader.csproj VideoUploader/VideoUploader.csproj
```

- [ ] **Step 2: Update KJSProject.sln**

Find this line:
```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "AbyssUploader", "AbyssUploader\AbyssUploader.csproj", "{B8F6008D-1F42-4811-B8CB-C3B672965F49}"
```
Replace with:
```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "VideoUploader", "VideoUploader\VideoUploader.csproj", "{B8F6008D-1F42-4811-B8CB-C3B672965F49}"
```
(The GUID stays the same — only the name and path change.)

- [ ] **Step 3: Verify solution loads**

```bash
dotnet sln KJSProject.sln list
```
Expected output includes `VideoUploader/VideoUploader.csproj` (not AbyssUploader).

---

## Task 2: Update BunnySettings config + appsettings.json

**Files:**
- Modify: `VideoUploader/Configuration/AppSettings.cs`
- Modify: `VideoUploader/appsettings.json`

- [ ] **Step 1: Replace AppSettings.cs**

Full file content for `VideoUploader/Configuration/AppSettings.cs`:
```csharp
namespace VideoUploader.Configuration;

public class SupabaseSettings
{
    public string Url { get; set; } = "";
    public string ServiceKey { get; set; } = "";
}

public class B2Settings
{
    public string ApplicationKeyId { get; set; } = "";
    public string ApplicationKey { get; set; } = "";
    public string BucketName { get; set; } = "";
    public string Region { get; set; } = "us-east-005";
    public string ServiceUrl { get; set; } = "https://s3.us-east-005.backblazeb2.com";
    public string PublicBaseUrl { get; set; } = "";
}

public class BunnySettings
{
    public string ApiKey { get; set; } = "";
    public string LibraryId { get; set; } = "";
    public double DailyLimitGb { get; set; } = 95;
    public int BatchSize { get; set; } = 5;
    public string TempFolder { get; set; } = "/tmp/video-uploader";
}
```

- [ ] **Step 2: Update appsettings.json**

Full file content for `VideoUploader/appsettings.json`:
```json
{
  "Supabase": {
    "Url": "https://djxlrniywyamhkfasczp.supabase.co",
    "ServiceKey": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImRqeGxybml5d3lhbWhrZmFzY3pwIiwicm9sZSI6InNlcnZpY2Vfcm9sZSIsImlhdCI6MTc3NTIyMDE5NSwiZXhwIjoyMDkwNzk2MTk1fQ.6BJBJHAJCUVzqp3oQjHX-K3M6MI4kQIvkv9SAXNTU6k"
  },
  "B2": {
    "ApplicationKeyId": "0055f95d9bf31ae0000000001",
    "ApplicationKey": "K005HxssTgGFg2kAV4IlmSL9jgxMXOs",
    "BucketName": "KJSProject",
    "Region": "us-east-005",
    "ServiceUrl": "https://s3.us-east-005.backblazeb2.com",
    "PublicBaseUrl": "https://f005.backblazeb2.com/file/KJSProject"
  },
  "Bunny": {
    "ApiKey": "",
    "LibraryId": "",
    "DailyLimitGb": 95,
    "BatchSize": 5,
    "TempFolder": "/tmp/video-uploader"
  }
}
```
Fill in `ApiKey` and `LibraryId` from the Bunny.net dashboard before running.

---

## Task 3: Create StreamVideo model in VideoUploader

**Files:**
- Delete: `VideoUploader/Models/AbyssVideo.cs`
- Create: `VideoUploader/Models/StreamVideo.cs`

- [ ] **Step 1: Delete old model**

```bash
rm VideoUploader/Models/AbyssVideo.cs
```

- [ ] **Step 2: Create StreamVideo.cs**

Full file content for `VideoUploader/Models/StreamVideo.cs`:
```csharp
using Newtonsoft.Json;

namespace VideoUploader.Models;

public class StreamVideo
{
    [JsonProperty("video_id")]
    public string VideoId { get; set; } = "";

    [JsonProperty("filename")]
    public string Filename { get; set; } = "";
}
```

- [ ] **Step 3: Update namespace in PostRow.cs**

Full file content for `VideoUploader/Models/PostRow.cs`:
```csharp
namespace VideoUploader.Models;

public class PostRow
{
    public Guid Id { get; set; }
    public List<string> OurDownloadLink { get; set; } = new();
}
```

---

## Task 4: Create BunnyUploadService

**Files:**
- Delete: `VideoUploader/Services/AbyssUploadService.cs`
- Create: `VideoUploader/Services/BunnyUploadService.cs`

- [ ] **Step 1: Delete old service**

```bash
rm VideoUploader/Services/AbyssUploadService.cs
```

- [ ] **Step 2: Create BunnyUploadService.cs**

Full file content for `VideoUploader/Services/BunnyUploadService.cs`:
```csharp
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
```

---

## Task 5: Create VideoOrchestrator

**Files:**
- Delete: `VideoUploader/AbyssOrchestrator.cs`
- Create: `VideoUploader/VideoOrchestrator.cs`

- [ ] **Step 1: Delete old orchestrator**

```bash
rm VideoUploader/AbyssOrchestrator.cs
```

- [ ] **Step 2: Create VideoOrchestrator.cs**

Full file content for `VideoUploader/VideoOrchestrator.cs`:
```csharp
using VideoUploader.Configuration;
using VideoUploader.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace VideoUploader;

public class VideoOrchestrator
{
    private readonly SupabaseMigrationService _supabase;
    private readonly B2DownloadService _b2;
    private readonly BunnyUploadService _bunny;
    private readonly BunnySettings _settings;
    private readonly ILogger<VideoOrchestrator> _logger;

    private const long BytesPerGb = 1024L * 1024 * 1024;

    public VideoOrchestrator(
        SupabaseMigrationService supabase,
        B2DownloadService b2,
        BunnyUploadService bunny,
        IOptions<BunnySettings> settings,
        ILogger<VideoOrchestrator> logger)
    {
        _supabase = supabase;
        _b2 = b2;
        _bunny = bunny;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var dailyLimitBytes = (long)(_settings.DailyLimitGb * BytesPerGb);
        long bytesUploadedThisRun = 0;

        _logger.LogInformation("VideoUploader starting. Daily limit: {LimitGb} GB", _settings.DailyLimitGb);

        var posts = await _supabase.FetchUnprocessedPostsAsync(_settings.BatchSize, ct);

        if (posts.Count == 0)
        {
            _logger.LogInformation("No unprocessed posts found. Exiting.");
            return;
        }

        Directory.CreateDirectory(_settings.TempFolder);

        foreach (var post in posts)
        {
            ct.ThrowIfCancellationRequested();

            if (bytesUploadedThisRun >= dailyLimitBytes)
            {
                _logger.LogWarning("Daily upload limit reached ({Gb} GB). Stopping.", _settings.DailyLimitGb);
                break;
            }

            var postTempDir = Path.Combine(_settings.TempFolder, post.Id.ToString());
            try
            {
                _logger.LogInformation("Processing post {Id} ({Count} zip(s))", post.Id, post.OurDownloadLink.Count);

                var extractDir = Path.Combine(postTempDir, "extracted");
                Directory.CreateDirectory(extractDir);

                foreach (var zipUrl in post.OurDownloadLink)
                {
                    ct.ThrowIfCancellationRequested();
                    var zipFilename = Path.GetFileName(zipUrl.Replace('\\', '/'));
                    var zipPath = Path.Combine(postTempDir, zipFilename);
                    await _b2.DownloadAsync(zipUrl, zipPath, ct);
                    ExtractArchive(zipPath, extractDir);
                    File.Delete(zipPath);
                }

                var videoSize = BunnyUploadService.GetVideoFilesSize(extractDir);
                if (bytesUploadedThisRun + videoSize > dailyLimitBytes)
                {
                    _logger.LogWarning("Post {Id} would exceed daily limit, stopping", post.Id);
                    break;
                }

                var videos = await _bunny.UploadVideosAsync(extractDir, ct);

                await _supabase.MarkStreamingDoneAsync(post.Id, videos, ct);

                bytesUploadedThisRun += videoSize;
                _logger.LogInformation("Post {Id} done. {Count} video(s) uploaded. Run total: {Gb:F2} GB",
                    post.Id, videos.Count, bytesUploadedThisRun / (double)BytesPerGb);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing post {Id} — skipping", post.Id);
            }
            finally
            {
                if (Directory.Exists(postTempDir))
                {
                    try { Directory.Delete(postTempDir, recursive: true); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete temp dir {Dir}", postTempDir); }
                }
            }
        }

        _logger.LogInformation("Run complete. Total uploaded this run: {Gb:F2} GB",
            bytesUploadedThisRun / (double)BytesPerGb);
    }

    private void ExtractArchive(string archivePath, string destDir)
    {
        _logger.LogInformation("Extracting {Archive} → {Dir}", archivePath, destDir);
        using var archive = ArchiveFactory.Open(archivePath);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            entry.WriteToDirectory(destDir, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });
        }
    }
}
```

---

## Task 6: Update SupabaseMigrationService + B2DownloadService namespaces

**Files:**
- Modify: `VideoUploader/Services/SupabaseMigrationService.cs`
- Modify: `VideoUploader/Services/B2DownloadService.cs`

- [ ] **Step 1: Rewrite SupabaseMigrationService.cs**

Full file content for `VideoUploader/Services/SupabaseMigrationService.cs`:
```csharp
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
```

- [ ] **Step 2: Update namespace in B2DownloadService.cs**

Change only the first line of `VideoUploader/Services/B2DownloadService.cs`:
```csharp
// Line 1: was
using AbyssUploader.Configuration;
// Change to:
using VideoUploader.Configuration;
```

And line with `namespace`:
```csharp
// was
namespace AbyssUploader.Services;
// change to
namespace VideoUploader.Services;
```

---

## Task 7: Update Program.cs

**Files:**
- Modify: `VideoUploader/Program.cs`

- [ ] **Step 1: Replace Program.cs entirely**

Full file content for `VideoUploader/Program.cs`:
```csharp
using VideoUploader;
using VideoUploader.Configuration;
using VideoUploader.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureAppConfiguration((context, config) =>
{
    config
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true)
        .AddEnvironmentVariables();
});

builder.ConfigureServices((context, services) =>
{
    services.Configure<SupabaseSettings>(context.Configuration.GetSection("Supabase"));
    services.Configure<B2Settings>(context.Configuration.GetSection("B2"));
    services.Configure<BunnySettings>(context.Configuration.GetSection("Bunny"));

    services.AddHttpClient("Bunny", (sp, client) =>
    {
        var settings = sp.GetRequiredService<IOptions<BunnySettings>>().Value;
        client.DefaultRequestHeaders.Add("AccessKey", settings.ApiKey);
        client.Timeout = Timeout.InfiniteTimeSpan;
    });

    services.AddSingleton<SupabaseMigrationService>();
    services.AddSingleton<B2DownloadService>();
    services.AddSingleton<BunnyUploadService>();
    services.AddSingleton<VideoOrchestrator>();
});

builder.ConfigureLogging((_, logging) =>
{
    logging.ClearProviders();
    logging.AddConsole();
});

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var orchestrator = host.Services.GetRequiredService<VideoOrchestrator>();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.LogInformation("Cancellation requested");
    cts.Cancel();
};

try
{
    await orchestrator.RunAsync(cts.Token);
    logger.LogInformation("VideoUploader completed successfully");
}
catch (OperationCanceledException)
{
    logger.LogInformation("VideoUploader cancelled");
}
catch (Exception ex)
{
    logger.LogError(ex, "VideoUploader failed");
    Environment.Exit(1);
}
```

---

## Task 8: Build VideoUploader — verify 0 errors

**Files:** none

- [ ] **Step 1: Build**

```bash
dotnet build VideoUploader/VideoUploader.csproj
```

Expected:
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

If errors appear, fix them before continuing. Common issues: remaining `AbyssUploader` namespace reference, missing `using` statement.

---

## Task 9: Update KJSWeb — StreamVideo model + Post.cs

**Files:**
- Delete: `KJSWeb/Models/AbyssVideo.cs`
- Create: `KJSWeb/Models/StreamVideo.cs`
- Modify: `KJSWeb/Models/Post.cs`

- [ ] **Step 1: Delete KJSWeb/Models/AbyssVideo.cs**

```bash
rm KJSWeb/Models/AbyssVideo.cs
```

- [ ] **Step 2: Create KJSWeb/Models/StreamVideo.cs**

Full file content:
```csharp
using System.Text.Json.Serialization;

namespace KJSWeb.Models;

public class StreamVideo
{
    [JsonPropertyName("video_id")]
    public string VideoId { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";
}
```

- [ ] **Step 3: Update KJSWeb/Models/Post.cs**

Replace the last two lines of the class (the `is_streaming` and `abyss_videos` columns):
```csharp
// Remove these two properties:
    [Column("is_streaming")]
    public bool IsStreaming { get; set; }

    [Column("abyss_videos")]
    public List<AbyssVideo> AbyssVideos { get; set; } = new();

// Replace with:
    [Column("is_streaming")]
    public bool IsStreaming { get; set; }

    [Column("stream_videos")]
    public List<StreamVideo> StreamVideos { get; set; } = new();
```

---

## Task 10: Update Details.cshtml — Bunny embed + JS carousel

**Files:**
- Modify: `KJSWeb/Views/Home/Details.cshtml`

- [ ] **Step 1: Update the streaming section (lines 57–117)**

Replace the entire `@if (Model.IsStreaming && Model.AbyssVideos.Any())` block with:

```cshtml
@if (Model.IsStreaming && Model.StreamVideos.Any())
{
    var videos = Model.StreamVideos;
    var single = videos.Count == 1;
    var libraryId = (string)(ViewBag.BunnyLibraryId ?? "");
    <div class="mb-10">
        <h2 class="text-sm font-black text-slate-500 uppercase tracking-widest mb-4">@Localizer["Watch Online"]</h2>

        <div class="relative">
            @if (!single)
            {
                <button onclick="carousel.prev()" class="absolute left-0 top-1/2 -translate-y-1/2 -translate-x-4 z-10 w-9 h-9 flex items-center justify-center bg-black/70 hover:bg-orange-accent text-white rounded-full transition-colors" aria-label="Previous">&#8592;</button>
                <button onclick="carousel.next()" class="absolute right-0 top-1/2 -translate-y-1/2 translate-x-4 z-10 w-9 h-9 flex items-center justify-center bg-black/70 hover:bg-orange-accent text-white rounded-full transition-colors" aria-label="Next">&#8594;</button>
            }

            <div class="relative w-full" style="padding-bottom:56.25%;">
                <iframe id="stream-player"
                        src="https://iframe.mediadelivery.net/embed/@libraryId/@videos[0].VideoId"
                        allowfullscreen
                        class="absolute inset-0 w-full h-full rounded-sm border border-white/10"
                        frameborder="0">
                </iframe>
            </div>
        </div>

        @if (!single)
        {
            <div class="flex gap-2 mt-3 overflow-x-auto pb-1">
                @for (int i = 0; i < videos.Count; i++)
                {
                    <button onclick="carousel.go(@i)"
                            id="stream-thumb-@i"
                            class="flex-shrink-0 px-4 py-2 text-xs font-bold uppercase tracking-wider rounded-sm border transition-colors @(i == 0 ? "border-orange-accent text-orange-accent bg-orange-accent/10" : "border-white/10 text-slate-400 hover:border-white/30")">
                        Video @(i + 1)
                    </button>
                }
            </div>
        }
    </div>

    <script>
        (function () {
            var ids = [@Html.Raw(string.Join(", ", videos.Select(v => $"'{v.VideoId}'")))];
            var libraryId = '@libraryId';
            var current = 0;
            window.carousel = {
                go: function (index) {
                    current = index;
                    document.getElementById('stream-player').src =
                        'https://iframe.mediadelivery.net/embed/' + libraryId + '/' + ids[index];
                    document.querySelectorAll('[id^="stream-thumb-"]').forEach(function (btn, i) {
                        btn.classList.toggle('border-orange-accent', i === index);
                        btn.classList.toggle('text-orange-accent', i === index);
                        btn.classList.toggle('bg-orange-accent/10', i === index);
                        btn.classList.toggle('border-white/10', i !== index);
                        btn.classList.toggle('text-slate-400', i !== index);
                    });
                },
                prev: function () { this.go((current - 1 + ids.length) % ids.length); },
                next: function () { this.go((current + 1) % ids.length); }
            };
        })();
    </script>
}
```

---

## Task 11: Update HomeController + KJSWeb appsettings

**Files:**
- Modify: `KJSWeb/Controllers/HomeController.cs`
- Modify: `KJSWeb/appsettings.json`

- [ ] **Step 1: Add BunnyLibraryId ViewBag in HomeController Details action**

In `KJSWeb/Controllers/HomeController.cs`, find the Details action where `ViewBag.WorkerBaseUrl` and `ViewBag.B2DirectBase` are set (around line 141–142). Add one line immediately after:

```csharp
ViewBag.WorkerBaseUrl = workerBaseUrl;
ViewBag.B2DirectBase = _config["B2:PublicBaseUrl"]?.TrimEnd('/') ?? "https://f005.backblazeb2.com/file/KJSProject";
ViewBag.BunnyLibraryId = _config["Bunny:LibraryId"] ?? "";
```

- [ ] **Step 2: Add Bunny section to KJSWeb/appsettings.json**

Open `KJSWeb/appsettings.json` and add a `"Bunny"` section. Find the end of the JSON object and add before the closing `}`:

```json
"Bunny": {
  "LibraryId": ""
}
```

Fill in the Library ID from the Bunny.net Stream dashboard (same Library ID used in the VideoUploader config).

---

## Task 12: Build KJSWeb — verify 0 errors

- [ ] **Step 1: Build**

```bash
dotnet build KJSWeb/KJSWeb.csproj
```

Expected:
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

---

## Task 13: Run Supabase SQL column rename

- [ ] **Step 1: Run SQL in Supabase dashboard**

Go to Supabase → SQL Editor and run:
```sql
ALTER TABLE posts RENAME COLUMN abyss_videos TO stream_videos;
```

Expected: "Success. No rows returned."

- [ ] **Step 2: Fill in VideoUploader credentials**

In `VideoUploader/appsettings.json`, set:
- `Bunny:ApiKey` — from Bunny.net → Stream → Your Library → API
- `Bunny:LibraryId` — the numeric ID shown in the Bunny.net Stream library URL

- [ ] **Step 3: Test-run VideoUploader**

```bash
dotnet run --project VideoUploader/VideoUploader.csproj
```

Expected log lines:
```
VideoUploader starting. Daily limit: 95 GB
Fetched N unprocessed posts
Processing post ...
Uploading filename.mp4 (X.X MB) to Bunny.net
Uploaded filename.mp4 → guid: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
Marked post ... as streaming with N video(s)
```

---

## Task 14: Commit and push

- [ ] **Step 1: Stage all changes**

```bash
git add VideoUploader/ KJSWeb/Models/StreamVideo.cs KJSWeb/Models/AbyssVideo.cs KJSWeb/Models/Post.cs KJSWeb/Controllers/HomeController.cs KJSWeb/Views/Home/Details.cshtml KJSWeb/appsettings.json KJSProject.sln
```

- [ ] **Step 2: Commit**

```bash
git commit -m "feat: migrate video streaming from Abyss.to to Bunny.net

- Rename AbyssUploader project to VideoUploader
- Replace AbyssUploadService (curl) with BunnyUploadService (HttpClient PUT)
- Rename abyss_videos DB column to stream_videos
- Update player embed to Bunny iframe.mediadelivery.net
- Update model AbyssVideo → StreamVideo with VideoId instead of Slug"
```

- [ ] **Step 3: Push**

```bash
git push
```
