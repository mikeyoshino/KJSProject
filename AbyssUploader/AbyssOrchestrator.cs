using AbyssUploader.Configuration;
using AbyssUploader.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace AbyssUploader;

public class AbyssOrchestrator
{
    private readonly SupabaseMigrationService _supabase;
    private readonly B2DownloadService _b2;
    private readonly AbyssUploadService _abyss;
    private readonly AbyssSettings _settings;
    private readonly ILogger<AbyssOrchestrator> _logger;

    private const long BytesPerGb = 1024L * 1024 * 1024;

    public AbyssOrchestrator(
        SupabaseMigrationService supabase,
        B2DownloadService b2,
        AbyssUploadService abyss,
        IOptions<AbyssSettings> settings,
        ILogger<AbyssOrchestrator> logger)
    {
        _supabase = supabase;
        _b2 = b2;
        _abyss = abyss;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var dailyLimitBytes = (long)(_settings.DailyLimitGb * BytesPerGb);
        long bytesUploadedThisRun = 0;

        _logger.LogInformation("AbyssUploader starting. Daily limit: {LimitGb} GB", _settings.DailyLimitGb);

        var posts = await _supabase.FetchUnprocessedPostsAsync(_settings.BatchSize, ct);

        if (posts.Count == 0)
        {
            _logger.LogInformation("No unprocessed posts found. Exiting.");
            return;
        }

        Directory.CreateDirectory(_settings.TempFolder);

        foreach (var post in posts)
        {
            ct.ThrowIfCancellationRequested();

            if (bytesUploadedThisRun >= dailyLimitBytes)
            {
                _logger.LogWarning("Daily upload limit reached ({Gb} GB). Stopping.", _settings.DailyLimitGb);
                break;
            }

            var postTempDir = Path.Combine(_settings.TempFolder, post.Id.ToString());
            try
            {
                _logger.LogInformation("Processing post {Id} ({Count} zip(s))", post.Id, post.OurDownloadLink.Count);

                var extractDir = Path.Combine(postTempDir, "extracted");
                Directory.CreateDirectory(extractDir);

                // Download and extract each zip
                foreach (var zipUrl in post.OurDownloadLink)
                {
                    ct.ThrowIfCancellationRequested();
                    var zipFilename = Path.GetFileName(zipUrl.Replace('\\', '/'));
                    var zipPath = Path.Combine(postTempDir, zipFilename);
                    await _b2.DownloadAsync(zipUrl, zipPath, ct);
                    ExtractArchive(zipPath, extractDir);
                    File.Delete(zipPath);
                }

                // Check daily limit before uploading
                var videoSize = AbyssUploadService.GetVideoFilesSize(extractDir);
                if (bytesUploadedThisRun + videoSize > dailyLimitBytes)
                {
                    _logger.LogWarning("Post {Id} would exceed daily limit, stopping", post.Id);
                    break;
                }

                // Upload videos to Abyss.to
                var videos = await _abyss.UploadVideosAsync(extractDir, ct);

                // Mark done (even if no videos — avoids infinite retry on image-only posts)
                await _supabase.MarkStreamingDoneAsync(post.Id, videos, ct);

                bytesUploadedThisRun += videoSize;
                _logger.LogInformation("Post {Id} done. {Count} video(s) uploaded. Run total: {Gb:F2} GB",
                    post.Id, videos.Count, bytesUploadedThisRun / (double)BytesPerGb);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing post {Id} — skipping", post.Id);
            }
            finally
            {
                if (Directory.Exists(postTempDir))
                {
                    try { Directory.Delete(postTempDir, recursive: true); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete temp dir {Dir}", postTempDir); }
                }
            }
        }

        _logger.LogInformation("Run complete. Total uploaded this run: {Gb:F2} GB",
            bytesUploadedThisRun / (double)BytesPerGb);
    }

    private void ExtractArchive(string archivePath, string destDir)
    {
        _logger.LogInformation("Extracting {Archive} → {Dir}", archivePath, destDir);
        using var archive = ArchiveFactory.Open(archivePath);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            entry.WriteToDirectory(destDir, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });
        }
    }
}
