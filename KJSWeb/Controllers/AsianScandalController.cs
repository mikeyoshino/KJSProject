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
    private const int PageSize = 24;

    public AsianScandalController(SupabaseService supabase, TokenGenService tokenGen, IConfiguration config)
    {
        _supabase = supabase;
        _tokenGen = tokenGen;
        _config = config;
    }

    public async Task<IActionResult> Index(int page = 1)
    {
        if (page < 1) page = 1;

        var (posts, totalCount) = await _supabase.GetLatestAsianScandalPostsAsync(page, PageSize);
        
        var viewModel = new PaginationInfo
        {
            CurrentPage = page,
            PageSize = PageSize,
            TotalItems = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)PageSize)
        };

        ViewBag.Pagination = viewModel;
        ViewBag.Title = "Asian Scandal Updates";
        return View(posts);
    }

    [Route("post/{id}")]
    public async Task<IActionResult> Details(string id)
    {
        var post = await _supabase.GetAsianScandalPostByIdAsync(id);
        if (post == null) return NotFound();
        
        // Load some recent standard posts for the sidebar
        var (recentPosts, _) = await _supabase.GetLatestPostsAsync(1, 5);
        ViewBag.RecentPosts = recentPosts;

        // Check subscription status for download gating
        var userId = HttpContext.Session.GetString("user_id");
        if (!string.IsNullOrEmpty(userId))
        {
            var activeSub = await _supabase.GetActiveSubscriptionAsync(userId);
            ViewBag.HasActiveSubscription = activeSub != null;
        }
        else
        {
            ViewBag.HasActiveSubscription = false;
        }
        
        return View(post);
    }

    [Route("download")]
    public async Task<IActionResult> Download(string b2Path)
    {
        if (string.IsNullOrEmpty(b2Path)) return BadRequest();

        var userId = HttpContext.Session.GetString("user_id");
        if (string.IsNullOrEmpty(userId))
        {
            return RedirectToAction("Login", "Auth", new { returnUrl = $"/asian-scandal" });
        }

        var activeSub = await _supabase.GetActiveSubscriptionAsync(userId);
        if (activeSub == null)
        {
            return RedirectToAction("Pricing", "Subscription");
        }

        // Clean the path
        if (b2Path.StartsWith("/")) b2Path = b2Path.Substring(1);

        // Generate JWT
        var token = _tokenGen.GenerateDownloadToken(userId, b2Path);

        var workerBaseUrl = _config["CloudflareWorker:WorkerBaseUrl"] ?? "https://dl.yourdomain.com";

        // Redirect to Cloudflare Worker
        var downloadUrl = $"{workerBaseUrl}/download?file={Uri.EscapeDataString(b2Path)}&token={Uri.EscapeDataString(token)}";
        return Redirect(downloadUrl);
    }
}
