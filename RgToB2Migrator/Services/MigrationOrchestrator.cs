using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RgToB2Migrator.Configuration;
using RgToB2Migrator.Models;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

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

            _logger.LogInformation("Repairing database status before migration run...");
            // Reset rows stuck in 'processing' or 'done' but empty back to 'pending'
            await _supabaseService.ResetStuckProcessingRowsAsync(ct);
            _logger.LogInformation("Database repair complete.");

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

            // Expand folder URLs → individual file URLs, drop non-Rapidgator junk
            var fileUrls = await ExpandUrlsAsync(urls, postId, ct);

            // ── Phase 1: Download all files in parallel (2 at a time) ─────────
            var downloadedPaths = new ConcurrentBag<string>();
            using var semaphore = new SemaphoreSlim(2);

            var downloadTasks = fileUrls.Select(async (url, i) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    if (i > 0) await Task.Delay(1000, ct);
                    var localPath = await DownloadOneUrlWithRetryAsync(url, postTempFolder, i + 1, fileUrls.Count, ct);
                    if (localPath != null) downloadedPaths.Add(localPath);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(downloadTasks);

            // ── Phase 2: Extract — skip non-first RAR volumes ─────────────────
            // For multi-part RARs (part1/part2/…), SharpCompress reads subsequent
            // volumes automatically when opening part1. Opening part2+ alone always
            // fails with "Need to start from first volume", so we skip them here.
            var fileCounter = new FileProcessingService.FileCounter();
            var allProcessedFiles = new List<FileProcessingService.ProcessedFile>();

            foreach (var filePath in downloadedPaths.OrderBy(f => f))
            {
                if (_fileProcessingService.IsArchive(filePath))
                {
                    if (IsNonFirstRarVolume(filePath))
                    {
                        _logger.LogInformation("Skipping non-first RAR volume (will be read via part1): {File}", Path.GetFileName(filePath));
                        continue;
                    }

                    try
                    {
                        var extractionFolder = await _fileProcessingService.ExtractArchiveAsync(filePath, postTempFolder, ct);
                        var extracted = _fileProcessingService.ProcessExtractedFiles(extractionFolder, fileCounter);
                        allProcessedFiles.AddRange(extracted);
                        if (extracted.Count == 0)
                            _logger.LogWarning("Archive extraction yielded 0 files: {File}", Path.GetFileName(filePath));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Extraction failed (non-retryable): {File}", Path.GetFileName(filePath));
                    }

                    TryDelete(filePath);
                }
                else
                {
                    var processed = _fileProcessingService.ProcessSingleFile(filePath, postTempFolder, fileCounter);
                    allProcessedFiles.Add(processed);
                }
            }

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
                _logger.LogWarning("No files were successfully processed/collected for post {Id} from {Count} source URL(s)", 
                    postId, fileUrls.Count);
            }

            // ── Store the B2 object key or mark as failed ──────────────────────
            if (!string.IsNullOrWhiteSpace(b2ObjectKey))
            {
                await _supabaseService.MarkDoneAsync(postId, tableName, [b2ObjectKey], ct);
            }
            else
            {
                _logger.LogWarning("Post {Id} in {Table} produced no B2 archive; marking as failed", postId, tableName);
                await _supabaseService.MarkFailedAsync(postId, tableName, ct);
            }
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
        
        _logger.LogInformation("Post {Id} expanded to {Count} individual Rapidgator URL(s)", postId, result.Count);
        return result;
    }

    /// <summary>
    /// Downloads one Rapidgator URL to disk with retry logic (network errors only).
    /// Returns the local file path, or null if all attempts fail.
    /// </summary>
    private async Task<string?> DownloadOneUrlWithRetryAsync(
        string rapidgatorUrl,
        string postTempFolder,
        int index,
        int total,
        CancellationToken ct)
    {
        int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("[dl] [{Index}/{Total}] attempt {Attempt}: {Url}",
                    index, total, attempt, rapidgatorUrl);

                var (downloadUrl, fileName, fileSize) = await _rapidgatorService.GetDownloadLinkAsync(rapidgatorUrl, ct);
                var archivePath = Path.Combine(postTempFolder, fileName);

                await _rapidgatorService.DownloadFileAsync(downloadUrl, archivePath, ct);

                // Fix missing extension by sniffing magic bytes
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

                return archivePath;
            }
            catch (RapidgatorTrafficExceededException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[dl] [{Index}/{Total}] attempt {Attempt} failed: {Message}",
                    index, total, attempt, ex.Message);

                if (attempt == maxRetries)
                {
                    _logger.LogError("[dl] [{Index}/{Total}] giving up: {Url}", index, total, rapidgatorUrl);
                    return null;
                }

                await Task.Delay(5000 * attempt, ct);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true for non-first volumes of a multi-part RAR set:
    /// new format (part2.rar, part3.rar, …) and old format (.r00, .r01, …).
    /// These must be skipped during extraction — SharpCompress reads them
    /// automatically when opening the first volume.
    /// </summary>
    private static bool IsNonFirstRarVolume(string filePath)
    {
        var name = Path.GetFileName(filePath);
        // New format: filename.part2.rar, filename.part3.rar, etc.
        var m = Regex.Match(name, @"\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
        if (m.Success && int.Parse(m.Groups[1].Value) > 1)
            return true;
        // Old format: filename.r00, filename.r01, etc.
        if (Regex.IsMatch(name, @"\.r\d+$", RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    public async Task RunThumbnailsAsync(int? limit = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting thumbnail migration → B2");

        int batchSize = limit ?? 50;

        var posts = await _supabaseService.FetchPendingThumbnailPostsAsync(batchSize, ct);
        var asianScandal = await _supabaseService.FetchPendingThumbnailAsianScandalPostsAsync(
            Math.Max(0, batchSize - posts.Count), ct);

        var total = posts.Count + asianScandal.Count;
        _logger.LogInformation("Found {Total} post(s) with external thumbnails ({Posts} posts, {AS} asianscandal)",
            total, posts.Count, asianScandal.Count);

        if (total == 0)
        {
            _logger.LogInformation("No thumbnails to migrate.");
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        int processed = 0;

        async Task MigrateThumbnailAsync(Guid postId, string tableName, string thumbnailUrl)
        {
            var postTempFolder = Path.Combine(_settings.TempFolder, postId.ToString());
            Directory.CreateDirectory(postTempFolder);

            try
            {
                _logger.LogInformation("[{Done}/{Total}] Migrating thumbnail for post {Id} from {Url}",
                    processed + 1, total, postId, thumbnailUrl);

                // Download thumbnail
                using var response = await http.GetAsync(thumbnailUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                // Detect extension
                var ext = DetectImageExtension(response.Content.Headers.ContentType?.MediaType, thumbnailUrl);
                var localPath = Path.Combine(postTempFolder, $"thumbnail{ext}");

                await using (var fs = File.Create(localPath))
                    await response.Content.CopyToAsync(fs, ct);

                // Upload to B2
                var b2Key = $"posts/{postId}/thumbnail{ext}";
                await _b2Service.UploadFileAsync(localPath, b2Key, ct);

                // Update Supabase
                await _supabaseService.UpdateThumbnailUrlAsync(postId, tableName, b2Key, ct);

                _logger.LogInformation("Thumbnail migrated → {Key}", b2Key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to migrate thumbnail for post {Id} in {Table}", postId, tableName);
            }
            finally
            {
                CleanupFolder(postTempFolder);
                processed++;
            }
        }

        foreach (var post in posts)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(post.ThumbnailUrl)) continue;
            await MigrateThumbnailAsync(post.Id, "posts", post.ThumbnailUrl);
        }

        foreach (var post in asianScandal)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(post.ThumbnailUrl)) continue;
            await MigrateThumbnailAsync(post.Id, "asianscandal_posts", post.ThumbnailUrl);
        }

        _logger.LogInformation("Thumbnail migration complete. Processed {Count} post(s)", processed);
    }

    private static string DetectImageExtension(string? contentType, string url)
    {
        var ext = contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png"  => ".png",
            "image/gif"  => ".gif",
            "image/webp" => ".webp",
            _ => null
        };

        if (ext != null) return ext;

        // Fallback: extract from URL path
        try
        {
            var path = new Uri(url).AbsolutePath;
            var urlExt = Path.GetExtension(path).ToLowerInvariant();
            if (urlExt is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp")
                return urlExt == ".jpeg" ? ".jpg" : urlExt;
        }
        catch { /* ignore malformed URLs */ }

        return ".jpg";
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
