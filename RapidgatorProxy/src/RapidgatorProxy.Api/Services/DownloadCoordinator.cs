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

        // Quick check: already cached or in progress?
        var existing = await _cacheManager.FindCachedAsync(normalizedUrl);
        if (existing != null)
        {
            _logger.LogInformation("Cache hit for {Url}, status: {Status}", normalizedUrl, existing.Status);
            return existing;
        }

        // Acquire per-URL lock to prevent duplicate downloads
        var urlLock = _urlLocks.GetOrAdd(normalizedUrl, _ => new SemaphoreSlim(1, 1));
        await urlLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            existing = await _cacheManager.FindCachedAsync(normalizedUrl);
            if (existing != null) return existing;

            var entry = new DownloadEntry
            {
                RapidgatorUrl = normalizedUrl,
                CachedFileName = $"{Guid.NewGuid()}.download",
                Status = DownloadStatus.Pending,
                RequestedByUserId = userId
            };

            // Persist the entry
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.DownloadEntries.Add(entry);
                await db.SaveChangesAsync();
            }

            // Start background download (fire and forget with concurrency control)
            _ = Task.Run(async () =>
            {
                await _globalDownloadLimit.WaitAsync();
                try
                {
                    _logger.LogInformation("Starting download for {Url}", normalizedUrl);
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
