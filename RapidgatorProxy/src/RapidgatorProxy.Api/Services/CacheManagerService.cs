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
                && (e.Status == DownloadStatus.Ready || e.Status == DownloadStatus.Downloading || e.Status == DownloadStatus.Pending)
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

        _logger.LogWarning("Cache size {SizeGB:F1}GB exceeds limit {LimitGB}GB, evicting LRU files",
            totalSize / 1_073_741_824.0, _cacheSettings.MaxSizeGB);

        var toEvict = await db.DownloadEntries
            .Where(e => e.Status == DownloadStatus.Ready)
            .OrderBy(e => e.LastAccessedAt ?? e.CompletedAt)
            .Take(10)
            .ToListAsync();

        foreach (var entry in toEvict)
        {
            var path = Path.Combine(_cacheSettings.Directory, entry.CachedFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("Evicted cached file: {File}", entry.CachedFileName);
            }
            entry.Status = DownloadStatus.Expired;
        }

        await db.SaveChangesAsync();
    }
}
