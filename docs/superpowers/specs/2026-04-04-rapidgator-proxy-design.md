# Rapidgator Download Proxy API — Design Spec

**Date:** 2026-04-04  
**Status:** Draft  
**Author:** Brainstorming session

---

## 1. Problem Statement

KJSWeb and the AsianScandal post site host content with download links pointing to Rapidgator. Users must have their own Rapidgator premium accounts to download, which creates friction. The goal is to build a proxy service that:

1. Downloads files from Rapidgator using our premium account
2. Caches files on our server for 24 hours
3. Serves files directly to subscribed users via NGINX
4. Handles concurrent requests efficiently without duplicate downloads

## 2. Requirements

### Functional
- **R1:** Subscribed users can request a file download by providing a Rapidgator URL
- **R2:** The service downloads the file from Rapidgator using our premium account (via HTTP proxy)
- **R3:** Downloaded files are cached on disk for 24 hours
- **R4:** If a file is already cached, serve it immediately
- **R5:** If multiple users request the same file simultaneously, only one Rapidgator download occurs
- **R6:** Users can poll download progress while waiting
- **R7:** Files are served via NGINX X-Accel-Redirect (ASP.NET Core never sends file bytes)
- **R8:** Expired files are automatically cleaned up
- **R9:** Only users with active subscriptions (validated via Supabase) can download

### Non-Functional
- **NF1:** Handle 10-50 concurrent users at peak
- **NF2:** Support files up to 500MB
- **NF3:** Max cache size limit with LRU eviction (configurable, default 20GB)
- **NF4:** Configurable concurrent Rapidgator download limit (default 5)
- **NF5:** Stream downloads to disk (no in-memory buffering of entire files)
- **NF6:** Survive process restarts (SQLite persistence)
- **NF7:** Auto-restart on crash (systemd)

## 3. Architecture

### Overview

```
┌──────────┐  HTTPS   ┌──────────┐  upstream  ┌─────────────────────┐
│  User    │ ────────▶│  NGINX   │ ─────────▶│  ASP.NET Core API   │
│ (Browser)│ ◀────────│  :443    │            │  :5050 (Kestrel)    │
└──────────┘  file    └──────────┘            │                     │
              bytes      │  ▲                 │  ┌─ AuthService     │──▶ Supabase
                         │  │ X-Accel         │  │  (JWT + sub)     │
                         ▼  │ Redirect        │  │                  │
                   ┌──────────┐               │  ├─ RGApiClient     │──▶ Rapidgator API
                   │  Disk    │◀── streams ───│  │  (login, token,  │     (via HTTP proxy)
                   │  Cache   │   to disk     │  │   download)      │
                   │  (LRU    │               │  │                  │
                   │  eviction)│               │  ├─ CacheManager    │──▶ SQLite (WAL)
                   └──────────┘               │  │  (track, evict)  │
                                              │  │                  │
                                              │  └─ CleanupWorker   │
                                              │    (background svc) │
                                              └─────────────────────┘
```

### Request Flow

1. User sends `POST /api/download/request` with `{ rapidgatorUrl }` and Supabase JWT
2. NGINX forwards to ASP.NET Core
3. **AuthService** validates JWT and checks subscription status in Supabase
4. **DownloadCoordinator** checks if the URL is already cached or being downloaded:
   - **Cached & not expired:** Returns `{ downloadId, status: "ready" }` immediately
   - **Currently downloading:** Returns existing `{ downloadId, status: "downloading" }` — user polls progress
   - **Not cached:** Creates a `DownloadEntry`, starts async download, returns `{ downloadId, status: "downloading" }`
5. User polls `GET /api/download/status/{downloadId}` for progress
6. When ready, user calls `GET /api/download/file/{downloadId}`
7. ASP.NET Core returns `X-Accel-Redirect: /internal-cache/{filename}` header + `Content-Disposition`
8. **NGINX** serves the file bytes directly from disk — ASP.NET Core sends zero file bytes

### NGINX X-Accel-Redirect

NGINX is configured with an `internal` location that only ASP.NET Core can trigger:

```nginx
server {
    listen 443 ssl;
    server_name dl.yoursite.com;

    ssl_certificate     /etc/letsencrypt/live/dl.yoursite.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/dl.yoursite.com/privkey.pem;

    # Internal-only — cannot be accessed by users directly
    location /internal-cache/ {
        internal;
        alias /var/cache/rg-downloads/;
    }

    # All API calls → Kestrel
    location /api/ {
        proxy_pass http://127.0.0.1:5050;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 600s;
    }
}
```

## 4. API Endpoints

| Method | Endpoint | Purpose | Auth |
|--------|----------|---------|------|
| `POST` | `/api/download/request` | Request a file download | Supabase JWT |
| `GET` | `/api/download/status/{downloadId}` | Poll download progress | Supabase JWT |
| `GET` | `/api/download/file/{downloadId}` | Get the file (X-Accel-Redirect) | Supabase JWT |
| `GET` | `/api/health` | Health check | None |

### POST /api/download/request

**Request:**
```json
{
  "rapidgatorUrl": "https://rapidgator.net/file/abc123/video.mp4"
}
```

**Response (cached):**
```json
{
  "downloadId": "550e8400-e29b-41d4-a716-446655440000",
  "status": "ready",
  "fileName": "video.mp4",
  "fileSize": 104857600
}
```

**Response (not cached):**
```json
{
  "downloadId": "550e8400-e29b-41d4-a716-446655440000",
  "status": "downloading",
  "fileName": "video.mp4"
}
```

### GET /api/download/status/{downloadId}

**Response:**
```json
{
  "downloadId": "550e8400-e29b-41d4-a716-446655440000",
  "status": "downloading",
  "progress": 73.5,
  "fileSize": 104857600,
  "downloadedBytes": 77070336,
  "estimatedSecondsRemaining": 45
}
```

### GET /api/download/file/{downloadId}

**Success:** Returns `200 OK` with:
- `X-Accel-Redirect: /internal-cache/{cachedFileName}`
- `Content-Disposition: attachment; filename="video.mp4"`
- `Content-Type: application/octet-stream`
- Empty body (NGINX serves the file)

**Not ready:** Returns `409 Conflict` with `{ "error": "File is still downloading", "progress": 73.5 }`

## 5. Data Model

### DownloadEntry (SQLite table: `download_entries`)

| Column | Type | Description |
|--------|------|-------------|
| `id` | TEXT (PK) | GUID |
| `rapidgator_url` | TEXT | Original Rapidgator link |
| `cached_file_name` | TEXT | Filename on disk (GUID prefix to avoid collisions) |
| `original_file_name` | TEXT | Original filename for Content-Disposition |
| `file_size` | INTEGER | File size in bytes |
| `downloaded_bytes` | INTEGER | Progress tracking |
| `status` | TEXT | pending, downloading, ready, failed, expired |
| `error_message` | TEXT | Error details if failed |
| `requested_by_user_id` | TEXT | First user who triggered the download |
| `created_at` | TEXT (ISO8601) | When the request was made |
| `completed_at` | TEXT (ISO8601) | When download finished |
| `expires_at` | TEXT (ISO8601) | When file should be cleaned up (completed_at + 24h) |
| `last_accessed_at` | TEXT (ISO8601) | Updated on each file serve (for LRU) |

### Indexes
- `ix_rapidgator_url` on `rapidgator_url` — fast lookup for dedup
- `ix_status_expires` on `(status, expires_at)` — cleanup query
- `ix_last_accessed` on `last_accessed_at` — LRU eviction

## 6. Component Design

### 6.1 RapidgatorApiClient

Manages authentication and download URL resolution with Rapidgator's API.

**Responsibilities:**
- Login with username/password → obtain session token
- Cache token, refresh when expired (tokens last ~1 hour)
- Resolve file URL → direct download CDN link via `/api/v2/file/download`
- All requests routed through configured HTTP proxy

**Key implementation detail:** Use `IHttpClientFactory` with a named client that has the proxy configured:
```csharp
services.AddHttpClient("RapidgatorProxy", client => { ... })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        Proxy = new WebProxy(proxyAddress),
        UseProxy = true
    });
```

### 6.2 FileDownloadService

Downloads files from Rapidgator CDN to local disk with progress tracking.

**Responsibilities:**
- Stream download using `HttpCompletionOption.ResponseHeadersRead` (no memory buffering)
- Write to disk in 80KB chunks
- Update `DownloadEntry.downloaded_bytes` periodically (every 1MB or 2 seconds)
- Handle network errors with retry (3 attempts, exponential backoff)

### 6.3 DownloadCoordinator

Prevents duplicate downloads and manages concurrency.

**Responsibilities:**
- Maintain `ConcurrentDictionary<string, DownloadEntry>` keyed by normalized Rapidgator URL
- `SemaphoreSlim` per URL to coordinate concurrent requests for the same file
- Global `SemaphoreSlim(maxConcurrentDownloads: 5)` to limit total Rapidgator connections
- On startup, reload active entries from SQLite

### 6.4 CacheManagerService

Tracks cached files, handles eviction and disk space management.

**Responsibilities:**
- Check if a URL is already cached and not expired
- Track total cache size
- LRU eviction when cache approaches max size (default 20GB)
- Update `last_accessed_at` on file serve

### 6.5 AuthService

Validates user identity and subscription status.

**Responsibilities:**
- Validate Supabase JWT token (signature + expiry)
- Query Supabase for active subscription record
- Cache subscription status briefly (5 minutes) to avoid hammering Supabase

### 6.6 CacheCleanupService (BackgroundService)

Runs periodically to clean up expired files.

**Responsibilities:**
- Every 30 minutes: query SQLite for entries where `expires_at < now`
- Delete files from disk
- Update status to `expired` in SQLite
- Delete entries older than 7 days
- Log cleanup summary

## 7. Configuration

```json
{
  "Rapidgator": {
    "Username": "your-rg-username",
    "Password": "your-rg-password",
    "ApiBaseUrl": "https://rapidgator.net/api/v2",
    "MaxConcurrentDownloads": 5
  },
  "Proxy": {
    "Address": "http://proxy-ip:port",
    "Username": "",
    "Password": ""
  },
  "Cache": {
    "Directory": "/var/cache/rg-downloads",
    "MaxSizeGB": 20,
    "FileExpiryHours": 24,
    "CleanupIntervalMinutes": 30
  },
  "Supabase": {
    "Url": "https://djxlrniywyamhkfasczp.supabase.co",
    "Key": "your-anon-key",
    "ServiceKey": "your-service-key"
  },
  "Cors": {
    "AllowedOrigins": ["https://yoursite.com", "https://post.yoursite.com"]
  }
}
```

## 8. Error Handling

| Scenario | Behavior |
|----------|----------|
| Rapidgator auth fails | Return 503, log alert, retry login |
| Rapidgator file not found | Return 404, mark entry as `failed` |
| Download network error | Retry 3x with backoff, then mark `failed` |
| Disk full | Trigger emergency LRU eviction, reject new downloads if still full |
| Invalid/expired JWT | Return 401 |
| No active subscription | Return 403 |
| File expired between status check and download | Return 410 Gone |
| Rapidgator rate limited | Queue request, retry after delay |

## 9. Security Considerations

- **No direct file access:** NGINX `internal` directive prevents direct browsing of cache directory
- **JWT validation:** Every request validated server-side, not just client-side
- **Subscription gate:** Active subscription required, not just authentication
- **Proxy for Rapidgator:** Server IP not exposed to Rapidgator directly
- **HTTPS only:** All user-facing traffic encrypted
- **No URL guessing:** Download IDs are GUIDs, files have GUID-prefixed names
- **CORS restricted:** Only allowed origins can call the API

## 10. Deployment

### VPS Requirements
- **OS:** Ubuntu 24.04 LTS
- **CPU:** 2+ cores
- **RAM:** 4GB minimum (8GB recommended)
- **Disk:** 50GB+ SSD (20GB for cache + OS + logs)
- **Bandwidth:** 1Gbps unmetered recommended (Hetzner or Contabo)

### Services
- **NGINX:** Reverse proxy + file serving, managed via systemd
- **ASP.NET Core app:** Runs as systemd service on port 5050
- **SSL:** Let's Encrypt via certbot

### systemd Service Unit
```ini
[Unit]
Description=Rapidgator Proxy API
After=network.target

[Service]
Type=notify
User=www-data
WorkingDirectory=/opt/rg-proxy
ExecStart=/usr/bin/dotnet /opt/rg-proxy/RapidgatorProxy.Api.dll
Restart=always
RestartSec=10
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5050

[Install]
WantedBy=multi-user.target
```

## 11. Project Structure

```
RapidgatorProxy/
├── RapidgatorProxy.sln
├── src/
│   └── RapidgatorProxy.Api/
│       ├── Controllers/
│       │   ├── DownloadController.cs
│       │   └── HealthController.cs
│       ├── Services/
│       │   ├── RapidgatorApiClient.cs
│       │   ├── FileDownloadService.cs
│       │   ├── CacheManagerService.cs
│       │   ├── DownloadCoordinator.cs
│       │   └── AuthService.cs
│       ├── BackgroundServices/
│       │   └── CacheCleanupService.cs
│       ├── Models/
│       │   ├── DownloadEntry.cs
│       │   ├── DownloadRequest.cs
│       │   └── DownloadStatus.cs
│       ├── Data/
│       │   └── AppDbContext.cs
│       ├── Configuration/
│       │   └── ProxySettings.cs
│       ├── Program.cs
│       ├── appsettings.json
│       └── RapidgatorProxy.Api.csproj
├── deploy/
│   ├── nginx/
│   │   └── rg-proxy.conf
│   └── systemd/
│       └── rg-proxy.service
└── README.md
```

## 12. Future Considerations (Not in Scope)

- **Multiple Rapidgator accounts:** Round-robin for higher concurrent download limits
- **Download queue UI:** Show users their position in queue
- **Usage analytics:** Track per-user download volume
- **CDN integration:** Cloudflare or BunnyCDN in front of NGINX for global edge caching
- **Load balancing (Approach 3):** Multiple instances + Redis + shared storage — only if traffic exceeds 200+ concurrent users
