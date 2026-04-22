using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using KJSWeb.Models;
using KJSWeb.Services;

namespace KJSWeb.Controllers;

[Route("asian-scandal")]
public class AsianScandalController : Controller
{
    private readonly SupabaseService _supabase;
    private readonly TokenGenService _tokenGen;
    private readonly IConfiguration _config;
    private readonly ILogger<AsianScandalController> _logger;
    private const int PageSize = 24;

    public AsianScandalController(SupabaseService supabase, TokenGenService tokenGen, IConfiguration config, ILogger<AsianScandalController> logger)
    {
        _supabase = supabase;
        _tokenGen = tokenGen;
        _config = config;
        _logger = logger;
    }

    public async Task<IActionResult> Index(int page = 1, string period = "week")
    {
        if (page < 1) page = 1;

        var workerBase = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";

        var (posts, totalCount) = await _supabase.GetLatestPostsAsync(page, PageSize, PostSource.AsianScandal);
        foreach (var p in posts)
            p.ThumbnailUrl = ResolveImageUrl(p.ThumbnailUrl, workerBase);

        var categories   = await _supabase.GetCategoriesAsync();
        var popularPosts = await _supabase.GetPopularPostsAsync(6, PostSource.AsianScandal, period);
        foreach (var p in popularPosts)
            p.ThumbnailUrl = ResolveImageUrl(p.ThumbnailUrl, workerBase);

        ViewBag.Pagination = new PaginationInfo
        {
            CurrentPage = page,
            PageSize = PageSize,
            TotalItems = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)PageSize)
        };
        ViewBag.SourceTitle       = "Asian Scandal";
        ViewBag.SourcePrefix      = "asian-scandal";
        ViewBag.SidebarCategories = categories;
        ViewBag.PopularPosts      = popularPosts;
        ViewBag.PopularPeriod     = period;
        ViewBag.ShowSidebar       = true;

        ViewData["OgTitle"]    = "Asian Scandal – Exclusive Leaked Videos | SCANDAL69";
        ViewData["Description"] = "Watch and download exclusive Asian scandal leaked videos on SCANDAL69. Uncensored content from Japan, Korea, and across Asia. Updated daily.";
        ViewData["OgType"]     = "website";
        return View("~/Views/Home/Listing.cshtml", posts);
    }

    [Route("popular")]
    public async Task<IActionResult> Popular(string period = "week")
    {
        var workerBase   = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
        var popularPosts = await _supabase.GetPopularPostsAsync(6, PostSource.AsianScandal, period);
        foreach (var p in popularPosts)
            p.ThumbnailUrl = ResolveImageUrl(p.ThumbnailUrl, workerBase);

        ViewBag.PopularPeriod = period;
        ViewBag.SourcePrefix  = "asian-scandal";
        return PartialView("_PopularPosts", popularPosts);
    }

    [Route("post/{id}")]
    public async Task<IActionResult> Details(string id)
    {
        var post = await _supabase.GetPostByIdAsync(id);
        if (post == null) return NotFound();

        var relatedTask = _supabase.GetRelatedPostsAsync(post.Id, post.Tags?.ToList() ?? [], PostSource.AsianScandal, limit: 6);
        var (recentPosts, _) = await _supabase.GetLatestPostsAsync(1, 5);
        ViewBag.RecentPosts = recentPosts;

        var workerBaseUrl = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
        ViewBag.WorkerBaseUrl = workerBaseUrl;
        ViewBag.B2DirectBase = _config["B2:PublicBaseUrl"]?.TrimEnd('/') ?? "https://f005.backblazeb2.com/file/KJSProject";

        post.ThumbnailUrl = ResolveImageUrl(post.ThumbnailUrl, workerBaseUrl);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(userId))
        {
            var activeSub = await _supabase.GetActiveSubscriptionAsync(userId);
            ViewBag.HasActiveSubscription = activeSub != null;

            if (activeSub != null && post.OurDownloadLink != null && post.OurDownloadLink.Any())
            {
                ViewBag.DownloadUrls = post.OurDownloadLink.Select(b2Path =>
                {
                    var clean = b2Path.TrimStart('/');
                    var token = _tokenGen.GenerateDownloadToken(userId, clean);
                    return $"{workerBaseUrl}/download?file={Uri.EscapeDataString(clean)}&token={Uri.EscapeDataString(token)}";
                }).ToList();
            }
        }
        else
        {
            ViewBag.HasActiveSubscription = false;
        }

        ViewData["OgTitle"]    = post.Title;
        ViewData["Description"] = StripHtml(post.ContentHtml);
        ViewData["OgImage"]    = post.ThumbnailUrl;
        ViewData["OgType"]     = "article";

        var related = await relatedTask;
        foreach (var rp in related)
            rp.ThumbnailUrl = ResolveImageUrl(rp.ThumbnailUrl, workerBaseUrl);
        ViewBag.RelatedPosts = related;

        if (ShouldCountView(id))
            _ = _supabase.IncrementViewCountAsync(post.Id, "posts");

        return View(post);
    }

    private static string StripHtml(string? html, int maxLen = 155)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var text = Regex.Replace(html, "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= maxLen ? text : text[..maxLen].TrimEnd() + "…";
    }

    private static string ResolveImageUrl(string url, string workerBase)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(workerBase))
            return url;
        if (url.StartsWith("http://") || url.StartsWith("https://"))
            return url;
        return $"{workerBase}/{url.TrimStart('/')}";
    }

    private bool ShouldCountView(string postId)
    {
        var ua = Request.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(ua)) return false;
        var lower = ua.ToLowerInvariant();
        if (lower.Contains("bot") || lower.Contains("crawler") || lower.Contains("spider") ||
            lower.Contains("slurp") || lower.Contains("baidu") || lower.Contains("yandex") ||
            lower.Contains("facebot") || lower.Contains("ia_archiver")) return false;

        var cookieKey = $"pv_{postId}";
        if (Request.Cookies.ContainsKey(cookieKey)) return false;

        Response.Cookies.Append(cookieKey, "1", new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddHours(24),
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = false
        });
        return true;
    }
}
