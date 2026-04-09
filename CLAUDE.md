# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run Commands

All projects target **net10.0**.

```bash
# Build entire solution
dotnet build KJSProject.sln

# Run the web frontend
dotnet run --project KJSWeb

# Run the migrator (all pending posts)
dotnet run --project RgToB2Migrator

# Run the migrator with a post limit
dotnet run --project RgToB2Migrator -- --limit 10
```

There are no automated tests in this solution.

## Architecture Overview

This is a two-project solution centered around serving content scraped/migrated from Rapidgator.

### KJSWeb — ASP.NET Core MVC Frontend
The user-facing website. Uses **Supabase** as the backend database (via the `Supabase` C# SDK and raw HTTP calls to the Supabase REST API). Key concerns:

- **SupabaseService** wraps all DB access. Uses the Supabase SDK for reads (`posts`, `asianscandal_posts`, `categories`) but falls back to raw `HttpClient` calls for operations where the SDK has naming conflicts with `IConfiguration` (e.g. `GetTotalPostCountAsync` uses `HEAD` with `Prefer: count=exact`).
- **Authentication** is session-based (cookie `BUZZ69_Session`, 24h). JWT validation via `System.IdentityModel.Tokens.Jwt`.
- **BlockonomicsService** handles Bitcoin payment webhooks for subscriptions.
- Two content sections: main posts (`HomeController`) and an asian-scandal section (`AsianScandalController`), each with their own Supabase table.
- Config keys: `Supabase:Url`, `Supabase:Key`, `Supabase:ServiceKey`.

### RgToB2Migrator — Console Migration Tool
Batch migrates files from Rapidgator → Backblaze B2 (S3-compatible). Pipeline per post:

1. **SupabaseMigrationService** — fetches `pending` posts from `posts` / `asianscandal_posts` tables, marks status as `processing` / `done` / `failed` / `pending`.
2. **RapidgatorDownloadService** — authenticates with Rapidgator API v2, resolves download links, handles folder URL expansion, streams files to disk.
3. **FileProcessingService** — detects file type by magic bytes, extracts archives (zip/rar/7z/tar via SharpCompress), renames extracted files sequentially.
4. **B2UploadService** — uploads via AWSSDK.S3 to Backblaze B2 using S3-compatible endpoint.
5. **MigrationOrchestrator** — drives the pipeline with parallelism (2 URLs at a time per post via `SemaphoreSlim`), retry logic (3 attempts, exponential backoff), and `RapidgatorTrafficExceededException` handling (stops the run early, resets post to `pending`).

On startup, the orchestrator resets any rows stuck in `processing` state back to `pending` before fetching a new batch.

Config sections: `Supabase` (Url, ServiceKey), `Rapidgator` (Username, Password, ApiBaseUrl, RequestDelayMs), `B2` (ApplicationKeyId, ApplicationKey, BucketName, Region, ServiceUrl), `Migrator` (TempFolder, RateLimitDelayMs).

## Testing Locally

### Expose local server with Cloudflare Tunnel
```bash
cloudflared tunnel --url http://localhost:5000
# Prints a public URL like https://random-words.trycloudflare.com
dotnet run --project KJSWeb --urls http://localhost:5000
```

### Test Blockonomics subscription callback manually
Simulates a confirmed Bitcoin payment (status=2) without sending real BTC. Use the BTC address shown on the payment page after subscribing:
```
GET https://your-tunnel.trycloudflare.com/api/blockonomics/callback?status=2&addr={BTC_ADDRESS}&value=500000&txid=faketxid123&secret={Blockonomics:CallbackSecret}
```
Status codes: `0` = unconfirmed, `1` = partially confirmed, `2` = fully confirmed (activates subscription).  
Expected log output:
```
Blockonomics callback: status=2, addr=..., value=500000, txid=faketxid123
Subscription activated for address: ..., plan: monthly, days: 30
```

### Test CrakRevenue CPA postback manually
Simulates a completed CPA offer. Get the `session_key` from your browser session cookie `cpa_session_key` and a real `post_id` from Supabase:
```
GET https://your-tunnel.trycloudflare.com/api/cpa/postback?post_id={POST_ID}&session_key={SESSION_KEY}&table=posts&secret={CrakRevenue:PostbackSecret}
```
Expected response: `1` with status 200. Then revisit the post — download buttons should appear.

### CrakRevenue SmartLink postback URL (set in CrakRevenue dashboard)
```
https://your-domain.com/api/cpa/postback?post_id={aff_sub}&session_key={aff_sub2}&table={aff_sub3}&secret={CrakRevenue:PostbackSecret}
```

## Supabase Tables

- `posts` — main content posts with `original_rapidgator_urls`, `our_download_link`, `migration_status`
- `asianscandal_posts` — same schema, separate table
- `categories` — category list for the main posts section
- `subscriptions` — user subscription records with BTC payment tracking
