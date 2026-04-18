# Persistent Cookie Authentication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace in-memory ASP.NET Core sessions with encrypted persistent authentication cookies so users stay logged in across server restarts and deployments.

**Architecture:** Remove `AddDistributedMemoryCache` + `AddSession` and replace with ASP.NET Core Cookie Authentication (`AddAuthentication().AddCookie()`). User identity (`user_id`, `user_email`) is stored as encrypted claims inside a 30-day sliding-expiration cookie. Data Protection keys are persisted to a configurable directory on disk so the server can decrypt existing cookies after a restart. All `HttpContext.Session.GetString("user_id")` calls across 9 files are replaced with `User.FindFirstValue(ClaimTypes.NameIdentifier)`.

**Tech Stack:** ASP.NET Core Cookie Authentication, `Microsoft.AspNetCore.Authentication.Cookies` (built-in), `Microsoft.AspNetCore.DataProtection` (built-in), .NET 10

---

## File Map

| File | Action | Change |
|------|--------|--------|
| `KJSWeb/Program.cs` | Modify | Remove session; add `AddAuthentication().AddCookie()` + `AddDataProtection()` + `UseAuthentication()` |
| `KJSWeb/Controllers/AuthController.cs` | Modify | `SignInAsync` / `SignOutAsync` instead of Session.Set/Clear; `User.Identity.IsAuthenticated` instead of Session.GetString checks |
| `KJSWeb/Middleware/BanCheckMiddleware.cs` | Modify | Read `context.User` claims; call `SignOutAsync` instead of `Session.Clear()` |
| `KJSWeb/Filters/AdminAuthFilter.cs` | Modify | Read `context.HttpContext.User` claims instead of Session |
| `KJSWeb/Controllers/HomeController.cs` | Modify | `User.FindFirstValue` instead of `Session.GetString("user_id")` |
| `KJSWeb/Controllers/AsianScandalController.cs` | Modify | `User.FindFirstValue` instead of `Session.GetString("user_id")` |
| `KJSWeb/Controllers/JGirlController.cs` | Modify | `User.FindFirstValue` instead of `Session.GetString("user_id")` |
| `KJSWeb/Controllers/SubscriptionController.cs` | Modify | `User.FindFirstValue` instead of all `Session.GetString` calls |
| `KJSWeb/Controllers/SupportController.cs` | Modify | `User.FindFirstValue` instead of `Session.GetString` in `GetSession()` helper |
| `KJSWeb/Controllers/CrmController.cs` | Modify | `User.FindFirstValue` instead of `Session.GetString` |

**New claim types used throughout:**
- `ClaimTypes.NameIdentifier` → replaces session key `"user_id"`
- `ClaimTypes.Email` → replaces session key `"user_email"`
- `"access_token"` session key was written but never read — **not stored** in the new cookie

---

### Task 1: Configure Cookie Authentication in Program.cs

**Files:**
- Modify: `KJSWeb/Program.cs`

- [ ] **Step 1: Replace the session block and add cookie auth + data protection**

Open `KJSWeb/Program.cs`. Replace the entire file with:

```csharp
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<KJSWeb.Services.SupabaseService>();
builder.Services.AddSingleton<KJSWeb.Services.BlockonomicsService>();
builder.Services.AddSingleton<KJSWeb.Services.TokenGenService>();
builder.Services.AddScoped<KJSWeb.Services.AdminService>();
builder.Services.AddScoped<KJSWeb.Services.ExeIoService>();
builder.Services.AddScoped<KJSWeb.Filters.AdminAuthFilter>();
builder.Services.AddSingleton<KJSWeb.Services.EmailService>();

// Persist Data Protection keys to disk so encrypted auth cookies survive restarts/redeploys.
// Set DataProtection:KeysPath in appsettings.json (e.g. "/app/keys") or leave blank to use
// a "keys" folder next to the executable.
var keysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "keys");
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("SCANDAL69");

// Cookie authentication — replaces AddDistributedMemoryCache + AddSession.
// Claims stored inside an encrypted, signed cookie on the user's browser.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name       = "SCANDAL69_Auth";
        options.Cookie.HttpOnly   = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // Secure in prod (HTTPS), relaxed in dev
        options.Cookie.SameSite   = SameSiteMode.Lax;
        options.ExpireTimeSpan    = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;   // renews cookie on every request while active
        options.LoginPath         = "/auth/login";
        options.AccessDeniedPath  = "/auth/login";
    });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseRouting();

app.UseAuthentication(); // MUST come before BanCheckMiddleware and UseAuthorization
app.UseMiddleware<KJSWeb.Middleware.BanCheckMiddleware>();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}/{slug?}")
    .WithStaticAssets();

app.Run();
```

- [ ] **Step 2: Build and verify no compile errors**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject && dotnet build KJSProject.sln
```
Expected: `Build succeeded. 0 Error(s)` (there may be warnings from other files that still reference Session — that's OK, they'll be fixed in subsequent tasks)

- [ ] **Step 3: Commit**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
git add KJSWeb/Program.cs
git commit -m "feat: replace in-memory session with persistent cookie authentication"
```

---

### Task 2: Update AuthController to use SignInAsync / SignOutAsync

**Files:**
- Modify: `KJSWeb/Controllers/AuthController.cs`

- [ ] **Step 1: Rewrite AuthController.cs**

Replace the entire file content with:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
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
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(returnUrl ?? "/");
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
        return Redirect(!string.IsNullOrEmpty(returnUrl) ? returnUrl : "/");
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
            return RedirectToAction("Index", "Home");
        }

        // Email confirmation required
        ViewBag.Success = "Account created! Check your email to confirm, then log in.";
        return View("Login");
    }

    [HttpPost]
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
```

- [ ] **Step 2: Build**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject && dotnet build KJSProject.sln
```
Expected: `Build succeeded. 0 Error(s)` (warnings from files still using Session are OK)

- [ ] **Step 3: Commit**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
git add KJSWeb/Controllers/AuthController.cs
git commit -m "feat: use SignInAsync/SignOutAsync with cookie claims in AuthController"
```

---

### Task 3: Update BanCheckMiddleware and AdminAuthFilter

**Files:**
- Modify: `KJSWeb/Middleware/BanCheckMiddleware.cs`
- Modify: `KJSWeb/Filters/AdminAuthFilter.cs`

- [ ] **Step 1: Rewrite BanCheckMiddleware.cs**

Replace `KJSWeb/Middleware/BanCheckMiddleware.cs` with:

```csharp
using KJSWeb.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

namespace KJSWeb.Middleware;

public class BanCheckMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IServiceProvider _services;

    private static HashSet<string> _bannedIds  = new();
    private static DateTime _lastRefresh       = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl  = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim _refreshLock = new(1, 1);

    public BanCheckMiddleware(RequestDelegate next, IServiceProvider services)
    {
        _next     = next;
        _services = services;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!string.IsNullOrEmpty(userId))
        {
            await EnsureCacheAsync();

            if (_bannedIds.Contains(userId))
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                context.Response.Redirect("/auth/login?banned=1");
                return;
            }
        }

        await _next(context);
    }

    private async Task EnsureCacheAsync()
    {
        if (DateTime.UtcNow - _lastRefresh < CacheTtl)
            return;

        if (!await _refreshLock.WaitAsync(0))
            return; // another thread is refreshing — use stale cache

        try
        {
            using var scope   = _services.CreateScope();
            var supabase      = scope.ServiceProvider.GetRequiredService<SupabaseService>();
            var ids           = await supabase.GetBannedUserIdsAsync();
            _bannedIds        = new HashSet<string>(ids, StringComparer.Ordinal);
            _lastRefresh      = DateTime.UtcNow;
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
```

- [ ] **Step 2: Rewrite AdminAuthFilter.cs**

Replace `KJSWeb/Filters/AdminAuthFilter.cs` with:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Security.Claims;

namespace KJSWeb.Filters;

public class AdminAuthFilter : IActionFilter
{
    private readonly IConfiguration _config;

    public AdminAuthFilter(IConfiguration config)
    {
        _config = config;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var user   = context.HttpContext.User;
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            context.Result = new RedirectResult("/auth/login?returnUrl=/crm");
            return;
        }

        var userEmail   = user.FindFirstValue(ClaimTypes.Email) ?? "";
        var adminEmails = _config.GetSection("Admin:Emails").Get<string[]>() ?? Array.Empty<string>();

        if (!adminEmails.Contains(userEmail, StringComparer.OrdinalIgnoreCase))
        {
            context.Result = new StatusCodeResult(403);
            return;
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
```

- [ ] **Step 3: Build**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject && dotnet build KJSProject.sln
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
git add KJSWeb/Middleware/BanCheckMiddleware.cs KJSWeb/Filters/AdminAuthFilter.cs
git commit -m "feat: replace session reads with User claims in BanCheckMiddleware and AdminAuthFilter"
```

---

### Task 4: Update Content Controllers (Home, AsianScandal, JGirl)

**Files:**
- Modify: `KJSWeb/Controllers/HomeController.cs` — line ~164 (`Details` action)
- Modify: `KJSWeb/Controllers/AsianScandalController.cs` — line ~88 (`Details` action)
- Modify: `KJSWeb/Controllers/JGirlController.cs` — line ~76 (`Details` action)

All three controllers have identical session read pattern in their `Details` action:
```csharp
var userId = HttpContext.Session.GetString("user_id");
```

Replace every occurrence with:
```csharp
var userId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
```

- [ ] **Step 1: Update HomeController.cs**

In `KJSWeb/Controllers/HomeController.cs`, find (in the `Details` action):
```csharp
        var userId = HttpContext.Session.GetString("user_id");
```
Replace with:
```csharp
        var userId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
```

- [ ] **Step 2: Update AsianScandalController.cs**

In `KJSWeb/Controllers/AsianScandalController.cs`, find (in the `Details` action):
```csharp
        var userId = HttpContext.Session.GetString("user_id");
```
Replace with:
```csharp
        var userId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
```

- [ ] **Step 3: Update JGirlController.cs**

In `KJSWeb/Controllers/JGirlController.cs`, find (in the `Details` action):
```csharp
        var userId = HttpContext.Session.GetString("user_id");
```
Replace with:
```csharp
        var userId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
```

- [ ] **Step 4: Build**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject && dotnet build KJSProject.sln
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
git add KJSWeb/Controllers/HomeController.cs \
        KJSWeb/Controllers/AsianScandalController.cs \
        KJSWeb/Controllers/JGirlController.cs
git commit -m "feat: replace session reads with User claims in content controllers"
```

---

### Task 5: Update SubscriptionController

**Files:**
- Modify: `KJSWeb/Controllers/SubscriptionController.cs`

The controller has 4 calls to `HttpContext.Session.GetString("user_id")`. Replace every one.

- [ ] **Step 1: Replace all Session reads in SubscriptionController.cs**

In `KJSWeb/Controllers/SubscriptionController.cs`, replace every occurrence of:
```csharp
HttpContext.Session.GetString("user_id")
```
with:
```csharp
User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
```

There are 4 occurrences — in `Pricing`, `Subscribe`, `Payment`, and `MySubscription` actions. Replace all 4.

- [ ] **Step 2: Build**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject && dotnet build KJSProject.sln
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
git add KJSWeb/Controllers/SubscriptionController.cs
git commit -m "feat: replace session reads with User claims in SubscriptionController"
```

---

### Task 6: Update SupportController and CrmController

**Files:**
- Modify: `KJSWeb/Controllers/SupportController.cs`
- Modify: `KJSWeb/Controllers/CrmController.cs`

- [ ] **Step 1: Update SupportController.cs**

In `KJSWeb/Controllers/SupportController.cs`, find the `GetSession()` helper method:
```csharp
private (string? UserId, string? UserEmail) GetSession()
{
    var userId    = HttpContext.Session.GetString("user_id");
    var userEmail = HttpContext.Session.GetString("user_email");
    return (userId, userEmail);
}
```

Replace with:
```csharp
private (string? UserId, string? UserEmail) GetSession()
{
    var userId    = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
    var userEmail = User.FindFirstValue(System.Security.Claims.ClaimTypes.Email);
    return (userId, userEmail);
}
```

- [ ] **Step 2: Update CrmController.cs**

In `KJSWeb/Controllers/CrmController.cs`, find and replace the following occurrences:

**Occurrence 1** — in `BanUser` action:
```csharp
        var adminEmail = HttpContext.Session.GetString("user_email") ?? "";
```
Replace with:
```csharp
        var adminEmail = User.FindFirstValue(System.Security.Claims.ClaimTypes.Email) ?? "";
```

**Occurrence 2** — in `ReplyTicket` action:
```csharp
        var adminId    = HttpContext.Session.GetString("user_id")    ?? "admin";
        var adminEmail = HttpContext.Session.GetString("user_email") ?? "Support Team";
```
Replace with:
```csharp
        var adminId    = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier) ?? "admin";
        var adminEmail = User.FindFirstValue(System.Security.Claims.ClaimTypes.Email) ?? "Support Team";
```

- [ ] **Step 3: Build**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject && dotnet build KJSProject.sln
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Verify no remaining Session references**

```bash
grep -rn "HttpContext\.Session\|Session\.GetString\|Session\.SetString\|Session\.Clear" \
  /Users/mikeyoshino/gitRepos/KJSProject/KJSWeb --include="*.cs"
```
Expected: **no output** (zero matches). If any appear, fix them before committing.

- [ ] **Step 5: Commit**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
git add KJSWeb/Controllers/SupportController.cs KJSWeb/Controllers/CrmController.cs
git commit -m "feat: replace session reads with User claims in SupportController and CrmController"
```

---

### Task 7: Add DataProtection:KeysPath to configuration and push

**Files:**
- Modify: `KJSWeb/appsettings.json`

- [ ] **Step 1: Add the DataProtection key path to appsettings.json**

Open `KJSWeb/appsettings.json`. Add a `DataProtection` section so the path is configurable per environment without code changes:

```json
{
  "DataProtection": {
    "KeysPath": ""
  }
}
```

Add it as a top-level key alongside the existing sections. Leave `KeysPath` empty — when empty, `Program.cs` falls back to `{ContentRootPath}/keys` (a `keys` folder next to the executable).

> **On the production server:** Set `DataProtection:KeysPath` to a directory on a **persistent volume** (e.g. `/app/keys` in Docker, or any directory that survives redeploys). This directory must be readable and writable by the app process. The keys inside are sensitive — ensure the directory is not publicly accessible.

- [ ] **Step 2: Verify the keys directory is git-ignored**

```bash
grep -n "keys" /Users/mikeyoshino/gitRepos/KJSProject/.gitignore
```

If `keys/` is not listed, add it:
```bash
echo "KJSWeb/keys/" >> /Users/mikeyoshino/gitRepos/KJSProject/.gitignore
```

- [ ] **Step 3: Final build**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject && dotnet build KJSProject.sln
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit and push**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
git add KJSWeb/appsettings.json .gitignore
git commit -m "chore: add DataProtection:KeysPath config and gitignore keys directory"
git push origin master
```

---

## Verification

After deploying, verify:

1. **Login persists after restart** — log in, restart the server (`dotnet run --project KJSWeb`), navigate to a protected page — still logged in.

2. **30-day cookie** — open browser DevTools → Application → Cookies → look for `SCANDAL69_Auth`. It should have an expiry ~30 days from now.

3. **Logout works** — click Logout, verify cookie is removed and you're redirected to home.

4. **Ban check still works** — ban a user in the CRM, wait 5 minutes (cache TTL), visit any page as that user — should be redirected to `/auth/login?banned=1` and cookie cleared.

5. **Admin access still works** — log in as an admin email, visit `/crm` — should have access.

6. **No stale Session references** — run the grep from Task 6 Step 4 and confirm zero matches.
