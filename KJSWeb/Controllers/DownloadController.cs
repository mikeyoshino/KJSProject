using Microsoft.AspNetCore.Mvc;
using KJSWeb.Services;

namespace KJSWeb.Controllers;

[Route("download")]
public class DownloadController : Controller
{
    private readonly SupabaseService _supabase;
    private readonly TokenGenService _tokenGen;
    private readonly IConfiguration _config;

    public DownloadController(SupabaseService supabase, TokenGenService tokenGen, IConfiguration config)
    {
        _supabase = supabase;
        _tokenGen = tokenGen;
        _config = config;
    }

    /// <summary>
    /// Generates a short-lived (30 min) token and redirects to the Cloudflare Worker
    /// /public-download endpoint. Publicly accessible — no subscription required.
    /// The exe.io link wraps this URL so each visit goes through the ad page first.
    /// </summary>
    [Route("public")]
    [HttpGet]
    public async Task<IActionResult> Public(string postId, string table, int part = 0)
    {
        if (string.IsNullOrWhiteSpace(postId) || string.IsNullOrWhiteSpace(table))
            return BadRequest("Missing postId or table.");

        var b2Base = _config["B2:PublicBaseUrl"]?.TrimEnd('/') ?? "";
        string? b2Path = null;

        if (table == "jgirl_posts")
        {
            var post = await _supabase.GetJGirlPostByIdAsync(postId);
            if (post == null || part >= post.DownloadLinks.Count) return NotFound();

            var raw = post.DownloadLinks[part];
            b2Path = raw.StartsWith(b2Base)
                ? raw[b2Base.Length..].TrimStart('/')
                : raw.TrimStart('/');
        }
        else
        {
            var post = await _supabase.GetPostByIdAsync(postId);
            if (post == null || post.OurDownloadLink == null || part >= post.OurDownloadLink.Count)
                return NotFound();

            b2Path = post.OurDownloadLink[part].TrimStart('/');
        }

        if (string.IsNullOrEmpty(b2Path)) return NotFound();

        var workerBaseUrl = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
        var token = _tokenGen.GeneratePublicDownloadToken(b2Path);
        var redirectUrl = $"{workerBaseUrl}/public-download?file={Uri.EscapeDataString(b2Path)}&token={Uri.EscapeDataString(token)}";

        return Redirect(redirectUrl);
    }
}
