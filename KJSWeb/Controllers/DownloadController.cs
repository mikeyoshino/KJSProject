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
    /// Free download entry point.
    /// 1. Gets the b2Path for the requested file.
    /// 2. Generates a 30-min JWT and builds the CF Worker download URL.
    /// 3. Registers that URL with exe.io to get an ad-gated short link.
    /// 4. Redirects the user to https://exe.io/xxxxx — they see the ad page,
    ///    then exe.io sends them to the CF Worker URL which streams at 5 MB/s.
    /// Falls back to the CF Worker URL directly if exe.io is unavailable.
    /// </summary>
    [Route("public")]
    [HttpGet]
    public async Task<IActionResult> Public(string postId, string table, int part = 0)
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

        // Build a short-lived CF Worker download URL — this becomes the exe.io destination
        var token     = _tokenGen.GeneratePublicDownloadToken(b2Path);
        var workerUrl = $"{workerBase}/public-download?file={Uri.EscapeDataString(b2Path)}&token={Uri.EscapeDataString(token)}";

        // Get an exe.io ad-gated link wrapping the CF Worker URL
        var exeIoUrl = await _exeIo.GenerateLinkAsync(workerUrl);

        // Redirect to exe.io ad page, or fall back to direct download if exe.io fails
        return Redirect(exeIoUrl ?? workerUrl);
    }
}
