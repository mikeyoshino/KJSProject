using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using KJSWeb.Models;
using KJSWeb.Services;
using static KJSWeb.Models.PostSource;

namespace KJSWeb.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly SupabaseService _supabase;
    private readonly TokenGenService _tokenGen;
    private readonly IConfiguration _config;
    private const int PageSize = 24;

    public HomeController(ILogger<HomeController> logger, SupabaseService supabase, TokenGenService tokenGen, IConfiguration config)
    {
        _logger = logger;
        _supabase = supabase;
        _tokenGen = tokenGen;
        _config = config;
    }

    public async Task<IActionResult> Index()
    {
        var workerBase = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
        var b2Base     = _config["B2:PublicBaseUrl"]?.TrimEnd('/') ?? "https://f005.backblazeb2.com/file/KJSProject";

        var (buzz69Posts, _)   = await _supabase.GetLatestPostsAsync(1, 8, PostSource.Buzz69);
        var (asianPosts, _)    = await _supabase.GetLatestPostsAsync(1, 8, PostSource.AsianScandal);
        var (upskirtPosts, _)  = await _supabase.GetJGirlPostsAsync(1, 8, "upskirt");
        var (bathroomPosts, _) = await _supabase.GetJGirlPostsAsync(1, 8, "bathroom");
        var (fc2Posts, _)      = await _supabase.GetJGirlPostsAsync(1, 8, "fc2");

        foreach (var p in buzz69Posts) p.ThumbnailUrl = ResolveImageUrl(p.ThumbnailUrl, workerBase);
        foreach (var p in asianPosts)  p.ThumbnailUrl = ResolveImageUrl(p.ThumbnailUrl, workerBase);
        foreach (var p in upskirtPosts)
        {
            p.ThumbnailUrl = RewriteB2(p.ThumbnailUrl, workerBase, b2Base);
            p.Images       = p.Images.Select(u => RewriteB2(u, workerBase, b2Base)).ToList();
        }
        foreach (var p in bathroomPosts)
        {
            p.ThumbnailUrl = RewriteB2(p.ThumbnailUrl, workerBase, b2Base);
            p.Images       = p.Images.Select(u => RewriteB2(u, workerBase, b2Base)).ToList();
        }
        foreach (var p in fc2Posts)
        {
            p.ThumbnailUrl = RewriteB2(p.ThumbnailUrl, workerBase, b2Base);
            p.Images       = p.Images.Select(u => RewriteB2(u, workerBase, b2Base)).ToList();
        }

        ViewBag.Buzz69Posts   = buzz69Posts;
        ViewBag.AsianPosts    = asianPosts;
        ViewBag.UpskirtPosts  = upskirtPosts;
        ViewBag.BathroomPosts = bathroomPosts;
        ViewBag.Fc2Posts      = fc2Posts;

        ViewData["OgTitle"]    = "SCANDAL69 — Premium Content";
        ViewData["Description"] = "Premium content delivery network specializing in high-quality updates and community curation.";
        ViewData["OgType"]     = "website";
        return View();
    }

    private static string RewriteB2(string url, string workerBase, string b2Base)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(workerBase)) return url;
        if (url.StartsWith(b2Base)) return workerBase + url[b2Base.Length..];
        return url;
    }

    [Route("buzz69")]
    public async Task<IActionResult> Buzz69(int page = 1, string period = "week")
    {
        if (page < 1) page = 1;

        var workerBase = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";

        var (posts, totalCount) = await _supabase.GetLatestPostsAsync(page, PageSize, PostSource.Buzz69);
        foreach (var p in posts)
            p.ThumbnailUrl = ResolveImageUrl(p.ThumbnailUrl, workerBase);

        var categories   = await _supabase.GetCategoriesAsync();
        var popularPosts = await _supabase.GetPopularPostsAsync(6, PostSource.Buzz69, period);
        foreach (var p in popularPosts)
            p.ThumbnailUrl = ResolveImageUrl(p.ThumbnailUrl, workerBase);

        ViewBag.Pagination = new PaginationInfo
        {
            CurrentPage = page,
            PageSize = PageSize,
            TotalItems = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)PageSize)
        };
        ViewBag.SourceTitle       = "Buzz69";
        ViewBag.SourcePrefix      = "buzz69";
        ViewBag.SidebarCategories = categories;
        ViewBag.PopularPosts      = popularPosts;
        ViewBag.PopularPeriod     = period;
        ViewBag.ShowSidebar       = true;

        ViewData["OgTitle"]    = "Buzz69 — SCANDAL69";
        ViewData["Description"] = "Browse the latest Buzz69 content on SCANDAL69.";
        ViewData["OgType"]     = "website";
        return View("Listing", posts);
    }

    [Route("buzz69/popular")]
    public async Task<IActionResult> Buzz69Popular(string period = "week")
    {
        var workerBase   = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
        var popularPosts = await _supabase.GetPopularPostsAsync(6, PostSource.Buzz69, period);
        foreach (var p in popularPosts)
            p.ThumbnailUrl = ResolveImageUrl(p.ThumbnailUrl, workerBase);

        ViewBag.PopularPeriod = period;
        ViewBag.SourcePrefix  = "buzz69";
        return PartialView("_PopularPosts", popularPosts);
    }

    [Route("category/{name}")]
    public async Task<IActionResult> Category(string name, int page = 1)
    {
        if (page < 1) page = 1;
        if (string.IsNullOrEmpty(name)) return RedirectToAction("Index");

        var (posts, totalCount) = await _supabase.GetPostsByCategoryAsync(name, page, PageSize);

        var workerBase = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
        foreach (var p in posts)
            p.ThumbnailUrl = ResolveImageUrl(p.ThumbnailUrl, workerBase);

        ViewBag.Pagination = new PaginationInfo
        {
            CurrentPage = page,
            PageSize = PageSize,
            TotalItems = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)PageSize)
        };
        ViewBag.CategoryName = name;

        ViewData["OgTitle"]    = $"{name} — SCANDAL69";
        ViewData["Description"] = $"Browse {name} posts on SCANDAL69.";
        ViewData["OgType"]     = "website";
        return View("Listing", posts);
    }

    [Route("post/{id}")]
    public async Task<IActionResult> Details(string id)
    {
        var post = await _supabase.GetPostByIdAsync(id);
        if (post == null) return NotFound();

        var (recentPosts, _) = await _supabase.GetLatestPostsAsync(1, 5);
        ViewBag.RecentPosts = recentPosts;

        var workerBaseUrl = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
        ViewBag.WorkerBaseUrl = workerBaseUrl;
        ViewBag.B2DirectBase = _config["B2:PublicBaseUrl"]?.TrimEnd('/') ?? "https://f005.backblazeb2.com/file/KJSProject";
        post.ThumbnailUrl = ResolveImageUrl(post.ThumbnailUrl, workerBaseUrl);

        var userId = HttpContext.Session.GetString("user_id");
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

        // Always show free download buttons — exe.io link generated on first click
        if (post.OurDownloadLink != null && post.OurDownloadLink.Any())
        {
            var siteBase = $"{Request.Scheme}://{Request.Host.Value}";
            ViewBag.PublicDownloadUrls = post.OurDownloadLink
                .Select((_, i) => $"{siteBase}/download/public?postId={post.Id}&table=posts&part={i}")
                .ToList();
        }

        ViewData["OgTitle"]    = post.Title;
        ViewData["Description"] = StripHtml(post.ContentHtml);
        ViewData["OgImage"]    = post.ThumbnailUrl;
        ViewData["OgType"]     = "article";

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

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
