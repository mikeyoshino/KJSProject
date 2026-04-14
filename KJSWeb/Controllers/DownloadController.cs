using Microsoft.AspNetCore.Mvc;
using KJSWeb.Services;

namespace KJSWeb.Controllers;

[Route("download")]
public class DownloadController : Controller
{
    private readonly SupabaseService _supabase;
    private readonly TokenGenService _tokenGen;
    private readonly ExeIoService _exeIo;
    private readonly IConfiguration _config;

    public DownloadController(SupabaseService supabase, TokenGenService tokenGen, ExeIoService exeIo, IConfiguration config)
    {
        _supabase = supabase;
        _tokenGen = tokenGen;
        _exeIo = exeIo;
        _config = config;
    }

    /// <summary>
    /// Step 1 — called when the user clicks the Free Download button.
    /// Checks for a cached exe.io link (destination = /download/file).
    /// If none exists, generates one via the exe.io API and caches it.
    /// Redirects to the exe.io ad page. Falls back to /download/file directly
    /// if exe.io is unavailable.
    /// </summary>
    [Route("public")]
    [HttpGet]
    public async Task<IActionResult> Public(string postId, string table, int part = 0)
    {
        if (string.IsNullOrWhiteSpace(postId) || string.IsNullOrWhiteSpace(table))
            return BadRequest("Missing postId or table.");

        var siteBase = $"{Request.Scheme}://{Request.Host.Value}";

        // /download/file is what exe.io will redirect to after the ad — never /download/public
        var fileUrl = $"{siteBase}/download/file?postId={postId}&table={table}&part={part}";

        List<string>? cachedExeIoLinks;
        List<string> downloadLinks;
        Guid postGuid;

        if (table == "jgirl_posts")
        {
            var post = await _supabase.GetJGirlPostByIdAsync(postId);
            if (post == null || part >= post.DownloadLinks.Count) return NotFound();
            postGuid = post.Id;
            downloadLinks = post.DownloadLinks;
            cachedExeIoLinks = post.ExeIoLinks;
        }
        else
        {
            var post = await _supabase.GetPostByIdAsync(postId);
            if (post == null || post.OurDownloadLink == null || part >= post.OurDownloadLink.Count)
                return NotFound();
            postGuid = post.Id;
            downloadLinks = post.OurDownloadLink;
            cachedExeIoLinks = post.ExeIoLinks;
        }

        // Use cached exe.io link if valid
        var exeIoUrl = (cachedExeIoLinks != null && part < cachedExeIoLinks.Count)
            ? cachedExeIoLinks[part] : null;

        if (string.IsNullOrEmpty(exeIoUrl) || !exeIoUrl.StartsWith("https://exe.io/"))
        {
            // Register /download/file with exe.io — that's the post-ad destination
            exeIoUrl = await _exeIo.GenerateLinkAsync(fileUrl);

            if (exeIoUrl != null)
            {
                var updated = new List<string>(new string[downloadLinks.Count]);
                if (cachedExeIoLinks != null)
                    for (int i = 0; i < Math.Min(cachedExeIoLinks.Count, updated.Count); i++)
                        if (!string.IsNullOrEmpty(cachedExeIoLinks[i]))
                            updated[i] = cachedExeIoLinks[i];
                updated[part] = exeIoUrl;
                _ = _supabase.UpdateExeIoLinksAsync(postGuid, table, updated);
            }
        }

        // Redirect through exe.io (or directly to /download/file if exe.io unavailable)
        return Redirect(exeIoUrl ?? fileUrl);
    }

    /// <summary>
    /// Step 2 — exe.io redirects here after the user completes the ad.
    /// Generates a short-lived JWT and redirects to the Cloudflare Worker
    /// /public-download endpoint which streams the file at 5 MB/s.
    /// </summary>
    [Route("file")]
    [HttpGet]
    public async Task<IActionResult> File(string postId, string table, int part = 0)
    {
        if (string.IsNullOrWhiteSpace(postId) || string.IsNullOrWhiteSpace(table))
            return BadRequest("Missing postId or table.");

        var b2Base     = _config["B2:PublicBaseUrl"]?.TrimEnd('/') ?? "";
        var workerBase = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
        string? b2Path;

        if (table == "jgirl_posts")
        {
            var post = await _supabase.GetJGirlPostByIdAsync(postId);
            if (post == null || part >= post.DownloadLinks.Count) return NotFound();
            var raw = post.DownloadLinks[part];
            b2Path = raw.StartsWith(b2Base) ? raw[b2Base.Length..].TrimStart('/') : raw.TrimStart('/');
        }
        else
        {
            var post = await _supabase.GetPostByIdAsync(postId);
            if (post == null || post.OurDownloadLink == null || part >= post.OurDownloadLink.Count)
                return NotFound();
            b2Path = post.OurDownloadLink[part].TrimStart('/');
        }

        if (string.IsNullOrEmpty(b2Path)) return NotFound();

        var token = _tokenGen.GeneratePublicDownloadToken(b2Path);
        return Redirect($"{workerBase}/public-download?file={Uri.EscapeDataString(b2Path)}&token={Uri.EscapeDataString(token)}");
    }
}
