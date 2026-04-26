# Abyss.to Video Streaming Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a daily-cron console tool (`AbyssUploader`) that downloads zip archives from B2, extracts video files, uploads them to Abyss.to, and updates the `posts` table — then surface those videos as a public iframe carousel on the post detail page in KJSWeb.

**Architecture:** Single-pass sequential pipeline — fetch unprocessed posts → download zip from B2 via S3 SDK → extract with SharpCompress → upload video files to Abyss.to multipart API → PATCH post row with `abyss_videos` JSON + `is_streaming = true`. KJSWeb reads the new columns via the Supabase SDK and renders a carousel in `Details.cshtml`.

**Tech Stack:** .NET 10, AWSSDK.S3, SharpCompress, Newtonsoft.Json, Microsoft.Extensions.Hosting, Supabase REST API (raw HTTP), Abyss.to upload API, Tailwind CSS + vanilla JS for carousel.

---

## File Map

**New files — AbyssUploader project:**
- `AbyssUploader/AbyssUploader.csproj`
- `AbyssUploader/appsettings.json`
- `AbyssUploader/Program.cs`
- `AbyssUploader/Configuration/AppSettings.cs`
- `AbyssUploader/Models/AbyssVideo.cs`
- `AbyssUploader/Models/PostRow.cs`
- `AbyssUploader/Services/SupabaseMigrationService.cs`
- `AbyssUploader/Services/B2DownloadService.cs`
- `AbyssUploader/Services/AbyssUploadService.cs`
- `AbyssUploader/AbyssOrchestrator.cs`

**Modified — solution:**
- `KJSProject.sln` — add AbyssUploader project

**Modified — KJSWeb:**
- `KJSWeb/Models/AbyssVideo.cs` — new shared model
- `KJSWeb/Models/Post.cs` — add `IsStreaming`, `AbyssVideos`
- `KJSWeb/Services/SupabaseService.cs` — add `UpdateAbyssVideosAsync`
- `KJSWeb/Views/Home/Details.cshtml` — add carousel above download section

---

## Task 1: Add DB Columns in Supabase

**Files:** (manual step — Supabase dashboard)

- [ ] **Step 1: Run SQL in Supabase SQL editor**

Go to your Supabase project → SQL Editor → run:

```sql
ALTER TABLE posts ADD COLUMN IF NOT EXISTS is_streaming boolean DEFAULT false;
ALTER TABLE posts ADD COLUMN IF NOT EXISTS abyss_videos jsonb;
```

- [ ] **Step 2: Verify columns exist**

In Supabase Table Editor, open `posts` table and confirm `is_streaming` (bool, default false) and `abyss_videos` (jsonb, nullable) columns are present.

---

## Task 2: Scaffold AbyssUploader Project

**Files:**
- Create: `AbyssUploader/AbyssUploader.csproj`
- Modify: `KJSProject.sln`

- [ ] **Step 1: Create project directory and csproj**

```bash
mkdir -p /path/to/KJSProject/AbyssUploader/Services
mkdir -p /path/to/KJSProject/AbyssUploader/Configuration
mkdir -p /path/to/KJSProject/AbyssUploader/Models
```

Create `AbyssUploader/AbyssUploader.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="AWSSDK.S3" Version="4.0.20.3" />
    <PackageReference Include="SharpCompress" Version="0.37.2" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.4" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Console" Version="10.0.0" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add project to solution**

```bash
dotnet sln KJSProject.sln add AbyssUploader/AbyssUploader.csproj
```

Expected output: `Project 'AbyssUploader/AbyssUploader.csproj' added to the solution.`

---

## Task 3: Configuration Classes

**Files:**
- Create: `AbyssUploader/Configuration/AppSettings.cs`
- Create: `AbyssUploader/appsettings.json`

- [ ] **Step 1: Create `AbyssUploader/Configuration/AppSettings.cs`**

```csharp
namespace AbyssUploader.Configuration;

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

public class AbyssSettings
{
    public string ApiKey { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "http://up.abyss.to";
    public double DailyLimitGb { get; set; } = 95;
    public int BatchSize { get; set; } = 50;
    public string TempFolder { get; set; } = "/tmp/abyss-uploader";
}
```

- [ ] **Step 2: Create `AbyssUploader/appsettings.json`**

```json
{
  "Supabase": {
    "Url": "",
    "ServiceKey": ""
  },
  "B2": {
    "ApplicationKeyId": "",
    "ApplicationKey": "",
    "BucketName": "KJSProject",
    "Region": "us-east-005",
    "ServiceUrl": "https://s3.us-east-005.backblazeb2.com",
    "PublicBaseUrl": "https://f005.backblazeb2.com/file/KJSProject"
  },
  "AbyssUploader": {
    "ApiKey": "",
    "ApiBaseUrl": "http://up.abyss.to",
    "DailyLimitGb": 95,
    "BatchSize": 50,
    "TempFolder": "/tmp/abyss-uploader"
  }
}
```

---

## Task 4: Models

**Files:**
- Create: `AbyssUploader/Models/AbyssVideo.cs`
- Create: `AbyssUploader/Models/PostRow.cs`

- [ ] **Step 1: Create `AbyssUploader/Models/AbyssVideo.cs`**

```csharp
using Newtonsoft.Json;

namespace AbyssUploader.Models;

public class AbyssVideo
{
    [JsonProperty("slug")]
    public string Slug { get; set; } = "";

    [JsonProperty("filename")]
    public string Filename { get; set; } = "";
}
```

- [ ] **Step 2: Create `AbyssUploader/Models/PostRow.cs`**

```csharp
namespace AbyssUploader.Models;

public class PostRow
{
    public Guid Id { get; set; }
    public List<string> OurDownloadLink { get; set; } = new();
}
```

---

## Task 5: SupabaseMigrationService

**Files:**
- Create: `AbyssUploader/Services/SupabaseMigrationService.cs`

- [ ] **Step 1: Create `AbyssUploader/Services/SupabaseMigrationService.cs`**

```csharp
using AbyssUploader.Configuration;
using AbyssUploader.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace AbyssUploader.Services;

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

    public async Task MarkStreamingDoneAsync(Guid postId, List<AbyssVideo> videos, CancellationToken ct = default)
    {
        using var http = CreateClient();
        var url = $"{_url}/rest/v1/posts?id=eq.{postId}";

        var payload = new
        {
            is_streaming = true,
            abyss_videos = videos
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

---

## Task 6: B2DownloadService

**Files:**
- Create: `AbyssUploader/Services/B2DownloadService.cs`

- [ ] **Step 1: Create `AbyssUploader/Services/B2DownloadService.cs`**

```csharp
using AbyssUploader.Configuration;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AbyssUploader.Services;

public class B2DownloadService
{
    private readonly B2Settings _b2;
    private readonly ILogger<B2DownloadService> _logger;

    public B2DownloadService(IOptions<B2Settings> settings, ILogger<B2DownloadService> logger)
    {
        _b2 = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Downloads a B2 public URL to a local file path.
    /// The public URL format is: {PublicBaseUrl}/{objectKey}
    /// e.g. https://f005.backblazeb2.com/file/KJSProject/posts/abc/file.zip
    /// </summary>
    public async Task<string> DownloadAsync(string b2Url, string destPath, CancellationToken ct = default)
    {
        var objectKey = ExtractObjectKey(b2Url);
        _logger.LogInformation("Downloading B2 object: {Key} → {Dest}", objectKey, destPath);

        var config = new AmazonS3Config
        {
            ServiceURL = _b2.ServiceUrl,
            AuthenticationRegion = _b2.Region,
        };

        using var s3 = new AmazonS3Client(_b2.ApplicationKeyId, _b2.ApplicationKey, config);

        var request = new GetObjectRequest
        {
            BucketName = _b2.BucketName,
            Key = objectKey
        };

        using var response = await s3.GetObjectAsync(request, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        await using var fs = File.Create(destPath);
        await response.ResponseStream.CopyToAsync(fs, ct);

        _logger.LogInformation("Downloaded {Bytes} bytes to {Dest}", fs.Length, destPath);
        return destPath;
    }

    private string ExtractObjectKey(string b2Url)
    {
        var prefix = _b2.PublicBaseUrl.TrimEnd('/') + "/";
        if (b2Url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return b2Url[prefix.Length..];

        // Fallback: strip scheme+host+/file/{bucket}/
        var uri = new Uri(b2Url);
        var segments = uri.AbsolutePath.TrimStart('/').Split('/', 3);
        // segments: ["file", "KJSProject", "rest/of/key"]
        return segments.Length >= 3 ? segments[2] : uri.AbsolutePath.TrimStart('/');
    }
}
```

---

## Task 7: AbyssUploadService

**Files:**
- Create: `AbyssUploader/Services/AbyssUploadService.cs`

- [ ] **Step 1: Create `AbyssUploader/Services/AbyssUploadService.cs`**

```csharp
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
            http.Timeout = Timeout.InfiniteTimeSpan;

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
```

---

## Task 8: AbyssOrchestrator

**Files:**
- Create: `AbyssUploader/AbyssOrchestrator.cs`

- [ ] **Step 1: Create `AbyssUploader/AbyssOrchestrator.cs`**

```csharp
using AbyssUploader.Configuration;
using AbyssUploader.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace AbyssUploader;

public class AbyssOrchestrator
{
    private readonly SupabaseMigrationService _supabase;
    private readonly B2DownloadService _b2;
    private readonly AbyssUploadService _abyss;
    private readonly AbyssSettings _settings;
    private readonly ILogger<AbyssOrchestrator> _logger;

    private const long BytesPerGb = 1024L * 1024 * 1024;

    public AbyssOrchestrator(
        SupabaseMigrationService supabase,
        B2DownloadService b2,
        AbyssUploadService abyss,
        IOptions<AbyssSettings> settings,
        ILogger<AbyssOrchestrator> logger)
    {
        _supabase = supabase;
        _b2 = b2;
        _abyss = abyss;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var dailyLimitBytes = (long)(_settings.DailyLimitGb * BytesPerGb);
        long bytesUploadedThisRun = 0;

        _logger.LogInformation("AbyssUploader starting. Daily limit: {LimitGb} GB", _settings.DailyLimitGb);

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

                // Download and extract each zip
                foreach (var zipUrl in post.OurDownloadLink)
                {
                    ct.ThrowIfCancellationRequested();
                    var zipPath = Path.Combine(postTempDir, Path.GetFileName(new Uri(zipUrl).LocalPath));
                    await _b2.DownloadAsync(zipUrl, zipPath, ct);
                    ExtractArchive(zipPath, extractDir);
                    File.Delete(zipPath);
                }

                // Check daily limit before uploading
                var videoSize = AbyssUploadService.GetVideoFilesSize(extractDir);
                if (bytesUploadedThisRun + videoSize > dailyLimitBytes)
                {
                    _logger.LogWarning("Post {Id} would exceed daily limit, stopping", post.Id);
                    break;
                }

                // Upload videos to Abyss.to
                var videos = await _abyss.UploadVideosAsync(extractDir, ct);

                // Mark done (even if no videos — avoids infinite retry)
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
                // Always clean up temp files for this post
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

## Task 9: Program.cs

**Files:**
- Create: `AbyssUploader/Program.cs`

- [ ] **Step 1: Create `AbyssUploader/Program.cs`**

```csharp
using AbyssUploader;
using AbyssUploader.Configuration;
using AbyssUploader.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    services.Configure<AbyssSettings>(context.Configuration.GetSection("AbyssUploader"));

    services.AddHttpClient("Abyss", client => client.Timeout = Timeout.InfiniteTimeSpan);

    services.AddSingleton<SupabaseMigrationService>();
    services.AddSingleton<B2DownloadService>();
    services.AddSingleton<AbyssUploadService>();
    services.AddSingleton<AbyssOrchestrator>();
});

builder.ConfigureLogging((_, logging) =>
{
    logging.ClearProviders();
    logging.AddConsole();
});

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var orchestrator = host.Services.GetRequiredService<AbyssOrchestrator>();

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
    logger.LogInformation("AbyssUploader completed successfully");
}
catch (OperationCanceledException)
{
    logger.LogInformation("AbyssUploader cancelled");
}
catch (Exception ex)
{
    logger.LogError(ex, "AbyssUploader failed");
    Environment.Exit(1);
}
```

- [ ] **Step 2: Build AbyssUploader and verify it compiles**

```bash
dotnet build AbyssUploader/AbyssUploader.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit AbyssUploader project**

```bash
git add AbyssUploader/ KJSProject.sln
git commit -m "feat: add AbyssUploader console project for Abyss.to video streaming"
```

---

## Task 10: KJSWeb — AbyssVideo Model

**Files:**
- Create: `KJSWeb/Models/AbyssVideo.cs`
- Modify: `KJSWeb/Models/Post.cs`

- [ ] **Step 1: Create `KJSWeb/Models/AbyssVideo.cs`**

```csharp
using System.Text.Json.Serialization;

namespace KJSWeb.Models;

public class AbyssVideo
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";
}
```

- [ ] **Step 2: Add `IsStreaming` and `AbyssVideos` to `KJSWeb/Models/Post.cs`**

Add these two properties at the end of the `Post` class, before the closing `}`:

```csharp
    [Column("is_streaming")]
    public bool IsStreaming { get; set; }

    [Column("abyss_videos")]
    public List<AbyssVideo> AbyssVideos { get; set; } = new();
```

- [ ] **Step 3: Build KJSWeb to verify**

```bash
dotnet build KJSWeb/KJSWeb.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add KJSWeb/Models/AbyssVideo.cs KJSWeb/Models/Post.cs
git commit -m "feat: add IsStreaming and AbyssVideos to Post model"
```

---

## Task 11: KJSWeb — Video Carousel in Details View

**Files:**
- Modify: `KJSWeb/Views/Home/Details.cshtml`

- [ ] **Step 1: Add carousel above the `_PremiumDownload` partial in `KJSWeb/Views/Home/Details.cshtml`**

Find this block (around line 56):

```cshtml
            @if (Model.OurDownloadLink != null && Model.OurDownloadLink.Any())
            {
                @await Html.PartialAsync("_PremiumDownload", ...
```

Insert the following **above** that block:

```cshtml
            @* ── Video Carousel ───────────────────────────────────────────── *@
            @if (Model.IsStreaming && Model.AbyssVideos.Any())
            {
                var videos = Model.AbyssVideos;
                var single = videos.Count == 1;
                <div class="mb-10" x-data="videoCarousel(@videos.Count)" x-init="init()">
                    <h2 class="text-sm font-black text-slate-500 uppercase tracking-widest mb-4">@Localizer["Watch Online"]</h2>

                    <div class="relative">
                        @if (!single)
                        {
                            <button onclick="carousel.prev()" class="absolute left-0 top-1/2 -translate-y-1/2 -translate-x-4 z-10 w-9 h-9 flex items-center justify-center bg-black/70 hover:bg-orange-accent text-white rounded-full transition-colors" aria-label="Previous">&#8592;</button>
                            <button onclick="carousel.next()" class="absolute right-0 top-1/2 -translate-y-1/2 translate-x-4 z-10 w-9 h-9 flex items-center justify-center bg-black/70 hover:bg-orange-accent text-white rounded-full transition-colors" aria-label="Next">&#8594;</button>
                        }

                        <div class="relative w-full" style="padding-bottom:56.25%;">
                            <iframe id="abyss-player"
                                    src="https://abysscdn.com/?v=@videos[0].Slug"
                                    allowfullscreen
                                    class="absolute inset-0 w-full h-full rounded-sm border border-white/10"
                                    frameborder="0">
                            </iframe>
                        </div>
                    </div>

                    @if (!single)
                    {
                        <div class="flex gap-2 mt-3 overflow-x-auto pb-1" id="abyss-strip">
                            @for (int i = 0; i < videos.Count; i++)
                            {
                                var label = $"Video {i + 1}";
                                var isActive = i == 0;
                                <button onclick="carousel.go(@i)"
                                        id="abyss-thumb-@i"
                                        class="flex-shrink-0 px-4 py-2 text-xs font-bold uppercase tracking-wider rounded-sm border transition-colors @(isActive ? "border-orange-accent text-orange-accent bg-orange-accent/10" : "border-white/10 text-slate-400 hover:border-white/30")">
                                    @label
                                </button>
                            }
                        </div>
                    }
                </div>

                <script>
                    (function () {
                        var slugs = [@Html.Raw(string.Join(", ", videos.Select(v => $"'{v.Slug}'")))];
                        var current = 0;

                        window.carousel = {
                            go: function (index) {
                                current = index;
                                document.getElementById('abyss-player').src = 'https://abysscdn.com/?v=' + slugs[index];
                                document.querySelectorAll('[id^="abyss-thumb-"]').forEach(function (btn, i) {
                                    btn.classList.toggle('border-orange-accent', i === index);
                                    btn.classList.toggle('text-orange-accent', i === index);
                                    btn.classList.toggle('bg-orange-accent/10', i === index);
                                    btn.classList.toggle('border-white/10', i !== index);
                                    btn.classList.toggle('text-slate-400', i !== index);
                                });
                            },
                            prev: function () {
                                this.go((current - 1 + slugs.length) % slugs.length);
                            },
                            next: function () {
                                this.go((current + 1) % slugs.length);
                            }
                        };
                    })();
                </script>
            }
```

- [ ] **Step 2: Build KJSWeb to verify no compile errors**

```bash
dotnet build KJSWeb/KJSWeb.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add KJSWeb/Views/Home/Details.cshtml
git commit -m "feat: add Abyss.to video carousel to post detail page"
```

---

## Task 12: Deployment Setup

**Files:** (server — manual steps)

- [ ] **Step 1: Publish AbyssUploader to server**

On your local machine, publish as a self-contained Linux binary:

```bash
dotnet publish AbyssUploader/AbyssUploader.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained false \
  -o ./publish/abyssuploader
```

Then `scp` or `rsync` the output to the VPS:

```bash
scp -r ./publish/abyssuploader user@your-vps:/opt/abyssuploader
```

- [ ] **Step 2: Create appsettings on server with real credentials**

On the VPS, create `/opt/abyssuploader/appsettings.json` with real values filled in — copy the template from `AbyssUploader/appsettings.json` and fill in `Supabase.Url`, `Supabase.ServiceKey`, `B2.*`, and `AbyssUploader.ApiKey`.

- [ ] **Step 3: Test run manually**

```bash
cd /opt/abyssuploader && dotnet AbyssUploader.dll
```

Expected: logs showing posts fetched, zips downloaded, videos uploaded, posts marked `is_streaming = true` in Supabase.

- [ ] **Step 4: Add cron job**

```bash
crontab -e
```

Add line:
```
0 2 * * * cd /opt/abyssuploader && dotnet AbyssUploader.dll >> /var/log/abyssuploader.log 2>&1
```

- [ ] **Step 5: Push all commits and trigger KJSWeb deploy**

```bash
git push
```

GitHub Actions will rebuild and redeploy KJSWeb automatically. Verify a post with `is_streaming = true` in Supabase shows the video carousel on its detail page.

---

## Self-Review

**Spec coverage check:**
- ✅ New `AbyssUploader` console project — Tasks 2–9
- ✅ DB columns `is_streaming` + `abyss_videos` — Task 1
- ✅ Fetch posts where `is_streaming = false` — Task 5
- ✅ Download zip from B2 via S3 SDK — Task 6
- ✅ Extract with SharpCompress, recurse subfolders — Task 8
- ✅ Video extension filter — Task 7
- ✅ Upload to Abyss.to multipart — Task 7
- ✅ Store `[{slug, filename}]` in `abyss_videos` — Tasks 5, 8
- ✅ Daily quota guard (95 GB) — Task 8
- ✅ No-videos case → still mark `is_streaming = true` — Task 8
- ✅ Error handling: skip post, leave `is_streaming = false` — Task 8
- ✅ KJSWeb `Post.cs` + `AbyssVideo.cs` model — Task 10
- ✅ Carousel above download section, public, no auth — Task 11
- ✅ Single-video case (no strip/arrows) — Task 11
- ✅ Cron deployment — Task 12

**Type consistency check:**
- `AbyssVideo.Slug` / `AbyssVideo.Filename` — used consistently in Tasks 4, 5, 7, 8, 10, 11 ✅
- `SupabaseMigrationService.MarkStreamingDoneAsync(Guid, List<AbyssVideo>)` — called in Task 8 with same signature ✅
- `AbyssUploadService.UploadVideosAsync(string, CancellationToken)` returns `List<AbyssVideo>` — consumed in Task 8 ✅
- `AbyssUploadService.GetVideoFilesSize(string)` is `static` — called as `AbyssUploadService.GetVideoFilesSize(...)` in Task 8 ✅
