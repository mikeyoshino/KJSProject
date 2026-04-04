# Rapidgator Download Proxy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a C# ASP.NET Core Web API that proxies Rapidgator downloads with NGINX X-Accel-Redirect file serving and 24h hybrid caching.

**Architecture:** ASP.NET Core handles auth, download orchestration, and cache management. NGINX serves cached files via X-Accel-Redirect. SQLite persists download state. Background service cleans expired files.

**Tech Stack:** .NET 10, ASP.NET Core Web API, EF Core + SQLite, NGINX, systemd

---

## File Structure

```
c:\Users\Admin\gitrepos\KJSProject\RapidgatorProxy\
├── RapidgatorProxy.sln
├── src\RapidgatorProxy.Api\
│   ├── RapidgatorProxy.Api.csproj
│   ├── Program.cs
│   ├── Configuration\
│   │   └── AppSettings.cs
│   ├── Models\
│   │   ├── DownloadEntry.cs
│   │   ├── DownloadStatus.cs
│   │   ├── DownloadRequest.cs
│   │   └── DownloadResponse.cs
│   ├── Data\
│   │   └── AppDbContext.cs
│   ├── Services\
│   │   ├── RapidgatorApiClient.cs
│   │   ├── FileDownloadService.cs
│   │   ├── CacheManagerService.cs
│   │   ├── DownloadCoordinator.cs
│   │   └── AuthService.cs
│   ├── BackgroundServices\
│   │   └── CacheCleanupService.cs
│   ├── Controllers\
│   │   ├── DownloadController.cs
│   │   └── HealthController.cs
│   └── appsettings.json
├── deploy\
│   ├── nginx\rg-proxy.conf
│   └── systemd\rg-proxy.service
└── README.md
```

---

### Task 1: Project Scaffolding

**Files:**
- Create: `RapidgatorProxy/RapidgatorProxy.sln`
- Create: `RapidgatorProxy/src/RapidgatorProxy.Api/RapidgatorProxy.Api.csproj`
- Create: `RapidgatorProxy/src/RapidgatorProxy.Api/Program.cs`
- Create: `RapidgatorProxy/src/RapidgatorProxy.Api/appsettings.json`

- [ ] **Step 1: Create solution and project**

```bash
cd c:\Users\Admin\gitrepos\KJSProject
mkdir RapidgatorProxy
cd RapidgatorProxy
dotnet new sln -n RapidgatorProxy
mkdir -p src/RapidgatorProxy.Api
cd src/RapidgatorProxy.Api
dotnet new webapi --no-openapi -n RapidgatorProxy.Api --framework net10.0
cd ../..
dotnet sln add src/RapidgatorProxy.Api/RapidgatorProxy.Api.csproj
```

- [ ] **Step 2: Add NuGet packages**

```bash
cd src/RapidgatorProxy.Api
dotnet add package Microsoft.EntityFrameworkCore.Sqlite --version 10.0.0
dotnet add package Microsoft.EntityFrameworkCore.Design --version 10.0.0
dotnet add package Newtonsoft.Json --version 13.0.4
```

- [ ] **Step 3: Create appsettings.json**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Rapidgator": {
    "Username": "",
    "Password": "",
    "ApiBaseUrl": "https://rapidgator.net/api/user/login",
    "MaxConcurrentDownloads": 5
  },
  "Proxy": {
    "Address": "",
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
    "Url": "",
    "Key": "",
    "ServiceKey": ""
  },
  "Cors": {
    "AllowedOrigins": []
  }
}
```

- [ ] **Step 4: Build to verify scaffolding**

Run: `dotnet build`
Expected: Build succeeded with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add .
git commit -m "feat: scaffold RapidgatorProxy project"
```

---

### Task 2: Configuration & Models

**Files:**
- Create: `Configuration/AppSettings.cs`
- Create: `Models/DownloadEntry.cs`
- Create: `Models/DownloadStatus.cs`
- Create: `Models/DownloadRequest.cs`
- Create: `Models/DownloadResponse.cs`

- [ ] **Step 1: Create AppSettings.cs**

```csharp
namespace RapidgatorProxy.Api.Configuration;

public class RapidgatorSettings
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://rapidgator.net/api";
    public int MaxConcurrentDownloads { get; set; } = 5;
}

public class ProxySettings
{
    public string Address { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class CacheSettings
{
    public string Directory { get; set; } = "/var/cache/rg-downloads";
    public int MaxSizeGB { get; set; } = 20;
    public int FileExpiryHours { get; set; } = 24;
    public int CleanupIntervalMinutes { get; set; } = 30;
    public long MaxSizeBytes => (long)MaxSizeGB * 1024 * 1024 * 1024;
}

public class SupabaseSettings
{
    public string Url { get; set; } = "";
    public string Key { get; set; } = "";
    public string ServiceKey { get; set; } = "";
}

public class CorsSettings
{
    public string[] AllowedOrigins { get; set; } = [];
}
```

- [ ] **Step 2: Create DownloadStatus.cs**

```csharp
namespace RapidgatorProxy.Api.Models;

public enum DownloadStatus
{
    Pending,
    Downloading,
    Ready,
    Failed,
    Expired
}
```

- [ ] **Step 3: Create DownloadEntry.cs**

```csharp
using System.ComponentModel.DataAnnotations;

namespace RapidgatorProxy.Api.Models;

public class DownloadEntry
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RapidgatorUrl { get; set; } = "";
    public string CachedFileName { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public long FileSize { get; set; }
    public long DownloadedBytes { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
    public string? ErrorMessage { get; set; }
    public string? RequestedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastAccessedAt { get; set; }
}
```

- [ ] **Step 4: Create DownloadRequest.cs and DownloadResponse.cs**

```csharp
// DownloadRequest.cs
namespace RapidgatorProxy.Api.Models;

public class DownloadRequest
{
    public string RapidgatorUrl { get; set; } = "";
}

// DownloadResponse.cs
namespace RapidgatorProxy.Api.Models;

public class DownloadResponse
{
    public string DownloadId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? FileName { get; set; }
    public long? FileSize { get; set; }
    public double? Progress { get; set; }
    public long? DownloadedBytes { get; set; }
    public int? EstimatedSecondsRemaining { get; set; }
    public string? Error { get; set; }
}
```

- [ ] **Step 5: Build and commit**

Run: `dotnet build` → Expected: success

```bash
git add .
git commit -m "feat: add configuration and data models"
```

---

### Task 3: Database Context (EF Core + SQLite)

**Files:**
- Create: `Data/AppDbContext.cs`

- [ ] **Step 1: Create AppDbContext.cs**

```csharp
using Microsoft.EntityFrameworkCore;
using RapidgatorProxy.Api.Models;

namespace RapidgatorProxy.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DownloadEntry> DownloadEntries => Set<DownloadEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DownloadEntry>();
        entity.HasIndex(e => e.RapidgatorUrl).HasDatabaseName("ix_rapidgator_url");
        entity.HasIndex(e => new { e.Status, e.ExpiresAt }).HasDatabaseName("ix_status_expires");
        entity.HasIndex(e => e.LastAccessedAt).HasDatabaseName("ix_last_accessed");
        entity.Property(e => e.Status).HasConversion<string>();
    }
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build` → Expected: success

```bash
git add .
git commit -m "feat: add EF Core SQLite database context"
```

---

### Task 4: RapidgatorApiClient Service

**Files:**
- Create: `Services/RapidgatorApiClient.cs`

- [ ] **Step 1: Create RapidgatorApiClient.cs**

```csharp
using System.Net;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using RapidgatorProxy.Api.Configuration;

namespace RapidgatorProxy.Api.Services;

public class RapidgatorApiClient
{
    private readonly HttpClient _httpClient;
    private readonly RapidgatorSettings _settings;
    private readonly ILogger<RapidgatorApiClient> _logger;
    private string? _sessionId;
    private DateTime _sessionExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    public RapidgatorApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<RapidgatorSettings> settings,
        ILogger<RapidgatorApiClient> logger)
    {
        _httpClient = httpClientFactory.CreateClient("RapidgatorProxy");
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<string> GetSessionIdAsync(CancellationToken ct = default)
    {
        if (_sessionId != null && DateTime.UtcNow < _sessionExpiry)
            return _sessionId;

        await _loginLock.WaitAsync(ct);
        try
        {
            if (_sessionId != null && DateTime.UtcNow < _sessionExpiry)
                return _sessionId;

            var url = $"{_settings.ApiBaseUrl}/user/login?login={Uri.EscapeDataString(_settings.Username)}&password={Uri.EscapeDataString(_settings.Password)}";
            var response = await _httpClient.GetStringAsync(url, ct);
            var json = JObject.Parse(response);

            if (json["response"]?["session_id"] == null)
                throw new Exception($"Rapidgator login failed: {response}");

            _sessionId = json["response"]!["session_id"]!.ToString();
            _sessionExpiry = DateTime.UtcNow.AddMinutes(50);
            _logger.LogInformation("Rapidgator login successful, session valid until {Expiry}", _sessionExpiry);
            return _sessionId;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async Task<(string downloadUrl, string fileName, long fileSize)> GetDownloadLinkAsync(
        string rapidgatorUrl, CancellationToken ct = default)
    {
        var sessionId = await GetSessionIdAsync(ct);
        var url = $"{_settings.ApiBaseUrl}/file/download?sid={sessionId}&url={Uri.EscapeDataString(rapidgatorUrl)}";
        var response = await _httpClient.GetStringAsync(url, ct);
        var json = JObject.Parse(response);

        var downloadUrl = json["response"]?["download_url"]?.ToString()
            ?? throw new Exception($"No download URL in response: {response}");
        var fileName = json["response"]?["filename"]?.ToString() ?? "unknown";
        var fileSize = json["response"]?["file_size"]?.Value<long>() ?? 0;

        return (downloadUrl, fileName, fileSize);
    }

    public HttpClient GetHttpClient() => _httpClient;
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build` → Expected: success

```bash
git add .
git commit -m "feat: add Rapidgator API client with session management"
```

---

### Task 5: FileDownloadService

**Files:**
- Create: `Services/FileDownloadService.cs`

- [ ] **Step 1: Create FileDownloadService.cs**

```csharp
using Microsoft.Extensions.Options;
using RapidgatorProxy.Api.Configuration;
using RapidgatorProxy.Api.Data;
using RapidgatorProxy.Api.Models;

namespace RapidgatorProxy.Api.Services;

public class FileDownloadService
{
    private readonly RapidgatorApiClient _rgClient;
    private readonly CacheSettings _cacheSettings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FileDownloadService> _logger;

    public FileDownloadService(
        RapidgatorApiClient rgClient,
        IOptions<CacheSettings> cacheSettings,
        IServiceScopeFactory scopeFactory,
        ILogger<FileDownloadService> logger)
    {
        _rgClient = rgClient;
        _cacheSettings = cacheSettings.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task DownloadFileAsync(DownloadEntry entry, CancellationToken ct)
    {
        try
        {
            var (downloadUrl, fileName, fileSize) = await _rgClient.GetDownloadLinkAsync(entry.RapidgatorUrl, ct);

            entry.OriginalFileName = fileName;
            entry.FileSize = fileSize;
            entry.Status = DownloadStatus.Downloading;
            await UpdateEntryAsync(entry);

            var filePath = Path.Combine(_cacheSettings.Directory, entry.CachedFileName);
            Directory.CreateDirectory(_cacheSettings.Directory);

            var httpClient = _rgClient.GetHttpClient();
            using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            if (entry.FileSize == 0 && response.Content.Headers.ContentLength.HasValue)
            {
                entry.FileSize = response.Content.Headers.ContentLength.Value;
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            var lastUpdate = DateTime.UtcNow;

            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                if ((DateTime.UtcNow - lastUpdate).TotalSeconds >= 2)
                {
                    entry.DownloadedBytes = totalRead;
                    await UpdateEntryAsync(entry);
                    lastUpdate = DateTime.UtcNow;
                }
            }

            entry.DownloadedBytes = totalRead;
            entry.FileSize = totalRead;
            entry.Status = DownloadStatus.Ready;
            entry.CompletedAt = DateTime.UtcNow;
            entry.ExpiresAt = DateTime.UtcNow.AddHours(_cacheSettings.FileExpiryHours);
            await UpdateEntryAsync(entry);

            _logger.LogInformation("Download complete: {FileName} ({Size} bytes)", fileName, totalRead);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Download failed for {Url}", entry.RapidgatorUrl);
            entry.Status = DownloadStatus.Failed;
            entry.ErrorMessage = ex.Message;
            await UpdateEntryAsync(entry);
            throw;
        }
    }

    private async Task UpdateEntryAsync(DownloadEntry entry)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.DownloadEntries.Update(entry);
        await db.SaveChangesAsync();
    }
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build` → Expected: success

```bash
git add .
git commit -m "feat: add streaming file download service with progress tracking"
```

---

### Task 6: CacheManagerService

**Files:**
- Create: `Services/CacheManagerService.cs`

- [ ] **Step 1: Create CacheManagerService.cs**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RapidgatorProxy.Api.Configuration;
using RapidgatorProxy.Api.Data;
using RapidgatorProxy.Api.Models;

namespace RapidgatorProxy.Api.Services;

public class CacheManagerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CacheSettings _cacheSettings;
    private readonly ILogger<CacheManagerService> _logger;

    public CacheManagerService(
        IServiceScopeFactory scopeFactory,
        IOptions<CacheSettings> cacheSettings,
        ILogger<CacheManagerService> logger)
    {
        _scopeFactory = scopeFactory;
        _cacheSettings = cacheSettings.Value;
        _logger = logger;
    }

    public async Task<DownloadEntry?> FindCachedAsync(string rapidgatorUrl)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DownloadEntries
            .Where(e => e.RapidgatorUrl == rapidgatorUrl
                && (e.Status == DownloadStatus.Ready || e.Status == DownloadStatus.Downloading)
                && (e.ExpiresAt == null || e.ExpiresAt > DateTime.UtcNow))
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<DownloadEntry?> GetByIdAsync(string id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DownloadEntries.FindAsync(id);
    }

    public async Task TouchAccessAsync(string id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.DownloadEntries.FindAsync(id);
        if (entry != null)
        {
            entry.LastAccessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task EvictIfNeededAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var totalSize = await db.DownloadEntries
            .Where(e => e.Status == DownloadStatus.Ready)
            .SumAsync(e => e.FileSize);

        if (totalSize <= _cacheSettings.MaxSizeBytes) return;

        _logger.LogWarning("Cache size {Size}GB exceeds limit {Limit}GB, evicting LRU files",
            totalSize / 1_073_741_824.0, _cacheSettings.MaxSizeGB);

        var toEvict = await db.DownloadEntries
            .Where(e => e.Status == DownloadStatus.Ready)
            .OrderBy(e => e.LastAccessedAt ?? e.CompletedAt)
            .Take(10)
            .ToListAsync();

        foreach (var entry in toEvict)
        {
            var path = Path.Combine(_cacheSettings.Directory, entry.CachedFileName);
            if (File.Exists(path)) File.Delete(path);
            entry.Status = DownloadStatus.Expired;
            _logger.LogInformation("Evicted: {File}", entry.CachedFileName);
        }

        await db.SaveChangesAsync();
    }
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build` → Expected: success

```bash
git add .
git commit -m "feat: add cache manager with LRU eviction"
```

---

### Task 7: DownloadCoordinator

**Files:**
- Create: `Services/DownloadCoordinator.cs`

- [ ] **Step 1: Create DownloadCoordinator.cs**

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RapidgatorProxy.Api.Configuration;
using RapidgatorProxy.Api.Data;
using RapidgatorProxy.Api.Models;

namespace RapidgatorProxy.Api.Services;

public class DownloadCoordinator
{
    private readonly CacheManagerService _cacheManager;
    private readonly FileDownloadService _downloadService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DownloadCoordinator> _logger;
    private readonly SemaphoreSlim _globalDownloadLimit;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _urlLocks = new();

    public DownloadCoordinator(
        CacheManagerService cacheManager,
        FileDownloadService downloadService,
        IServiceScopeFactory scopeFactory,
        IOptions<RapidgatorSettings> rgSettings,
        ILogger<DownloadCoordinator> logger)
    {
        _cacheManager = cacheManager;
        _downloadService = downloadService;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _globalDownloadLimit = new SemaphoreSlim(rgSettings.Value.MaxConcurrentDownloads);
    }

    public async Task<DownloadEntry> RequestDownloadAsync(string rapidgatorUrl, string? userId)
    {
        var normalizedUrl = rapidgatorUrl.Trim().TrimEnd('/');

        var existing = await _cacheManager.FindCachedAsync(normalizedUrl);
        if (existing != null) return existing;

        var urlLock = _urlLocks.GetOrAdd(normalizedUrl, _ => new SemaphoreSlim(1, 1));
        await urlLock.WaitAsync();
        try
        {
            existing = await _cacheManager.FindCachedAsync(normalizedUrl);
            if (existing != null) return existing;

            var entry = new DownloadEntry
            {
                RapidgatorUrl = normalizedUrl,
                CachedFileName = $"{Guid.NewGuid()}.tmp",
                Status = DownloadStatus.Pending,
                RequestedByUserId = userId
            };

            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.DownloadEntries.Add(entry);
                await db.SaveChangesAsync();
            }

            _ = Task.Run(async () =>
            {
                await _globalDownloadLimit.WaitAsync();
                try
                {
                    await _cacheManager.EvictIfNeededAsync();
                    await _downloadService.DownloadFileAsync(entry, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background download failed for {Url}", normalizedUrl);
                }
                finally
                {
                    _globalDownloadLimit.Release();
                    _urlLocks.TryRemove(normalizedUrl, out _);
                }
            });

            return entry;
        }
        finally
        {
            urlLock.Release();
        }
    }
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build` → Expected: success

```bash
git add .
git commit -m "feat: add download coordinator with dedup and concurrency control"
```

---

### Task 8: AuthService

**Files:**
- Create: `Services/AuthService.cs`

- [ ] **Step 1: Create AuthService.cs**

```csharp
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using RapidgatorProxy.Api.Configuration;

namespace RapidgatorProxy.Api.Services;

public class AuthService
{
    private readonly SupabaseSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IOptions<SupabaseSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<AuthService> logger)
    {
        _settings = settings.Value;
        _httpClient = httpClientFactory.CreateClient("Supabase");
        _logger = logger;
    }

    public async Task<(bool isValid, string? userId)> ValidateTokenAsync(string jwt)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_settings.Url}/auth/v1/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Headers.Add("apikey", _settings.Key);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return (false, null);

            var body = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(body);
            var userId = json["id"]?.ToString();
            return (userId != null, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Supabase auth validation failed");
            return (false, null);
        }
    }
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build` → Expected: success

```bash
git add .
git commit -m "feat: add Supabase JWT auth service"
```

---

### Task 9: CacheCleanupService (Background Worker)

**Files:**
- Create: `BackgroundServices/CacheCleanupService.cs`

- [ ] **Step 1: Create CacheCleanupService.cs**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RapidgatorProxy.Api.Configuration;
using RapidgatorProxy.Api.Data;
using RapidgatorProxy.Api.Models;

namespace RapidgatorProxy.Api.BackgroundServices;

public class CacheCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CacheSettings _cacheSettings;
    private readonly ILogger<CacheCleanupService> _logger;

    public CacheCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptions<CacheSettings> cacheSettings,
        ILogger<CacheCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _cacheSettings = cacheSettings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupExpiredFilesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache cleanup error");
            }

            await Task.Delay(TimeSpan.FromMinutes(_cacheSettings.CleanupIntervalMinutes), stoppingToken);
        }
    }

    private async Task CleanupExpiredFilesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var expired = await db.DownloadEntries
            .Where(e => e.Status == DownloadStatus.Ready && e.ExpiresAt != null && e.ExpiresAt < DateTime.UtcNow)
            .ToListAsync();

        var deletedCount = 0;
        foreach (var entry in expired)
        {
            var path = Path.Combine(_cacheSettings.Directory, entry.CachedFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
                deletedCount++;
            }
            entry.Status = DownloadStatus.Expired;
        }

        // Delete old records (>7 days)
        var oldRecords = await db.DownloadEntries
            .Where(e => (e.Status == DownloadStatus.Expired || e.Status == DownloadStatus.Failed)
                && e.CreatedAt < DateTime.UtcNow.AddDays(-7))
            .ToListAsync();
        db.DownloadEntries.RemoveRange(oldRecords);

        await db.SaveChangesAsync();
        _logger.LogInformation("Cleanup: deleted {Files} files, purged {Records} old records",
            deletedCount, oldRecords.Count);
    }
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build` → Expected: success

```bash
git add .
git commit -m "feat: add background cache cleanup service"
```

---

### Task 10: API Controllers

**Files:**
- Create: `Controllers/DownloadController.cs`
- Create: `Controllers/HealthController.cs`

- [ ] **Step 1: Create HealthController.cs**

```csharp
using Microsoft.AspNetCore.Mvc;

namespace RapidgatorProxy.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
}
```

- [ ] **Step 2: Create DownloadController.cs**

```csharp
using Microsoft.AspNetCore.Mvc;
using RapidgatorProxy.Api.Models;
using RapidgatorProxy.Api.Services;

namespace RapidgatorProxy.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DownloadController : ControllerBase
{
    private readonly DownloadCoordinator _coordinator;
    private readonly CacheManagerService _cacheManager;
    private readonly AuthService _authService;

    public DownloadController(
        DownloadCoordinator coordinator,
        CacheManagerService cacheManager,
        AuthService authService)
    {
        _coordinator = coordinator;
        _cacheManager = cacheManager;
        _authService = authService;
    }

    private async Task<(bool ok, string? userId)> AuthorizeAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return (false, null);
        var token = authHeader["Bearer ".Length..];
        return await _authService.ValidateTokenAsync(token);
    }

    [HttpPost("request")]
    public async Task<IActionResult> RequestDownload([FromBody] DownloadRequest request)
    {
        var (ok, userId) = await AuthorizeAsync();
        if (!ok) return Unauthorized(new { error = "Invalid or expired token" });

        if (string.IsNullOrWhiteSpace(request.RapidgatorUrl))
            return BadRequest(new { error = "rapidgatorUrl is required" });

        var entry = await _coordinator.RequestDownloadAsync(request.RapidgatorUrl, userId);
        return Ok(new DownloadResponse
        {
            DownloadId = entry.Id,
            Status = entry.Status.ToString().ToLower(),
            FileName = entry.OriginalFileName,
            FileSize = entry.FileSize > 0 ? entry.FileSize : null
        });
    }

    [HttpGet("status/{downloadId}")]
    public async Task<IActionResult> GetStatus(string downloadId)
    {
        var (ok, _) = await AuthorizeAsync();
        if (!ok) return Unauthorized(new { error = "Invalid or expired token" });

        var entry = await _cacheManager.GetByIdAsync(downloadId);
        if (entry == null) return NotFound(new { error = "Download not found" });

        double? progress = entry.FileSize > 0
            ? Math.Round((double)entry.DownloadedBytes / entry.FileSize * 100, 1)
            : null;

        int? eta = null;
        if (entry.Status == DownloadStatus.Downloading && progress > 0)
        {
            var elapsed = (DateTime.UtcNow - entry.CreatedAt).TotalSeconds;
            var rate = entry.DownloadedBytes / elapsed;
            if (rate > 0) eta = (int)((entry.FileSize - entry.DownloadedBytes) / rate);
        }

        return Ok(new DownloadResponse
        {
            DownloadId = entry.Id,
            Status = entry.Status.ToString().ToLower(),
            FileName = entry.OriginalFileName,
            FileSize = entry.FileSize,
            Progress = progress,
            DownloadedBytes = entry.DownloadedBytes,
            EstimatedSecondsRemaining = eta,
            Error = entry.ErrorMessage
        });
    }

    [HttpGet("file/{downloadId}")]
    public async Task<IActionResult> GetFile(string downloadId)
    {
        var (ok, _) = await AuthorizeAsync();
        if (!ok) return Unauthorized(new { error = "Invalid or expired token" });

        var entry = await _cacheManager.GetByIdAsync(downloadId);
        if (entry == null) return NotFound(new { error = "Download not found" });

        if (entry.Status == DownloadStatus.Downloading)
            return Conflict(new { error = "File is still downloading", progress = entry.DownloadedBytes * 100.0 / Math.Max(entry.FileSize, 1) });

        if (entry.Status != DownloadStatus.Ready)
            return BadRequest(new { error = $"File status is {entry.Status}", message = entry.ErrorMessage });

        await _cacheManager.TouchAccessAsync(downloadId);

        Response.Headers["X-Accel-Redirect"] = $"/internal-cache/{entry.CachedFileName}";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{entry.OriginalFileName}\"";
        Response.ContentType = "application/octet-stream";
        return new EmptyResult();
    }
}
```

- [ ] **Step 3: Build and commit**

Run: `dotnet build` → Expected: success

```bash
git add .
git commit -m "feat: add download and health API controllers"
```

---

### Task 11: Program.cs (Dependency Injection & Startup)

**Files:**
- Modify: `Program.cs`

- [ ] **Step 1: Write Program.cs**

```csharp
using System.Net;
using Microsoft.EntityFrameworkCore;
using RapidgatorProxy.Api.BackgroundServices;
using RapidgatorProxy.Api.Configuration;
using RapidgatorProxy.Api.Data;
using RapidgatorProxy.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration binding
builder.Services.Configure<RapidgatorSettings>(builder.Configuration.GetSection("Rapidgator"));
builder.Services.Configure<ProxySettings>(builder.Configuration.GetSection("Proxy"));
builder.Services.Configure<CacheSettings>(builder.Configuration.GetSection("Cache"));
builder.Services.Configure<SupabaseSettings>(builder.Configuration.GetSection("Supabase"));
builder.Services.Configure<CorsSettings>(builder.Configuration.GetSection("Cors"));

// SQLite + EF Core
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "rg-proxy.db");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// HttpClient for Rapidgator (with proxy)
var proxySettings = builder.Configuration.GetSection("Proxy").Get<ProxySettings>();
builder.Services.AddHttpClient("RapidgatorProxy", client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler();
    if (!string.IsNullOrEmpty(proxySettings?.Address))
    {
        handler.Proxy = new WebProxy(proxySettings.Address);
        if (!string.IsNullOrEmpty(proxySettings.Username))
            handler.Proxy.Credentials = new NetworkCredential(proxySettings.Username, proxySettings.Password);
        handler.UseProxy = true;
    }
    return handler;
});

// HttpClient for Supabase (no proxy)
builder.Services.AddHttpClient("Supabase");

// Services
builder.Services.AddSingleton<RapidgatorApiClient>();
builder.Services.AddSingleton<FileDownloadService>();
builder.Services.AddSingleton<CacheManagerService>();
builder.Services.AddSingleton<DownloadCoordinator>();
builder.Services.AddScoped<AuthService>();

// Background services
builder.Services.AddHostedService<CacheCleanupService>();

// CORS
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddControllers();

var app = builder.Build();

// Ensure DB is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// Ensure cache directory exists
var cacheDir = builder.Configuration.GetValue<string>("Cache:Directory") ?? "/var/cache/rg-downloads";
Directory.CreateDirectory(cacheDir);

app.UseCors();
app.MapControllers();

app.Run();
```

- [ ] **Step 2: Build and run to verify**

Run: `dotnet build` → Expected: success
Run: `dotnet run` → Expected: starts on port 5050, SQLite DB created

- [ ] **Step 3: Commit**

```bash
git add .
git commit -m "feat: wire up DI, startup, and middleware in Program.cs"
```

---

### Task 12: Deployment Files

**Files:**
- Create: `deploy/nginx/rg-proxy.conf`
- Create: `deploy/systemd/rg-proxy.service`
- Create: `README.md`

- [ ] **Step 1: Create NGINX config**

```nginx
# deploy/nginx/rg-proxy.conf
server {
    listen 443 ssl;
    server_name dl.yoursite.com;

    ssl_certificate     /etc/letsencrypt/live/dl.yoursite.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/dl.yoursite.com/privkey.pem;

    client_max_body_size 600M;

    # Internal-only file serving — only accessible via X-Accel-Redirect
    location /internal-cache/ {
        internal;
        alias /var/cache/rg-downloads/;
    }

    # API calls → Kestrel
    location /api/ {
        proxy_pass http://127.0.0.1:5050;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 600s;
        proxy_send_timeout 600s;
    }
}

server {
    listen 80;
    server_name dl.yoursite.com;
    return 301 https://$host$request_uri;
}
```

- [ ] **Step 2: Create systemd service**

```ini
# deploy/systemd/rg-proxy.service
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

- [ ] **Step 3: Create README.md**

A brief README covering: project purpose, setup steps, config, deployment commands.

- [ ] **Step 4: Commit**

```bash
git add .
git commit -m "feat: add NGINX, systemd, and README deployment files"
```

---

### Task 13: Build Verification

- [ ] **Step 1: Full clean build**

```bash
cd c:\Users\Admin\gitrepos\KJSProject\RapidgatorProxy
dotnet clean
dotnet build
```

Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 2: Run locally**

```bash
dotnet run --project src/RapidgatorProxy.Api
```

Expected: App starts, SQLite DB created, listening on configured port.

- [ ] **Step 3: Test health endpoint**

```bash
curl http://localhost:5050/api/health
```

Expected: `{ "status": "healthy", "timestamp": "..." }`
