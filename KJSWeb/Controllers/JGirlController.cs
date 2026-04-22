using System.Security.Claims;
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

    public JGirlController(SupabaseService supabase, TokenGenService tokenGen, IConfiguration config)
    {
        _supabase = supabase;
        _tokenGen = tokenGen;
        _config   = config;
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

        ViewData["OgTitle"]    = "JGirl – Japanese Photobooks & Upskirt Clips | SCANDAL69";
        ViewData["Description"] = "Browse exclusive JGirl Japanese photobooks, upskirt clips, and bathroom videos on SCANDAL69. High-quality content updated regularly.";
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

        // Fetch related posts in parallel with subscription check
        var relatedTask = _supabase.GetRelatedJGirlPostsAsync(post.Id, post.Tags?.ToList() ?? [], post.Source, limit: 6);

        // Capture original download links before URL rewriting (needed for token generation)
        var originalDownloadLinks = post.DownloadLinks.ToList();

        RewritePost(post);

        ViewData["OgTitle"]    = post.Title;
        ViewData["OgImage"]    = !string.IsNullOrEmpty(post.ThumbnailUrl) ? post.ThumbnailUrl : post.Images.FirstOrDefault() ?? "";
        ViewData["Description"] = post.Tags.Any() ? string.Join(", ", post.Tags.Take(10)) : post.Title;
        ViewData["OgType"]     = "article";

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
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

        // Rewrite thumbnails for related posts
        var related = await relatedTask;
        foreach (var rp in related)
            rp.ThumbnailUrl = ResolveRelatedThumb(rp.ThumbnailUrl, workerBase, b2Base);

        ViewBag.RelatedPosts = related;

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

    private static string ResolveRelatedThumb(string url, string workerBase, string b2Base)
        => Rewrite(url, workerBase, b2Base);
}
