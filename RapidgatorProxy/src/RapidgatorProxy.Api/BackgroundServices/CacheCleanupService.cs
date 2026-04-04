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
        _logger.LogInformation("Cache cleanup service starting. Interval: {Minutes}min", _cacheSettings.CleanupIntervalMinutes);

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

        // Find expired files
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

        // Purge old records (>7 days) to keep DB clean
        var oldRecords = await db.DownloadEntries
            .Where(e => (e.Status == DownloadStatus.Expired || e.Status == DownloadStatus.Failed)
                && e.CreatedAt < DateTime.UtcNow.AddDays(-7))
            .ToListAsync();
        db.DownloadEntries.RemoveRange(oldRecords);

        await db.SaveChangesAsync();

        if (deletedCount > 0 || oldRecords.Count > 0)
        {
            _logger.LogInformation("Cleanup: deleted {Files} expired files, purged {Records} old records",
                deletedCount, oldRecords.Count);
        }
    }
}
