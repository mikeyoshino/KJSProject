# Bunny.net Video Streaming Migration — Design Spec

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Abyss.to as the video streaming provider with Bunny.net Stream, rename the `AbyssUploader` console project to `VideoUploader`, and update the embedded player in KJSWeb.

**Architecture:** The B2 download → zip extraction → Supabase update pipeline is unchanged. Only the upload service, configuration, model names, DB column, and player embed URL change. The Bunny.net upload is a two-step HTTP flow: POST to create a video record (get GUID), then PUT raw file bytes.

**Tech Stack:** .NET 10, HttpClient (named client "Bunny"), Bunny.net Stream REST API, Supabase REST PATCH, ASP.NET Core MVC.

---

## 1. What Changes

| Area | Old | New |
|---|---|---|
| Project name | `AbyssUploader` | `VideoUploader` |
| Upload service | `AbyssUploadService` (curl → Abyss.to) | `BunnyUploadService` (HttpClient → Bunny.net) |
| Orchestrator class | `AbyssOrchestrator` | `VideoOrchestrator` |
| Config class | `AbyssSettings` | `BunnySettings` |
| Model (both projects) | `AbyssVideo { Slug, Filename }` | `StreamVideo { VideoId, Filename }` |
| DB column | `abyss_videos` jsonb | `stream_videos` jsonb |
| Player embed | `https://abysscdn.com/?v={slug}` | `https://iframe.mediadelivery.net/embed/{libraryId}/{videoId}` |
| KJSWeb model field | `Post.AbyssVideos` | `Post.StreamVideos` |

`is_streaming` boolean stays as-is — name still accurate.

---

## 2. Database Migration

Run once in Supabase SQL editor:

```sql
ALTER TABLE posts RENAME COLUMN abyss_videos TO stream_videos;
```

---

## 3. VideoUploader Console Project

### Renamed/restructured files

```
VideoUploader/                          (was AbyssUploader/)
  VideoUploader.csproj                  (was AbyssUploader.csproj)
  Program.cs                            (updated: renames throughout)
  appsettings.json                      (updated: Bunny section replaces AbyssUploader)
  VideoOrchestrator.cs                  (was AbyssOrchestrator.cs)
  Configuration/
    AppSettings.cs                      (updated: BunnySettings replaces AbyssSettings)
  Models/
    StreamVideo.cs                      (was AbyssVideo.cs: Slug→VideoId)
    PostRow.cs                          (unchanged)
  Services/
    BunnyUploadService.cs               (was AbyssUploadService.cs: full rewrite)
    B2DownloadService.cs                (unchanged)
    SupabaseMigrationService.cs         (updated: abyss_videos→stream_videos, AbyssVideo→StreamVideo)
```

### BunnySettings

```csharp
public class BunnySettings
{
    public string ApiKey { get; set; } = "";
    public string LibraryId { get; set; } = "";
    public double DailyLimitGb { get; set; } = 95;
    public int BatchSize { get; set; } = 5;
    public string TempFolder { get; set; } = "/tmp/video-uploader";
}
```

### appsettings.json shape

```json
{
  "Supabase": { "Url": "", "ServiceKey": "" },
  "B2": { "ApplicationKeyId": "", "ApplicationKey": "", "BucketName": "", "Region": "", "ServiceUrl": "", "PublicBaseUrl": "" },
  "Bunny": {
    "ApiKey": "",
    "LibraryId": "",
    "DailyLimitGb": 95,
    "BatchSize": 5,
    "TempFolder": "/tmp/video-uploader"
  }
}
```

### BunnyUploadService — upload flow

Two `HttpClient` calls per video file. Named client `"Bunny"` registered with `Timeout = InfiniteTimeSpan`.

**Step 1 — Create video record:**
```
POST https://video.bunnycdn.com/library/{libraryId}/videos
AccessKey: {apiKey}
Content-Type: application/json

{"title": "{filename}"}

→ 200 {"guid": "abc-123-...", ...}
```

**Step 2 — Upload raw bytes:**
```
PUT https://video.bunnycdn.com/library/{libraryId}/videos/{guid}
AccessKey: {apiKey}
Content-Type: application/octet-stream
Content-Length: {file size in bytes}

[raw file bytes streamed from disk]

→ 200 OK
```

If Step 1 fails, return null (post skipped, retried next run). If Step 2 fails, the orphan video record in Bunny is acceptable — it will not be referenced anywhere. Return null so post is retried.

`BunnyUploadService` takes `IOptions<BunnySettings>` and `IHttpClientFactory`. No `curl` subprocess — `HttpClient` PUT with raw `StreamContent` and explicit `Content-Length` works correctly.

### StreamVideo model

```csharp
// AbyssUploader/Models/StreamVideo.cs
using Newtonsoft.Json;

public class StreamVideo
{
    [JsonProperty("video_id")]
    public string VideoId { get; set; } = "";

    [JsonProperty("filename")]
    public string Filename { get; set; } = "";
}
```

### SupabaseMigrationService changes

- `MarkStreamingDoneAsync` signature: `List<StreamVideo>` (was `List<AbyssVideo>`)
- PATCH payload key: `stream_videos` (was `abyss_videos`)

---

## 4. KJSWeb Changes

### Files modified

```
KJSWeb/
  Models/
    AbyssVideo.cs               → DELETE
    StreamVideo.cs              → CREATE (System.Text.Json attributes)
    Post.cs                     → update column + field name
  Controllers/
    HomeController.cs           → read BunnyLibraryId from config, pass via ViewBag
  Views/Home/
    Details.cshtml              → update iframe src + JS array
  appsettings.json              → add Bunny:LibraryId
```

### StreamVideo (KJSWeb)

```csharp
// KJSWeb/Models/StreamVideo.cs
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

### Post.cs change

```csharp
// Remove:
[Column("abyss_videos")]
public List<AbyssVideo> AbyssVideos { get; set; } = new();

// Add:
[Column("stream_videos")]
public List<StreamVideo> StreamVideos { get; set; } = new();
```

### HomeController — Details action

Add after existing ViewBag assignments:

```csharp
ViewBag.BunnyLibraryId = _configuration["Bunny:LibraryId"];
```

`_configuration` is already injected (`IConfiguration`) in `HomeController`.

### Details.cshtml — player section

Replace the Abyss iframe `src` and JS:

```cshtml
@* Single video *@
src="https://iframe.mediadelivery.net/embed/@ViewBag.BunnyLibraryId/@videos[0].VideoId"

@* JS array *@
var ids = [@Html.Raw(string.Join(", ", videos.Select(v => $"'{v.VideoId}'")))];
@* iframe src in go(): *@
'https://iframe.mediadelivery.net/embed/@ViewBag.BunnyLibraryId/' + ids[index]
```

The carousel prev/next/thumbnail logic is otherwise unchanged.

### KJSWeb appsettings.json

```json
"Bunny": {
  "LibraryId": ""
}
```

---

## 5. Out of Scope

- Migrating existing `abyss_videos` rows to Bunny (existing data stays as-is; those posts stay `is_streaming=true` from before, but their old slugs will no longer embed — acceptable since Abyss.to isn't working anyway)
- `asianscandal_posts` table (not in scope per original Abyss spec)
- Bunny.net folder organisation per post
- Subtitle support
- Deleting orphan videos from Bunny on retry
