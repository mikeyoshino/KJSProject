using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace KJSWeb.Controllers;

public class AuthController : Controller
{
    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;

    public AuthController(IConfiguration config)
    {
        _supabaseUrl = config["Supabase:Url"]!;
        _supabaseKey = config["Supabase:Key"]!;
    }

    [HttpGet]
    public IActionResult Login()
    {
        if (HttpContext.Session.GetString("user_id") != null)
            return RedirectToAction("Index", "Home");
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Login(string email, string password)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            ViewBag.Error = "Email and password are required.";
            return View();
        }

        using var http = new HttpClient();
        var payload = JsonSerializer.Serialize(new { email, password });
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_supabaseUrl}/auth/v1/token?grant_type=password");
        request.Headers.Add("apikey", _supabaseKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            ViewBag.Error = "Invalid email or password.";
            return View();
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()!;
        var userId = root.GetProperty("user").GetProperty("id").GetString()!;
        var userEmail = root.GetProperty("user").GetProperty("email").GetString()!;

        // Store in session
        HttpContext.Session.SetString("access_token", accessToken);
        HttpContext.Session.SetString("user_id", userId);
        HttpContext.Session.SetString("user_email", userEmail);

        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    public IActionResult Signup()
    {
        if (HttpContext.Session.GetString("user_id") != null)
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

        using var http = new HttpClient();
        var payload = JsonSerializer.Serialize(new { email, password });
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_supabaseUrl}/auth/v1/signup");
        request.Headers.Add("apikey", _supabaseKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // Try to parse error message
            try
            {
                using var doc = JsonDocument.Parse(json);
                var msg = doc.RootElement.GetProperty("msg").GetString();
                ViewBag.Error = msg ?? "Signup failed. Please try again.";
            }
            catch
            {
                ViewBag.Error = "Signup failed. Please try again.";
            }
            return View();
        }

        // Auto-login after signup
        using var doc2 = JsonDocument.Parse(json);
        var root = doc2.RootElement;

        if (root.TryGetProperty("access_token", out var tokenProp))
        {
            HttpContext.Session.SetString("access_token", tokenProp.GetString()!);
            HttpContext.Session.SetString("user_id", root.GetProperty("user").GetProperty("id").GetString()!);
            HttpContext.Session.SetString("user_email", root.GetProperty("user").GetProperty("email").GetString()!);
            return RedirectToAction("Index", "Home");
        }

        // If email confirmation is required
        ViewBag.Success = "Account created! Check your email to confirm, then log in.";
        return View("Login");
    }

    [HttpPost]
    public IActionResult Logout()
    {
        HttpContext.Session.Clear();
        return RedirectToAction("Index", "Home");
    }
}
