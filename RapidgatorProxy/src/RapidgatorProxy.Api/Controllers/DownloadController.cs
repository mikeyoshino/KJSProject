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
    private readonly ILogger<DownloadController> _logger;

    public DownloadController(
        DownloadCoordinator coordinator,
        CacheManagerService cacheManager,
        AuthService authService,
        ILogger<DownloadController> logger)
    {
        _coordinator = coordinator;
        _cacheManager = cacheManager;
        _authService = authService;
        _logger = logger;
    }

    private async Task<(bool ok, string? userId)> AuthorizeAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return (false, null);
        var token = authHeader["Bearer ".Length..];
        return await _authService.ValidateTokenAsync(token);
    }

    /// <summary>
    /// Request a file download from Rapidgator.
    /// If the file is already cached, returns status "ready" immediately.
    /// If not, starts a background download and returns status "downloading".
    /// </summary>
    [HttpPost("request")]
    public async Task<IActionResult> RequestDownload([FromBody] DownloadRequest request)
    {
        var (ok, userId) = await AuthorizeAsync();
        if (!ok) return Unauthorized(new { error = "Invalid or expired token" });

        if (string.IsNullOrWhiteSpace(request.RapidgatorUrl))
            return BadRequest(new { error = "rapidgatorUrl is required" });

        try
        {
            var entry = await _coordinator.RequestDownloadAsync(request.RapidgatorUrl, userId);

            return Ok(new DownloadResponse
            {
                DownloadId = entry.Id,
                Status = entry.Status.ToString().ToLower(),
                FileName = string.IsNullOrEmpty(entry.OriginalFileName) ? null : entry.OriginalFileName,
                FileSize = entry.FileSize > 0 ? entry.FileSize : null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download request failed for {Url}", request.RapidgatorUrl);
            return StatusCode(500, new { error = "Failed to process download request" });
        }
    }

    /// <summary>
    /// Poll the download progress for a given downloadId.
    /// </summary>
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
        if (entry.Status == DownloadStatus.Downloading && entry.DownloadedBytes > 0)
        {
            var elapsed = (DateTime.UtcNow - entry.CreatedAt).TotalSeconds;
            if (elapsed > 0)
            {
                var rate = entry.DownloadedBytes / elapsed;
                if (rate > 0)
                    eta = (int)((entry.FileSize - entry.DownloadedBytes) / rate);
            }
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

    /// <summary>
    /// Get the file. Returns X-Accel-Redirect for NGINX to serve the file directly.
    /// In development (no NGINX), falls back to serving the file via Kestrel.
    /// </summary>
    [HttpGet("file/{downloadId}")]
    public async Task<IActionResult> GetFile(string downloadId)
    {
        var (ok, _) = await AuthorizeAsync();
        if (!ok) return Unauthorized(new { error = "Invalid or expired token" });

        var entry = await _cacheManager.GetByIdAsync(downloadId);
        if (entry == null) return NotFound(new { error = "Download not found" });

        if (entry.Status == DownloadStatus.Downloading || entry.Status == DownloadStatus.Pending)
        {
            var progress = entry.FileSize > 0
                ? Math.Round((double)entry.DownloadedBytes / entry.FileSize * 100, 1)
                : 0;
            return Conflict(new { error = "File is still downloading", progress });
        }

        if (entry.Status == DownloadStatus.Failed)
            return BadRequest(new { error = "Download failed", message = entry.ErrorMessage });

        if (entry.Status == DownloadStatus.Expired)
            return StatusCode(410, new { error = "File has expired. Please request a new download." });

        if (entry.Status != DownloadStatus.Ready)
            return BadRequest(new { error = $"Unexpected status: {entry.Status}" });

        await _cacheManager.TouchAccessAsync(downloadId);

        // X-Accel-Redirect: NGINX serves the file, ASP.NET Core sends zero bytes
        Response.Headers["X-Accel-Redirect"] = $"/internal-cache/{entry.CachedFileName}";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{entry.OriginalFileName}\"";
        Response.ContentType = "application/octet-stream";
        return new EmptyResult();
    }
}
