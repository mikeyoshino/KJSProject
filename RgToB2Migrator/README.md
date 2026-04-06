# RgToB2Migrator - Rapidgator to Backblaze B2 Migration Tool

A .NET 10 console application that migrates files from Rapidgator to Backblaze B2 cloud storage. It downloads files from original Rapidgator links using the Rapidgator premium API, extracts and processes them (stripping ads, renaming), re-packs them into a ZIP, and re-uploads them to B2. It then updates the database with the new secure B2 paths.

## Features

- **Handles large files**: Streams downloads and uploads to disk/B2 using 1MB/10MB buffers to avoid memory issues with files 10 GB+
- **Smart Extraction**: Automatically extracts archives, renames media files to `scandal69{N}.ext`, and replaces text files with `scandal69.txt` promotional content.
- **Backblaze B2 Integration**: Uses the S3-compatible API for reliable multipart uploads.
- **Database tracking**: Tracks migration status (`pending`, `processing`, `done`, `failed`) in Supabase.
- **Graceful error handling**: Continues processing on per-file failures; includes file-lock protection for reliable async I/O.

## Prerequisites

1. **Rapidgator Premium Account** (Login + Password)
2. **Backblaze B2 Account**
   - Bucket created (Private recommended)
   - Application key (ID + Secret)
   - S3 Endpoint (e.g., `s3.us-east-005.backblazeb2.com`)
3. **Supabase**
   - Service role key
   - `posts` and `asianscandal_posts` tables with `download_status` and `our_download_link` columns.

## Configuration

### appsettings.json

The main configuration file. Fill in your B2 and account details here.

```json
{
  "Supabase": {
    "Url": "https://your-project.supabase.co",
    "ServiceKey": "YOUR_SERVICE_ROLE_KEY"
  },
  "Rapidgator": {
    "Username": "your-email@example.com",
    "Password": "your-password",
    "ApiBaseUrl": "https://rapidgator.net/api/v2",
    "RequestDelayMs": 3000
  },
  "B2": {
    "ApplicationKeyId": "YOUR_KEY_ID",
    "ApplicationKey": "YOUR_APPLICATION_KEY",
    "BucketName": "YourBucketName",
    "Region": "us-east-005",
    "ServiceUrl": "https://s3.us-east-005.backblazeb2.com"
  },
  "Migrator": {
    "TempFolder": "./temp/rg-migrator",
    "RateLimitDelayMs": 3000
  }
}
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
dotnet run

# Migrate only N posts this run, then stop
dotnet run -- --limit 10
```

Re-running is safe — posts with status `done` or `failed` are skipped automatically.

## Storage Structure

Files are stored in B2 using a predictable, GUID-based path to prevent collisions:
`posts/{postId}/{postId}.zip`

The `our_download_link` column in Supabase will be updated with the relative path (e.g., `posts/00e53095.../00e53095....zip`). This path is then used by the Cloudflare Worker gatekeeper to serve the file to authorized users.

## File Processing Rules

1. **Archives**: Extracted and flattened.
2. **Media/Files**: Renamed to `scandal691.mp4`, `scandal692.jpg`, etc.
3. **Text Files**: All `.txt`, `.nfo`, `.url` files are replaced with a single `scandal69.txt` containing your site link.
4. **Final Pack**: Everything is zipped into a single archive before upload.

## Performance

- **Download**: Directly streamed from Rapidgator to local temp disk.
- **Upload**: Multipart streaming (10MB chunks) to B2.
- **Memory**: Constant memory footprint (~50-100MB) regardless of file size (tested up to 20GB+).

## License

Part of KJSProject.
