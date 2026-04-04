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
            // Get direct download URL from Rapidgator API
            var (downloadUrl, fileName, fileSize) = await _rgClient.GetDownloadLinkAsync(entry.RapidgatorUrl, ct);

            entry.OriginalFileName = fileName;
            entry.FileSize = fileSize;
            entry.Status = DownloadStatus.Downloading;
            await UpdateEntryAsync(entry);

            var filePath = Path.Combine(_cacheSettings.Directory, entry.CachedFileName);
            Directory.CreateDirectory(_cacheSettings.Directory);

            // Stream download — never buffer entire file in memory
            using var httpClient = _rgClient.CreateDownloadClient();
            using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            if (entry.FileSize == 0 && response.Content.Headers.ContentLength.HasValue)
            {
                entry.FileSize = response.Content.Headers.ContentLength.Value;
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920]; // 80KB buffer
            long totalRead = 0;
            var lastUpdate = DateTime.UtcNow;

            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                // Update progress every 2 seconds to avoid hammering SQLite
                if ((DateTime.UtcNow - lastUpdate).TotalSeconds >= 2)
                {
                    entry.DownloadedBytes = totalRead;
                    await UpdateEntryAsync(entry);
                    lastUpdate = DateTime.UtcNow;
                }
            }

            // Mark complete
            entry.DownloadedBytes = totalRead;
            entry.FileSize = totalRead;
            entry.Status = DownloadStatus.Ready;
            entry.CompletedAt = DateTime.UtcNow;
            entry.ExpiresAt = DateTime.UtcNow.AddHours(_cacheSettings.FileExpiryHours);
            entry.LastAccessedAt = DateTime.UtcNow;
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
