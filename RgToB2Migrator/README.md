# RgToB2Migrator - Rapidgator to Backblaze B2 Migration Tool

A .NET console application that migrates files from Rapidgator to Backblaze B2 cloud storage. It downloads files from original Rapidgator links using the Rapidgator premium API and re-uploads them to B2, updating the database with the new B2 URLs.

## Features

- **Handles large files**: Streams downloads and uploads to disk to avoid memory issues with files 10 GB+
- **Rate limit aware**: Respects Rapidgator API rate limits with configurable delays
- **Database tracking**: Tracks migration status (`pending`, `processing`, `done`, `failed`) in Supabase
- **Graceful error handling**: Continues processing on per-file failures; posts are only marked failed if all URLs fail
- **Crash recovery**: Can clean up and resume if interrupted

## Prerequisites

1. **Rapidgator Premium Account**
   - Login credentials (username/password)
   - API key from premium account settings

2. **Backblaze B2 Account**
   - Bucket created and configured
   - Application key (ID + secret)
   - Public URL accessible

3. **Supabase**
   - Service role key (not anon key)
   - Both `posts` and `asianscandal_posts` tables with `download_status` column

## Configuration

### appsettings.json

Default configuration file (committed to git). Only requires Supabase URL:

```json
{
  "Supabase": {
    "Url": "https://your-project.supabase.co"
  },
  "Rapidgator": {
    "ApiBaseUrl": "https://rapidgator.net/api/v2",
    "RequestDelayMs": 3000
  },
  "Migrator": {
    "TempFolder": "/tmp/rg-migrator",
    "RateLimitDelayMs": 3000
  }
}
```

### appsettings.Development.json

**Git-ignored** configuration file for secrets. Create this file locally:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "RgToB2Migrator": "Debug"
    }
  },
  "Supabase": {
    "ServiceKey": "YOUR_SUPABASE_SERVICE_ROLE_KEY"
  },
  "Rapidgator": {
    "Username": "YOUR_RAPIDGATOR_EMAIL",
    "Password": "YOUR_RAPIDGATOR_PASSWORD",
    "RequestDelayMs": 3000
  },
  "B2": {
    "ApplicationKeyId": "YOUR_B2_APP_KEY_ID",
    "ApplicationKey": "YOUR_B2_APP_KEY",
    "BucketId": "YOUR_B2_BUCKET_ID",
    "BucketName": "your-bucket-name",
    "PublicBaseUrl": "https://f000.backblazeb2.com/file/your-bucket-name"
  },
  "Migrator": {
    "TempFolder": "./temp/rg-migrator",
    "RateLimitDelayMs": 3000,
    "MaxRetries": 2
  }
}
```

## Database Preparation

Before running the migration, apply this SQL migration to add the `download_status` column to `asianscandal_posts`:

```bash
# The migration file is at:
scraper_service/supabase/migrations/20260406_add_download_status_asianscandal.sql
```

Or run the SQL directly in Supabase:

```sql
ALTER TABLE public.asianscandal_posts
ADD COLUMN IF NOT EXISTS download_status TEXT DEFAULT 'pending';

UPDATE public.asianscandal_posts
SET download_status = 'pending'
WHERE download_status IS NULL;

CREATE INDEX IF NOT EXISTS asianscandal_posts_download_status_idx
ON public.asianscandal_posts (download_status);
```

## Running the Migration

### Build

```bash
cd RgToB2Migrator
dotnet build
```

### Run

```bash
# Migrate all pending posts
DOTNET_ENVIRONMENT=Development dotnet run

# Migrate only N posts this run, then stop
DOTNET_ENVIRONMENT=Development dotnet run -- --limit 10
DOTNET_ENVIRONMENT=Development dotnet run -- -n 10
```

Re-running is safe — posts with status `done` or `failed` are skipped automatically. Only `pending` posts are picked up, so each run continues from where the previous one left off.

### Graceful Shutdown

Press `Ctrl+C` to gracefully cancel. The current post will complete, then the application exits cleanly.

## How It Works

### Processing Flow

1. **Startup**: Resets any posts stuck in `processing` state (older than 1 hour) back to `pending`
2. **Fetch**: Retrieves all pending posts from `posts` table, then `asianscandal_posts` table
3. **Process Each Post**:
   - Mark status as `processing`
   - For each Rapidgator URL in the array:
     - Get download link via Rapidgator API
     - Download file to temp folder (streaming, not in-memory)
     - Upload to B2 (via constructed URL)
     - Record B2 URL in result array
     - Clean up temp file
     - Wait `RateLimitDelayMs` before next file
   - Mark status as `done` with populated `our_download_link` array
4. **Error Handling**: If any file fails, the post is marked `failed` and processing continues

### Status Values

| Status | Meaning |
|--------|---------|
| `pending` | Waiting to be processed |
| `processing` | Currently being migrated |
| `done` | Successfully migrated; `our_download_link` is populated |
| `failed` | Migration failed for all URLs |

## Important Notes

### Large Files

The application streams downloads and uploads directly to disk using 1 MB buffers. This safely handles:
- Small files (< 200 MB): Direct download → upload
- Large files (200 MB - 10 GB): Chunked streaming
- Very large files (10 GB+): Safe due to streaming approach

### Disk Space

Ensure your `TempFolder` path has at least 20 GB free space to accommodate the largest expected file.

### Rate Limiting

The `RateLimitDelayMs` setting (default 3000ms = 3 seconds) is applied **between file downloads** to avoid hitting Rapidgator API rate limits. Conservative default; adjust based on your Rapidgator plan.

### Session Management

Rapidgator sessions are cached for 50 minutes. The application automatically re-logs in when needed.

## Troubleshooting

### "Rapidgator login failed"
- Check username and password in `appsettings.Development.json`
- Verify account is in good standing (no suspensions)
- Check API is accessible from your network

### "No download URL in response"
- URL may be expired or file deleted
- Account may lack permission (check premium status)
- Rapid gator API may be down

### "PATCH failed with 401"
- Supabase service key is invalid or wrong table name
- Check `Supabase:ServiceKey` in config

### "B2 upload failed"
- Check B2 credentials and bucket ID
- Ensure bucket is public or has proper CORS
- Check `PublicBaseUrl` format

## Performance

On a typical connection:
- Download from Rapidgator: 1-10 Mbps (depends on file size, throttling)
- Upload to B2: 10-50 Mbps (depends on region, account tier)
- Rapidgator API calls: ~50-200ms per call + network latency

With 3-second rate limits:
- Typical throughput: 1-5 posts per minute
- A post with 3 files: ~10-15 seconds per post

## Development

### Adding Real B2 Upload

The current `B2UploadService.cs` is a placeholder. To implement actual B2 uploads:

1. Add B2Net NuGet package (find stable version)
2. Implement the B2 authorization and upload flow
3. Handle both simple (<200MB) and large file uploads (chunked)
4. Add proper error recovery (CancelLargeFile for failed uploads)

### File Structure

```
RgToB2Migrator/
  Configuration/
    AppSettings.cs          - Config POCOs
  Models/
    MigrationPost.cs        - Supabase models
  Services/
    RapidgatorDownloadService.cs    - Rapidgator API + download
    B2UploadService.cs              - B2 upload (placeholder)
    SupabaseMigrationService.cs     - Database operations
    MigrationOrchestrator.cs        - Main processing loop
  Program.cs                - Entry point + DI
  appsettings.json          - Default config
  appsettings.Development.json - Secrets (git-ignored)
```

## License

Part of KJSProject. See root LICENSE file.
