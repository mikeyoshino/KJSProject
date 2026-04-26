# Abyss.to Video Streaming — Design Spec

**Date:** 2026-04-26
**Scope:** `posts` table only. New `AbyssUploader` console project + DB schema changes + video carousel in KJSWeb.

---

## Overview

Posts store zip archives in Backblaze B2. Each zip contains images and video files (possibly in subfolders). This feature adds a daily batch uploader that extracts video files from those zips and uploads them to Abyss.to for streaming. The post detail page gains a video carousel (public, no login required) powered by Abyss.to embeds.

---

## 1. AbyssUploader — Console Project

### Project structure
New .NET 10 console project `AbyssUploader` added to `KJSProject.sln`. Mirrors the `RgToB2Migrator` layout:

```
AbyssUploader/
  Program.cs
  appsettings.json
  Services/
    SupabaseMigrationService.cs   -- fetch/update posts
    B2DownloadService.cs          -- download zips from B2 via S3 SDK
    AbyssUploadService.cs         -- upload videos to Abyss.to
  Models/
    AbyssVideo.cs                 -- { Slug, Filename }
  AbyssOrchestrator.cs            -- drives the pipeline
```

### Pipeline (per run)

1. **Fetch batch** — query `posts` where `is_streaming = false` AND `our_download_link` is not null/empty, ordered `created_at asc`. Configurable `BatchSize` (default 50).
2. **For each post:**
   a. Mark post `is_streaming_status = processing` (optional internal tracking via log, not a DB column — keep schema simple)
   b. For each zip URL in `our_download_link`: download from B2 via AWSSDK.S3 → stream to temp folder
   c. Extract with SharpCompress (already in solution) — walk all files recursively including subfolders
   d. Collect video files by extension: `.mp4`, `.mkv`, `.avi`, `.mov`, `.wmv`, `.flv`, `.webm`
   e. Upload each video to `http://up.abyss.to/{apiKey}` via `multipart/form-data` (`file` field)
   f. Collect `[{slug, filename}]` pairs from responses
   g. Update post: set `abyss_videos = [{slug, filename}, ...]`, `is_streaming = true`
   h. Delete temp files
3. **Daily quota guard** — track total bytes uploaded per run. Stop processing new posts when cumulative upload size reaches `DailyLimitGb` (default 95 GB, leaving 5 GB headroom under Abyss.to's 100 GB/day cap).
4. **No videos found** — if a zip extracts successfully but contains zero video files (images only), set `is_streaming = true` and `abyss_videos = []` so the post is not retried on future runs.
5. **Error handling** — if a B2 download or Abyss.to upload fails for a post, log the error and skip (leave `is_streaming = false` so it retries next run). Do not mark as failed permanently.

### Configuration

```json
{
  "Supabase": {
    "Url": "",
    "ServiceKey": ""
  },
  "B2": {
    "ApplicationKeyId": "",
    "ApplicationKey": "",
    "BucketName": "",
    "Region": "",
    "ServiceUrl": ""
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

### Deployment & Cron

- Deployed to the same VPS as KJSWeb
- Built as a self-contained executable or run via `dotnet AbyssUploader.dll`
- Cron entry (runs daily at 2 AM server time):
  ```
  0 2 * * * cd /opt/abyssuploader && dotnet AbyssUploader.dll >> /var/log/abyssuploader.log 2>&1
  ```
- Config at `/opt/abyssuploader/appsettings.json` (or env vars via `.env` file sourced in cron)

---

## 2. Database Changes

Run in Supabase SQL editor:

```sql
ALTER TABLE posts ADD COLUMN IF NOT EXISTS is_streaming boolean DEFAULT false;
ALTER TABLE posts ADD COLUMN IF NOT EXISTS abyss_videos jsonb;
```

| Column | Type | Default | Purpose |
|---|---|---|---|
| `is_streaming` | `boolean` | `false` | True once all videos for this post are uploaded to Abyss.to |
| `abyss_videos` | `jsonb` | `null` | Array: `[{"slug":"ltJEfKQxR","filename":"scandal691.mp4"},...]` |

### KJSWeb model changes

**`Post.cs`** — add:
```csharp
public bool IsStreaming { get; set; }
public List<AbyssVideo> AbyssVideos { get; set; } = new();
```

**New `AbyssVideo.cs`** (shared between projects or duplicated):
```csharp
public record AbyssVideo(string Slug, string Filename);
```

**`SupabaseService.cs`** — include `is_streaming`, `abyss_videos` in post SELECT queries. Add:
```csharp
Task UpdateAbyssVideosAsync(string postId, List<AbyssVideo> videos);
// Sets abyss_videos and is_streaming = true via Supabase REST PATCH
```

---

## 3. Video Carousel UI

### Placement
On `Views/Home/Details.cshtml`, rendered **above** the `_PremiumDownload` partial, only when `Model.IsStreaming == true && Model.AbyssVideos.Any()`.

### Layout
```
[ ← ]  [ Abyss.to iframe — 16:9, full content width ]  [ → ]
             [ thumb1 | thumb2 | thumb3 ... ]
```

- **Main player** — `<iframe src="https://abysscdn.com/?v={slug}" allowfullscreen>`, 16:9 aspect ratio, full content column width
- **Thumbnail strip** — one tile per video, label derived from filename (`scandal691.mp4` → `Video 1`). Active tile has orange border/highlight.
- **Prev/Next arrows** — hidden when only one video
- **No authentication required** — Abyss.to embeds are public

### JavaScript
Vanilla JS inline in the view. On arrow click or thumbnail click: update iframe `src`, update active tile highlight. No page reload.

### Single-video case
If `AbyssVideos.Count == 1`: render iframe only, no strip or arrows.

---

## 4. Out of Scope

- `asianscandal_posts` and `jgirl_posts` tables — not included in this iteration
- Subtitle support
- Per-video Abyss.to folder organisation
- Deleting videos from Abyss.to when a post is deleted
- Subscriber-only video gating (videos are public for all users)
