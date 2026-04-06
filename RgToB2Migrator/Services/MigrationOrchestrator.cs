using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RgToB2Migrator.Configuration;
using RgToB2Migrator.Models;
using System.Collections.Concurrent;

namespace RgToB2Migrator.Services;

public class MigrationOrchestrator
{
    private readonly SupabaseMigrationService _supabaseService;
    private readonly RapidgatorDownloadService _rapidgatorService;
    private readonly FileProcessingService _fileProcessingService;
    private readonly B2UploadService _b2Service;
    private readonly MigratorSettings _settings;
    private readonly ILogger<MigrationOrchestrator> _logger;

    public MigrationOrchestrator(
        SupabaseMigrationService supabaseService,
        RapidgatorDownloadService rapidgatorService,
        FileProcessingService fileProcessingService,
        B2UploadService b2Service,
        IOptions<MigratorSettings> settings,
        ILogger<MigrationOrchestrator> logger)
    {
        _supabaseService = supabaseService;
        _rapidgatorService = rapidgatorService;
        _fileProcessingService = fileProcessingService;
        _b2Service = b2Service;
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

            // (B2 uses flat object keys, so no folder creation is necessary)
            string b2ObjectKey = string.Empty;

            // ── Counter shared across all archives in this post ────────────────
            var fileCounter = new FileProcessingService.FileCounter();

            // Expand folder URLs → individual file URLs, drop non-Rapidgator junk
            var fileUrls = await ExpandUrlsAsync(urls, postId, ct);

            // ── Process each URL in parallel (2 at a time) ──────────────────────
            var allProcessedFiles = new ConcurrentBag<FileProcessingService.ProcessedFile>();
            using var semaphore = new SemaphoreSlim(2);
            
            var tasks = fileUrls.Select(async (url, i) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    // Small staggered start to avoid simultaneous Rapidgator API hits
                    if (i > 0) await Task.Delay(1000, ct); 
                    
                    var files = await ProcessOneUrlWithRetryAsync(url, postTempFolder, fileCounter, i + 1, fileUrls.Count, ct);
                    foreach (var file in files) allProcessedFiles.Add(file);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            // ── Zip all renamed files and upload as one archive ────────────────
            if (allProcessedFiles.Count > 0)
            {
                var zipName = $"{postId}.zip";
                var zipPath = Path.Combine(postTempFolder, zipName);
                await _fileProcessingService.CreateZipAsync(allProcessedFiles, zipPath, ct);
                
                // Upload zip to B2 using prefix posts/{postId}/{postId}.zip
                var key = $"posts/{postId}/{zipName}";
                b2ObjectKey = await _b2Service.UploadFileAsync(zipPath, key, ct);
            }
            else
            {
                _logger.LogWarning("No files processed for post {Id}", postId);
            }

            // Store the B2 object key (or empty if failed)
            await _supabaseService.MarkDoneAsync(postId, tableName, [b2ObjectKey], ct);
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
    /// Wrapper around ProcessOneUrlAsync that adds retry logic.
    /// </summary>
    private async Task<List<FileProcessingService.ProcessedFile>> ProcessOneUrlWithRetryAsync(
        string rapidgatorUrl,
        string postTempFolder,
        FileProcessingService.FileCounter fileCounter,
        int index,
        int total,
        CancellationToken ct)
    {
        int maxRetries = 3;
        int delayMs = 5000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("[{Index}/{Total}] Processing URL (Attempt {Attempt}/{Max})", 
                    index, total, attempt, maxRetries);
                
                return await ProcessOneUrlAsync(rapidgatorUrl, postTempFolder, fileCounter, ct);
            }
            catch (RapidgatorTrafficExceededException)
            {
                throw; // Don't retry traffic exceeded
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Attempt {Attempt} failed for URL {Url}: {Message}", 
                    attempt, rapidgatorUrl, ex.Message);
                
                if (attempt == maxRetries)
                {
                    _logger.LogError(ex, "URL failed after {Max} attempts: {Url}", maxRetries, rapidgatorUrl);
                    return new List<FileProcessingService.ProcessedFile>();
                }
                
                // Exponential backoff
                await Task.Delay(delayMs * attempt, ct);
            }
        }

        return new List<FileProcessingService.ProcessedFile>();
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
