using System.Text;
using KJSWeb.Services;
using KJSWeb.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace KJSWeb.Controllers;

public class SitemapController : Controller
{
    private readonly SupabaseService _supabase;
    private readonly IMemoryCache _cache;

    public SitemapController(SupabaseService supabase, IMemoryCache cache)
    {
        _supabase = supabase;
        _cache = cache;
    }

    [Route("/sitemap.xml")]
    public async Task<IActionResult> Index()
    {
        // Cache the sitemap for 1 hour to prevent DB spam from crawlers
        if (!_cache.TryGetValue("SitemapXml", out string? xmlContent))
        {
            string host = Request.Scheme + "://" + Request.Host;
            var sb = new StringBuilder();
            
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

            // Core entry points
            sb.AppendLine($"<url><loc>{host}/</loc><priority>1.0</priority><changefreq>daily</changefreq></url>");
            sb.AppendLine($"<url><loc>{host}/buzz69</loc><priority>0.9</priority><changefreq>daily</changefreq></url>");
            sb.AppendLine($"<url><loc>{host}/asian-scandal</loc><priority>0.9</priority><changefreq>daily</changefreq></url>");
            sb.AppendLine($"<url><loc>{host}/jgirl</loc><priority>0.9</priority><changefreq>daily</changefreq></url>");

            try 
            {
                // Fetch up to 50 pages (1200 posts) for the sitemap
                // If there are more than 1200 posts, a sitemap index and pagination should be considered later.
                for (int i = 1; i <= 50; i++)
                {
                    var (posts, _) = await _supabase.GetLatestPostsAsync(i, 50);
                    if (posts.Count == 0) break;

                    foreach (var p in posts)
                    {
                        string controller = p.SourceNameRaw == "AsianScandal" ? "AsianScandal" : "Home";
                        string url = $"{host}/{controller}/Details/{p.Id}/{SeoHelper.ToSlug(p.Title)}";
                        // Clean XML ampersands from slugs if they happen to appear
                        url = System.Security.SecurityElement.Escape(url);
                        
                        sb.AppendLine($"<url><loc>{url}</loc><lastmod>{p.CreatedAt:yyyy-MM-dd}</lastmod><priority>0.8</priority></url>");
                    }
                }

                for (int i = 1; i <= 50; i++)
                {
                    var (jposts, _) = await _supabase.GetJGirlPostsAsync(i, 50);
                    if (jposts.Count == 0) break;

                    foreach (var p in jposts)
                    {
                        string url = $"{host}/JGirl/Details/{p.Id}/{SeoHelper.ToSlug(p.Title)}";
                        url = System.Security.SecurityElement.Escape(url);
                        
                        sb.AppendLine($"<url><loc>{url}</loc><lastmod>{p.CreatedAt:yyyy-MM-dd}</lastmod><priority>0.8</priority></url>");
                    }
                }
            }
            catch (Exception)
            {
                // Fail gracefully so the homepage still loads in the sitemap instead of completely crashing
            }

            sb.AppendLine("</urlset>");
            xmlContent = sb.ToString();

            // Store in cache
            var cacheEntryOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromHours(1));
            _cache.Set("SitemapXml", xmlContent, cacheEntryOptions);
        }

        return Content(xmlContent!, "text/xml", Encoding.UTF8);
    }
    
    [Route("/robots.txt")]
    public IActionResult Robots()
    {
        string host = Request.Scheme + "://" + Request.Host;
        var sb = new StringBuilder();
        sb.AppendLine("User-agent: *");
        sb.AppendLine("Allow: /");
        sb.AppendLine();
        sb.AppendLine($"Sitemap: {host}/sitemap.xml");

        return Content(sb.ToString(), "text/plain", Encoding.UTF8);
    }
}
