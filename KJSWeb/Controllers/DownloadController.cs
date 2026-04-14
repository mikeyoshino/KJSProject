using Microsoft.AspNetCore.Mvc;
using KJSWeb.Services;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace KJSWeb.Controllers;

[Route("download")]
public class DownloadController : Controller
{
    private readonly SupabaseService _supabase;
    private readonly TokenGenService _tokenGen;
    private readonly ExeIoService _exeIo;
    private readonly IConfiguration _config;

    public DownloadController(SupabaseService supabase, TokenGenService tokenGen, ExeIoService exeIo, IConfiguration config)
    {
        _supabase = supabase;
        _tokenGen = tokenGen;
        _exeIo = exeIo;
        _config = config;
    }

    /// <summary>
    /// Free download entry point.
    /// 1. Gets the b2Path for the requested file.
    /// 2. Generates a 30-min JWT and builds the /download/start landing URL (on this domain).
    /// 3. Registers *that* landing URL with exe.io to get an ad-gated short link.
    /// 4. Redirects the user to https://exe.io/xxxxx — they see the ad page,
    ///    then exe.io sends them to /download/start on scandal69.com.
    /// 5. /download/start renders a "Download starting…" page that JS-triggers the
    ///    CF Worker, so Chrome sees referrer = scandal69.com (not exe.io). ✓
    /// Falls back to the CF Worker URL directly if exe.io is unavailable.
    /// </summary>
    [Route("public")]
    [HttpGet]
    public async Task<IActionResult> Public(string postId, string table, int part = 0)
    {
        if (string.IsNullOrWhiteSpace(postId) || string.IsNullOrWhiteSpace(table))
            return BadRequest("Missing postId or table.");

        var b2Base = _config["B2:PublicBaseUrl"]?.TrimEnd('/') ?? "";
        string? b2Path;

        if (table == "jgirl_posts")
        {
            var post = await _supabase.GetJGirlPostByIdAsync(postId);
            if (post == null || part >= post.DownloadLinks.Count) return NotFound();
            var raw = post.DownloadLinks[part];
            b2Path = raw.StartsWith(b2Base) ? raw[b2Base.Length..].TrimStart('/') : raw.TrimStart('/');
        }
        else
        {
            var post = await _supabase.GetPostByIdAsync(postId);
            if (post == null || post.OurDownloadLink == null || part >= post.OurDownloadLink.Count)
                return NotFound();
            b2Path = post.OurDownloadLink[part].TrimStart('/');
        }

        if (string.IsNullOrEmpty(b2Path)) return NotFound();

        // Generate a short-lived JWT for the file
        var token = _tokenGen.GeneratePublicDownloadToken(b2Path);

        // Build the intermediate landing page URL on *our* domain.
        // exe.io will redirect users here — Chrome will then see scandal69.com as the referrer.
        var siteBase = $"{Request.Scheme}://{Request.Host}";
        var startUrl = $"{siteBase}/download/start?file={Uri.EscapeDataString(b2Path)}&token={Uri.EscapeDataString(token)}";

        // Wrap the landing URL with exe.io ad gate
        var exeIoUrl = await _exeIo.GenerateLinkAsync(startUrl);

        // Redirect to exe.io, or fall back to landing page directly
        return Redirect(exeIoUrl ?? startUrl);
    }

    /// <summary>
    /// Intermediate download landing page — exe.io destination.
    /// Validates the JWT, then renders a "Download starting…" page.
    /// JavaScript on that page fires the actual CF Worker request so
    /// Chrome records referrer = scandal69.com, not exe.io.
    /// </summary>
    [Route("start")]
    [HttpGet]
    public IActionResult Start(string file, string token)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(token))
            return BadRequest("Invalid download link.");

        // Validate JWT — return 401 if expired / tampered
        var jwtSecret = _config["Jwt:Secret"] ?? _config["JWT_SECRET"] ?? "";
        try
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
            var handler = new JwtSecurityTokenHandler();
            handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = false,
                ValidateAudience = false,
                ClockSkew = TimeSpan.FromMinutes(5),
            }, out var validated);

            var jwt = (JwtSecurityToken)validated;
            var fileClaim = jwt.Claims.FirstOrDefault(c => c.Type == "file")?.Value;
            if (fileClaim != file)
                return Unauthorized("Token does not match the requested file.");
        }
        catch
        {
            return Unauthorized("Download link has expired or is invalid. Please go back and try again.");
        }

        var workerBase = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
        var workerUrl  = $"{workerBase}/public-download?file={Uri.EscapeDataString(file)}&token={Uri.EscapeDataString(token)}";
        var filename   = Path.GetFileName(file);

        ViewData["Title"]     = $"Downloading {filename}";
        ViewData["WorkerUrl"] = workerUrl;
        ViewData["Filename"]  = filename;

        return View();
    }
}
