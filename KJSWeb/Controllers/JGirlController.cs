using Microsoft.AspNetCore.Mvc;
using KJSWeb.Models;
using KJSWeb.Services;

namespace KJSWeb.Controllers;

[Route("jgirl")]
public class JGirlController : Controller
{
    private readonly SupabaseService _supabase;
    private readonly IConfiguration _config;
    private const int PageSize = 24;

    public JGirlController(SupabaseService supabase, IConfiguration config)
    {
        _supabase = supabase;
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
        return View(posts);
    }

    [Route("post/{id}")]
    public async Task<IActionResult> Details(string id)
    {
        var post = await _supabase.GetJGirlPostByIdAsync(id);
        if (post == null) return NotFound();

        RewritePost(post);
        return View(post);
    }

    private void RewritePost(JGirlPost post)
    {
        var workerBase     = _config["CloudflareWorker:WorkerBaseUrl"]?.TrimEnd('/') ?? "";
        var downloadWorker = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? workerBase;
        var b2Base         = _config["B2:PublicBaseUrl"]?.TrimEnd('/')
                             ?? "https://f005.backblazeb2.com/file/KJSProject";

        post.ThumbnailUrl  = Rewrite(post.ThumbnailUrl, workerBase, b2Base);
        post.Images        = post.Images.Select(u => Rewrite(u, workerBase, b2Base)).ToList();
        post.PostImages    = post.PostImages.Select(u => Rewrite(u, workerBase, b2Base)).ToList();
        post.DownloadLinks = post.DownloadLinks.Select(u => Rewrite(u, downloadWorker, b2Base)).ToList();
    }

    private static string Rewrite(string url, string workerBase, string b2Base)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(workerBase)) return url;
        if (url.StartsWith(b2Base))
            return workerBase + url[b2Base.Length..];
        return url;
    }
}
