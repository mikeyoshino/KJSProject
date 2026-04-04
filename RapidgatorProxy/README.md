# Rapidgator Download Proxy API

A C# ASP.NET Core Web API that proxies file downloads from Rapidgator, caches them locally, and serves them efficiently via NGINX X-Accel-Redirect.

## Architecture

- **ASP.NET Core** handles API logic, auth, and download orchestration
- **NGINX** serves cached files directly (zero file bytes through ASP.NET Core)
- **SQLite** persists download state (survives process restarts)
- **Background worker** cleans expired files every 30 minutes

## Quick Start (Development)

```bash
cd src/RapidgatorProxy.Api

# Configure (edit appsettings.json with your credentials)
# Required: Rapidgator username/password, Supabase URL/keys

dotnet run
```

## API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/download/request` | Request a file download |
| GET | `/api/download/status/{id}` | Poll download progress |
| GET | `/api/download/file/{id}` | Download the file |
| GET | `/api/health` | Health check |

## Configuration (appsettings.json)

| Section | Key | Description |
|---------|-----|-------------|
| Rapidgator | Username, Password | Premium account credentials |
| Rapidgator | MaxConcurrentDownloads | Max parallel Rapidgator fetches (default: 5) |
| Proxy | Address | HTTP proxy for Rapidgator requests |
| Cache | Directory | Local disk cache path |
| Cache | MaxSizeGB | Max cache size before LRU eviction (default: 20) |
| Cache | FileExpiryHours | Cache TTL (default: 24) |
| Supabase | Url, Key, ServiceKey | Auth provider credentials |
| Cors | AllowedOrigins | Allowed CORS origins |

## Deployment (Linux VPS)

```bash
# 1. Build
dotnet publish src/RapidgatorProxy.Api -c Release -o /opt/rg-proxy

# 2. NGINX
sudo cp deploy/nginx/rg-proxy.conf /etc/nginx/sites-available/
sudo ln -s /etc/nginx/sites-available/rg-proxy.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx

# 3. Systemd service
sudo cp deploy/systemd/rg-proxy.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable rg-proxy
sudo systemctl start rg-proxy

# 4. SSL (Let's Encrypt)
sudo certbot --nginx -d dl.yoursite.com
```

## How X-Accel-Redirect Works

1. User requests file via API
2. ASP.NET Core validates auth, checks cache
3. If cached: returns `X-Accel-Redirect` header pointing to internal NGINX location
4. NGINX serves file bytes directly — ASP.NET Core sends zero file data
5. Result: NGINX-level file serving performance with ASP.NET Core auth/logic
