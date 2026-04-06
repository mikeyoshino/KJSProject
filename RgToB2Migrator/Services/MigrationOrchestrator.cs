using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RgToB2Migrator.Configuration;
using RgToB2Migrator.Models;

namespace RgToB2Migrator.Services;

public class MigrationOrchestrator
{
    private readonly SupabaseMigrationService _supabaseService;
    private readonly RapidgatorDownloadService _rapidgatorService;
    private readonly FileProcessingService _fileProcessingService;
    private readonly GofileUploadService _gofileService;
    private readonly MigratorSettings _settings;
    private readonly ILogger<MigrationOrchestrator> _logger;

    public MigrationOrchestrator(
        SupabaseMigrationService supabaseService,
        RapidgatorDownloadService rapidgatorService,
        FileProcessingService fileProcessingService,
        GofileUploadService gofileService,
        IOptions<MigratorSettings> settings,
        ILogger<MigrationOrchestrator> logger)
    {
        _supabaseService = supabaseService;
        _rapidgatorService = rapidgatorService;
        _fileProcessingService = fileProcessingService;
        _gofileService = gofileService;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task RunAsync(int? limit = null, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Starting Rapidgator → Gofile migration");

            // Reset rows stuck in 'processing' for more than 1 hour back to 'pending'
            await _supabaseService.ResetStuckProcessingRowsAsync(ct);

            int batchSize = limit ?? 50;

            var pendingPosts = await _supabaseService.FetchPendingPostsAsync(batchSize, ct);
            var pendingAsianScandal = await _supabaseService.FetchPendingAsianScandalPostsAsync(
                Math.Max(0, batchSize - pendingPosts.Count), ct);

            var totalPosts = pendingPosts.Count + pendingAsianScandal.Count;
            _logger.LogInformation(
                "Found {Total} pending post(s) ({Posts} posts, {AsianScandal} asianscandal){LimitNote}",
                totalPosts, pendingPosts.Count, pendingAsianScandal.Count,
                limit.HasValue ? $" — limit {limit.Value}" : "");

            var processed = 0;

            foreach (var post in pendingPosts)
            {
                if (ct.IsCancellationRequested || (limit.HasValue && processed >= limit.Value)) break;
                await ProcessPostAsync(post, "posts", ct);
                processed++;
                _logger.LogInformation("Progress: {Processed}/{Total}", processed, totalPosts);
            }

            foreach (var post in pendingAsianScandal)
            {
                if (ct.IsCancellationRequested || (limit.HasValue && processed >= limit.Value)) break;
                await ProcessPostAsync(post, "asianscandal_posts", ct);
                processed++;
                _logger.LogInformation("Progress: {Processed}/{Total}", processed, totalPosts);
            }

            _logger.LogInformation("Migration run complete. Processed {Count} post(s)", processed);
        }
        catch (RapidgatorTrafficExceededException)
        {
            _logger.LogWarning(
                "Migration stopped early — Rapidgator daily traffic exhausted. " +
                "Run again after midnight UTC to continue.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Migration run failed");
            throw;
        }
    }

    private async Task ProcessPostAsync(object post, string tableName, CancellationToken ct)
    {
        dynamic dynamicPost = post;
        var postId = (Guid)dynamicPost.Id;
        var urls = (List<string>)dynamicPost.OriginalRapidgatorUrls;

        _logger.LogInformation("Processing {Table} post {Id} with {Count} URL(s)", tableName, postId, urls.Count);

        var postTempFolder = Path.Combine(_settings.TempFolder, postId.ToString());

        try
        {
            await _supabaseService.MarkProcessingAsync(postId, tableName, ct);
            Directory.CreateDirectory(postTempFolder);

            // ── Create one Gofile folder for the whole post ────────────────────
            var (folderId, downloadPageUrl) = await _gofileService.CreatePostFolderAsync(
                postId.ToString(), ct);

            // ── Counter shared across all archives in this post ────────────────
            var fileCounter = new FileProcessingService.FileCounter();

            // Expand folder URLs → individual file URLs, drop non-Rapidgator junk
            var fileUrls = await ExpandUrlsAsync(urls, postId, ct);

            // ── Process each URL: download → extract → rename → collect files ──
            var allProcessedFiles = new List<FileProcessingService.ProcessedFile>();

            for (int i = 0; i < fileUrls.Count; i++)
            {
                if (ct.IsCancellationRequested) break;

                _logger.LogDebug("URL {Index}/{Total}: {Url}", i + 1, fileUrls.Count, fileUrls[i]);

                try
                {
                    var files = await ProcessOneUrlAsync(fileUrls[i], postTempFolder, fileCounter, ct);
                    allProcessedFiles.AddRange(files);
                }
                catch (RapidgatorTrafficExceededException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process URL {Index}/{Total}: {Url}",
                        i + 1, fileUrls.Count, fileUrls[i]);
                }

                if (i < fileUrls.Count - 1)
                    await Task.Delay(_settings.RateLimitDelayMs, ct);
            }

            // ── Zip all renamed files and upload as one archive ────────────────
            if (allProcessedFiles.Count > 0)
            {
                var zipPath = Path.Combine(postTempFolder, $"{postId}.zip");
                await _fileProcessingService.CreateZipAsync(allProcessedFiles, zipPath, ct);
                await _gofileService.UploadFileAsync(zipPath, $"{postId}.zip", folderId, ct);
            }
            else
            {
                _logger.LogWarning("No files processed for post {Id}", postId);
            }

            // Store the single Gofile folder URL (one per post)
            await _supabaseService.MarkDoneAsync(postId, tableName, [downloadPageUrl], ct);
        }
        catch (RapidgatorTrafficExceededException)
        {
            // Reset this post back to pending so it's retried next run, then stop
            try { await _supabaseService.MarkPendingAsync(postId, tableName, ct); }
            catch { /* ignore */ }
            throw;  // Bubble up to RunAsync to stop the whole migration run
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post {Id} in {Table} failed", (object)postId, (object)tableName);
            try { await _supabaseService.MarkFailedAsync(postId, tableName, ct); }
            catch (Exception ex2) { _logger.LogError(ex2, "Failed to mark post {Id} as failed", (object)postId); }
        }
        finally
        {
            CleanupFolder(postTempFolder);
        }
    }

    /// <summary>
    /// Expands any folder URLs into individual file URLs; drops non-Rapidgator junk.
    /// </summary>
    private async Task<List<string>> ExpandUrlsAsync(
        List<string> urls, Guid postId, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var url in urls)
        {
            if (RapidgatorDownloadService.IsFolderUrl(url))
            {
                _logger.LogInformation("Expanding folder URL for post {Id}: {Url}", postId, url);
                try
                {
                    var fileUrls = await _rapidgatorService.GetFolderFileUrlsAsync(url, ct);
                    result.AddRange(fileUrls);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to expand folder URL: {Url}", url);
                }
            }
            else if (url.Contains("rapidgator.net/file/", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(url);
            }
            else
            {
                _logger.LogDebug("Skipping non-file URL: {Url}", url);
            }
        }
        return result;
    }

    /// <summary>
    /// Downloads one Rapidgator URL, extracts if archive, renames all files,
    /// and returns the processed file list (caller zips and uploads).
    /// </summary>
    private async Task<List<FileProcessingService.ProcessedFile>> ProcessOneUrlAsync(
        string rapidgatorUrl,
        string postTempFolder,
        FileProcessingService.FileCounter fileCounter,
        CancellationToken ct)
    {
        // ── 1. Get download link ───────────────────────────────────────────────
        var (downloadUrl, fileName, fileSize) = await _rapidgatorService.GetDownloadLinkAsync(rapidgatorUrl, ct);
        _logger.LogInformation("Downloading {FileName} ({Size:N0} bytes)", fileName, fileSize);

        var archivePath = Path.Combine(postTempFolder, fileName);

        // ── 2. Download to disk ────────────────────────────────────────────────
        await _rapidgatorService.DownloadFileAsync(downloadUrl, archivePath, ct);

        // ── 2b. Fix missing extension by sniffing magic bytes ─────────────────
        if (string.IsNullOrEmpty(Path.GetExtension(archivePath)))
        {
            var detectedExt = FileProcessingService.DetectExtension(archivePath);
            if (!string.IsNullOrEmpty(detectedExt))
            {
                var fixedPath = archivePath + detectedExt;
                File.Move(archivePath, fixedPath);
                archivePath = fixedPath;
                _logger.LogInformation("Detected file type: {Ext} for {File}", detectedExt, fileName);
            }
        }

        // ── 3. Extract (if archive) or treat as single file ───────────────────
        List<FileProcessingService.ProcessedFile> processedFiles;

        if (_fileProcessingService.IsArchive(archivePath))
        {
            var extractionFolder = await _fileProcessingService.ExtractArchiveAsync(
                archivePath, postTempFolder, ct);
            processedFiles = _fileProcessingService.ProcessExtractedFiles(extractionFolder, fileCounter);
            TryDelete(archivePath);
        }
        else
        {
            var processed = _fileProcessingService.ProcessSingleFile(archivePath, postTempFolder, fileCounter);
            processedFiles = [processed];
        }

        _logger.LogInformation("{Count} file(s) collected from {Url}", processedFiles.Count, fileName);
        return processedFiles;
    }

    private void CleanupFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
                _logger.LogDebug("Cleaned up {Folder}", folder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean up {Folder}", folder);
        }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not delete {Path}", path); }
    }
}
