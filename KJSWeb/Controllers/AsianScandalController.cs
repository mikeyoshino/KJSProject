using Microsoft.AspNetCore.Mvc;
using KJSWeb.Models;
using KJSWeb.Services;

namespace KJSWeb.Controllers;

[Route("asian-scandal")]
public class AsianScandalController : Controller
{
    private readonly SupabaseService _supabase;
    private const int PageSize = 24;

    public AsianScandalController(SupabaseService supabase)
    {
        _supabase = supabase;
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
}
