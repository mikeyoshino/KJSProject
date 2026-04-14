using Microsoft.AspNetCore.Mvc;
using KJSWeb.Models;
using KJSWeb.Services;

namespace KJSWeb.Controllers;

[Route("jgirl")]
public class JGirlController : Controller
{
    private readonly SupabaseService _supabase;
    private readonly TokenGenService _tokenGen;
    private readonly IConfiguration _config;
    private const int PageSize = 24;

    private readonly ExeIoService _exeIo;

    public JGirlController(SupabaseService supabase, TokenGenService tokenGen, IConfiguration config, ExeIoService exeIo)
    {
        _supabase = supabase;
        _tokenGen = tokenGen;
        _config   = config;
        _exeIo    = exeIo;
    }

    public async Task<IActionResult> Index(int page = 1, string? source = null)
    {
        if (page < 1) page = 1;

        var (posts, totalCount) = await _supabase.GetJGirlPostsAsync(page, PageSize, source);
        var sources = await _supabase.GetJGirlSourcesAsync();

        foreach (var p in posts)
            RewritePost(p);

        ViewBag.Pagination = new PaginationInfo
        {
            CurrentPage = page,
            PageSize    = PageSize,
            TotalItems  = totalCount,
            TotalPages  = (int)Math.Ceiling(totalCount / (double)PageSize)
        };
        ViewBag.Sources       = sources;
        ViewBag.ActiveSource  = source;

        ViewData["OgTitle"]    = "JGirl — SCANDAL69";
        ViewData["Description"] = "Browse the latest JGirl content on SCANDAL69.";
        ViewData["OgType"]     = "website";
        return View(posts);
    }

    [Route("post/{id}")]
    public async Task<IActionResult> Details(string id)
    {
        var post = await _supabase.GetJGirlPostByIdAsync(id);
        if (post == null) return NotFound();

        var workerBase = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
        var b2Base     = _config["B2:PublicBaseUrl"]?.TrimEnd('/') ?? "https://f005.backblazeb2.com/file/KJSProject";

        // Capture original download links before URL rewriting (needed for token generation)
        var originalDownloadLinks = post.DownloadLinks.ToList();

        RewritePost(post);

        // Lazy-generate and cache exe.io public download links (use original links before RewritePost rewrites them)
        if (originalDownloadLinks.Any())
        {
            if (post.ExeIoLinks == null || !post.ExeIoLinks.Any())
            {
                var siteBase = $"{Request.Scheme}://{Request.Host.Value}";
                var generated = new List<string>();
                for (int i = 0; i < originalDownloadLinks.Count; i++)
                {
                    var ksjUrl = $"{siteBase}/download/public?postId={post.Id}&table=jgirl_posts&part={i}";
                    var exeUrl = await _exeIo.GenerateLinkAsync(ksjUrl);
                    if (exeUrl != null) generated.Add(exeUrl);
                }
                if (generated.Any())
                {
                    post.ExeIoLinks = generated;
                    _ = _supabase.UpdateExeIoLinksAsync(post.Id, "jgirl_posts", generated);
                }
            }
            ViewBag.PublicDownloadUrls = post.ExeIoLinks;
        }

        ViewData["OgTitle"]    = post.Title;
        ViewData["OgImage"]    = !string.IsNullOrEmpty(post.ThumbnailUrl) ? post.ThumbnailUrl : post.Images.FirstOrDefault() ?? "";
        ViewData["Description"] = post.Tags.Any() ? string.Join(", ", post.Tags.Take(10)) : post.Title;
        ViewData["OgType"]     = "article";

        var userId = HttpContext.Session.GetString("user_id");
        if (!string.IsNullOrEmpty(userId))
        {
            var activeSub = await _supabase.GetActiveSubscriptionAsync(userId);
            ViewBag.HasActiveSubscription = activeSub != null;

            if (activeSub != null && originalDownloadLinks.Any())
            {
                ViewBag.DownloadUrls = originalDownloadLinks.Select(url =>
                {
                    var clean = url.StartsWith(b2Base)
                        ? url[b2Base.Length..].TrimStart('/')
                        : url.TrimStart('/');
                    var token = _tokenGen.GenerateDownloadToken(userId, clean);
                    return $"{workerBase}/download?file={Uri.EscapeDataString(clean)}&token={Uri.EscapeDataString(token)}";
                }).ToList();
            }
        }
        else
        {
            ViewBag.HasActiveSubscription = false;
        }

        return View(post);
    }

    private void RewritePost(JGirlPost post)
    {
        var workerBase = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
        var b2Base     = _config["B2:PublicBaseUrl"]?.TrimEnd('/')
                         ?? "https://f005.backblazeb2.com/file/KJSProject";

        post.ThumbnailUrl  = Rewrite(post.ThumbnailUrl, workerBase, b2Base);
        post.Images        = post.Images.Select(u => Rewrite(u, workerBase, b2Base)).ToList();
        post.PostImages    = post.PostImages.Select(u => Rewrite(u, workerBase, b2Base)).ToList();
        post.DownloadLinks = post.DownloadLinks.Select(u => Rewrite(u, workerBase, b2Base)).ToList();
    }

    private static string Rewrite(string url, string workerBase, string b2Base)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(workerBase)) return url;
        if (url.StartsWith(b2Base))
            return workerBase + url[b2Base.Length..];
        return url;
    }
}
