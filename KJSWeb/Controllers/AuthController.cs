using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using KJSWeb.Services;

namespace KJSWeb.Controllers;

public class AuthController : Controller
{
    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;
    private readonly SupabaseService _supabase;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IConfiguration config, SupabaseService supabase, ILogger<AuthController> logger)
    {
        _supabaseUrl = config["Supabase:Url"]!;
        _supabaseKey = config["Supabase:Key"]!;
        _supabase = supabase;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Login(string email, string password, string? returnUrl = null)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            ViewBag.Error    = "Email and password are required.";
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        using var http    = new HttpClient();
        var payload       = JsonSerializer.Serialize(new { email, password });
        var request       = new HttpRequestMessage(HttpMethod.Post,
            $"{_supabaseUrl}/auth/v1/token?grant_type=password");
        request.Headers.Add("apikey", _supabaseKey);
        request.Content   = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        var json     = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            ViewBag.Error    = "Invalid email or password.";
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        using var doc    = JsonDocument.Parse(json);
        var root         = doc.RootElement;
        var userId       = root.GetProperty("user").GetProperty("id").GetString()!;
        var userEmail    = root.GetProperty("user").GetProperty("email").GetString()!;

        await SignInUserAsync(userId, userEmail);
        return Redirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    [HttpGet]
    public IActionResult Signup()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Signup(string email, string password, string confirmPassword)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            ViewBag.Error = "Email and password are required.";
            return View();
        }

        if (password != confirmPassword)
        {
            ViewBag.Error = "Passwords do not match.";
            return View();
        }

        if (password.Length < 6)
        {
            ViewBag.Error = "Password must be at least 6 characters.";
            return View();
        }

        using var http  = new HttpClient();
        var payload     = JsonSerializer.Serialize(new { email, password });
        var request     = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/auth/v1/signup");
        request.Headers.Add("apikey", _supabaseKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        var json     = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                using var errDoc = JsonDocument.Parse(json);
                ViewBag.Error = errDoc.RootElement.GetProperty("msg").GetString()
                                ?? "Signup failed. Please try again.";
            }
            catch { ViewBag.Error = "Signup failed. Please try again."; }
            return View();
        }

        using var doc = JsonDocument.Parse(json);
        var root      = doc.RootElement;

        if (root.TryGetProperty("access_token", out _))
        {
            var userId    = root.GetProperty("user").GetProperty("id").GetString()!;
            var userEmail = root.GetProperty("user").GetProperty("email").GetString()!;
            await SignInUserAsync(userId, userEmail);

            try
            {
                await _supabase.CreateTrialSubscriptionAsync(userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create trial subscription for user {UserId}", userId);
            }

            return RedirectToAction("Index", "Home");
        }

        // Email confirmation required
        ViewBag.Success = "Account created! Check your email to confirm, then log in.";
        return View("Login");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Index", "Home");
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task SignInUserAsync(string userId, string userEmail)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Email, userEmail),
        };
        var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true });
    }
}
