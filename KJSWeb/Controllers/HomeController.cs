using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using KJSWeb.Models;
using KJSWeb.Services;

namespace KJSWeb.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly SupabaseService _supabase;
    private const int PageSize = 24;

    public HomeController(ILogger<HomeController> logger, SupabaseService supabase)
    {
        _logger = logger;
        _supabase = supabase;
    }

    public async Task<IActionResult> Index(int page = 1)
    {
        if (page < 1) page = 1;

        var (posts, totalCount) = await _supabase.GetLatestPostsAsync(page, PageSize);
        
        var viewModel = new PaginationInfo
        {
            CurrentPage = page,
            PageSize = PageSize,
            TotalItems = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)PageSize)
        };

        ViewBag.Pagination = viewModel;
        return View(posts);
    }

    public async Task<IActionResult> Category(string name, int page = 1)
    {
        if (page < 1) page = 1;
        if (string.IsNullOrEmpty(name)) return RedirectToAction("Index");

        var (posts, totalCount) = await _supabase.GetPostsByCategoryAsync(name, page, PageSize);
        
        var viewModel = new PaginationInfo
        {
            CurrentPage = page,
            PageSize = PageSize,
            TotalItems = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)PageSize)
        };

        ViewBag.Pagination = viewModel;
        ViewBag.CategoryName = name;
        return View("Index", posts);
    }

    [Route("post/{id}")]
    public async Task<IActionResult> Details(string id)
    {
        var post = await _supabase.GetPostByIdAsync(id);
        if (post == null) return NotFound();
        
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
