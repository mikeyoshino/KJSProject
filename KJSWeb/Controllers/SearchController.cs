using Microsoft.AspNetCore.Mvc;
using KJSWeb.Models;
using KJSWeb.Services;

namespace KJSWeb.Controllers;

[Route("search")]
public class SearchController : Controller
{
    private readonly SupabaseService _supabase;
    private readonly IConfiguration _config;

    public SearchController(SupabaseService supabase, IConfiguration config)
    {
        _supabase = supabase;
        _config   = config;
    }

    [Route("results")]
    public async Task<IActionResult> Results(string q)
    {
        var vm = new SearchResultsViewModel { Query = q ?? "" };

        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return PartialView("_Results", vm);

        var workerBase = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
        var trimmed    = q.Trim();

        var postsTask  = _supabase.SearchPostsAsync(trimmed, limit: 8);
        var jgirlTask  = _supabase.SearchJGirlPostsAsync(trimmed, limit: 6);
        await Task.WhenAll(postsTask, jgirlTask);

        var posts = postsTask.Result;
        foreach (var p in posts)
            p.ThumbnailUrl = ResolveImageUrl(p.ThumbnailUrl, workerBase);

        var jgirlPosts = jgirlTask.Result;
        foreach (var jp in jgirlPosts)
            jp.ThumbnailUrl = ResolveImageUrl(jp.ThumbnailUrl, workerBase);

        vm.Posts      = posts;
        vm.JGirlPosts = jgirlPosts;
        return PartialView("_Results", vm);
    }

    private static string ResolveImageUrl(string url, string workerBase)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(workerBase)) return url;
        if (url.StartsWith("http://") || url.StartsWith("https://")) return url;
        return $"{workerBase}/{url.TrimStart('/')}";
    }
}
