# Free Trial Subscription Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace exe.io ad-gated free downloads with a 1-day free trial subscription granted on new user registration; users with no active subscription see a locked premium box and a modal CTA.

**Architecture:** A `plan='trial'` row is inserted into the existing `subscriptions` table on signup (`status=active`, `expires_at=now+1day`). `GetActiveSubscriptionAsync()` already filters by `status=active AND expires_at > NOW()`, so the trial grants full subscriber access and expires automatically. Non-subscribers (expired trial or no account) see the premium download UI locked, with a Tailwind modal on page load. Disposable email domains are blocked at registration via a static bundled blocklist.

**Tech Stack:** ASP.NET Core MVC (.NET 10), C#, Supabase REST API (raw HttpClient), Tailwind CSS (CDN), vanilla JS.

---

## File Map

| File | Action |
|------|--------|
| `KJSWeb/Data/disposable_email_domains.txt` | Create — blocklist (one domain per line) |
| `KJSWeb/Services/DisposableEmailService.cs` | Create — singleton that loads blocklist into HashSet |
| `KJSWeb/Services/SupabaseService.cs` | Modify — add `CreateTrialSubscriptionAsync` |
| `KJSWeb/Services/ExeIoService.cs` | Delete |
| `KJSWeb/Services/TokenGenService.cs` | Modify — remove `GeneratePublicDownloadToken` |
| `KJSWeb/Controllers/AuthController.cs` | Modify — inject services, add disposable check + trial insert |
| `KJSWeb/Controllers/DownloadController.cs` | Delete |
| `KJSWeb/Controllers/HomeController.cs` | Modify — remove `PublicDownloadUrls` generation |
| `KJSWeb/Controllers/AsianScandalController.cs` | Modify — remove `PublicDownloadUrls` generation |
| `KJSWeb/Controllers/JGirlController.cs` | Modify — remove `PublicDownloadUrls` generation |
| `KJSWeb/Views/Download/Start.cshtml` | Delete |
| `KJSWeb/Views/Shared/_PremiumDownload.cshtml` | Modify — remove free card, add locked UI + modal |
| `KJSWeb/Views/Home/Details.cshtml` | Modify — remove `PublicDownloadUrls` from model init |
| `KJSWeb/Views/AsianScandal/Details.cshtml` | Modify — remove `PublicDownloadUrls` from model init |
| `KJSWeb/Views/JGirl/Details.cshtml` | Modify — remove `PublicDownloadUrls` from model init |
| `KJSWeb/Models/DownloadComponentModel.cs` | Modify — remove `PublicDownloadUrls` property |
| `Program.cs` | Modify — remove ExeIo DI, add DisposableEmailService singleton |

---

## Task 1: Download and bundle the disposable email blocklist

**Files:**
- Create: `KJSWeb/Data/disposable_email_domains.txt`

- [ ] **Step 1: Create the Data directory and download the blocklist**

```bash
mkdir -p /Users/mikeyoshino/gitRepos/KJSProject/KJSWeb/Data
curl -s https://raw.githubusercontent.com/disposable-email-domains/disposable-email-domains/master/disposable_email_blocklist.conf \
  -o /Users/mikeyoshino/gitRepos/KJSProject/KJSWeb/Data/disposable_email_domains.txt
wc -l /Users/mikeyoshino/gitRepos/KJSProject/KJSWeb/Data/disposable_email_domains.txt
```

Expected: file created with several thousand lines (one domain per line, e.g. `mailinator.com`).

- [ ] **Step 2: Mark the file as Content in the csproj so it's copied to output**

Open `KJSWeb/KJSWeb.csproj` and add inside `<Project>`:

```xml
<ItemGroup>
  <Content Include="Data\disposable_email_domains.txt">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

- [ ] **Step 3: Commit**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
git add KJSWeb/Data/disposable_email_domains.txt KJSWeb/KJSWeb.csproj
git commit -m "feat: add disposable email domain blocklist"
```

---

## Task 2: Create DisposableEmailService

**Files:**
- Create: `KJSWeb/Services/DisposableEmailService.cs`
- Modify: `Program.cs`

- [ ] **Step 1: Create the service**

Create `KJSWeb/Services/DisposableEmailService.cs`:

```csharp
namespace KJSWeb.Services;

public class DisposableEmailService
{
    private readonly HashSet<string> _blockedDomains;

    public DisposableEmailService(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "Data", "disposable_email_domains.txt");
        _blockedDomains = File.Exists(path)
            ? File.ReadAllLines(path)
                  .Select(l => l.Trim().ToLowerInvariant())
                  .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
                  .ToHashSet()
            : new HashSet<string>();
    }

    public bool IsDisposable(string email)
    {
        var at = email.IndexOf('@');
        if (at < 0) return false;
        var domain = email[(at + 1)..].ToLowerInvariant().Trim();
        return _blockedDomains.Contains(domain);
    }
}
```

- [ ] **Step 2: Register as singleton in Program.cs**

In `KJSWeb/Program.cs`, add after the existing singleton registrations (around line 15):

```csharp
builder.Services.AddSingleton<KJSWeb.Services.DisposableEmailService>();
```

- [ ] **Step 3: Build to verify**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
dotnet build KJSWeb
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add KJSWeb/Services/DisposableEmailService.cs KJSWeb/Program.cs
git commit -m "feat: add DisposableEmailService to block throwaway email registrations"
```

---

## Task 3: Add CreateTrialSubscriptionAsync to SupabaseService

**Files:**
- Modify: `KJSWeb/Services/SupabaseService.cs`

- [ ] **Step 1: Add the method after `CreateSubscriptionAsync` (around line 62)**

In `KJSWeb/Services/SupabaseService.cs`, add this method after `CreateSubscriptionAsync`:

```csharp
public async Task<bool> CreateTrialSubscriptionAsync(string userId)
{
    using var http = _httpClientFactory.CreateClient();
    var payload = JsonSerializer.Serialize(new
    {
        user_id = userId,
        plan = "trial",
        status = "active",
        amount_usd = 0m,
        expires_at = DateTime.UtcNow.AddDays(1).ToString("o")
    });

    var request = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/rest/v1/subscriptions");
    request.Headers.Add("apikey", _serviceKey);
    request.Headers.Add("Authorization", $"Bearer {_serviceKey}");
    request.Headers.Add("Prefer", "return=minimal");
    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

    var response = await http.SendAsync(request);
    return response.IsSuccessStatusCode;
}
```

- [ ] **Step 2: Build to verify**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
dotnet build KJSWeb
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add KJSWeb/Services/SupabaseService.cs
git commit -m "feat: add CreateTrialSubscriptionAsync to SupabaseService"
```

---

## Task 4: Update AuthController — disposable email check + trial insert

**Files:**
- Modify: `KJSWeb/Controllers/AuthController.cs`

- [ ] **Step 1: Inject DisposableEmailService and SupabaseService into AuthController**

Replace the current constructor in `KJSWeb/Controllers/AuthController.cs`:

```csharp
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
    private readonly DisposableEmailService _disposableEmail;
    private readonly SupabaseService _supabase;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IConfiguration config, DisposableEmailService disposableEmail, SupabaseService supabase, ILogger<AuthController> logger)
    {
        _supabaseUrl = config["Supabase:Url"]!;
        _supabaseKey = config["Supabase:Key"]!;
        _disposableEmail = disposableEmail;
        _supabase = supabase;
        _logger = logger;
    }
```

- [ ] **Step 2: Add disposable email check to Signup POST (before Supabase call)**

In the `Signup` POST method, add this block immediately after the password length check (after the `if (password.Length < 6)` block, before `using var http = new HttpClient()`):

```csharp
        if (_disposableEmail.IsDisposable(email))
        {
            ViewBag.Error = "Please use a permanent email address.";
            return View();
        }
```

- [ ] **Step 3: Add trial insert after successful sign-in**

In the `Signup` POST method, replace the block that handles a successful signup (the `if (root.TryGetProperty("access_token", out _))` block) with:

```csharp
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
```

- [ ] **Step 4: Build to verify**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
dotnet build KJSWeb
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add KJSWeb/Controllers/AuthController.cs
git commit -m "feat: block disposable emails on signup and grant 1-day trial subscription"
```

---

## Task 5: Delete exe.io infrastructure

**Files:**
- Delete: `KJSWeb/Services/ExeIoService.cs`
- Delete: `KJSWeb/Controllers/DownloadController.cs`
- Delete: `KJSWeb/Views/Download/Start.cshtml` (and directory if empty)
- Modify: `Program.cs` — remove ExeIo DI line
- Modify: `KJSWeb/Services/TokenGenService.cs` — remove `GeneratePublicDownloadToken`

- [ ] **Step 1: Delete ExeIoService**

```bash
rm /Users/mikeyoshino/gitRepos/KJSProject/KJSWeb/Services/ExeIoService.cs
```

- [ ] **Step 2: Remove ExeIoService DI registration from Program.cs**

In `KJSWeb/Program.cs`, remove this line (currently line 15):

```csharp
builder.Services.AddScoped<KJSWeb.Services.ExeIoService>();
```

- [ ] **Step 3: Delete DownloadController and its views**

```bash
rm /Users/mikeyoshino/gitRepos/KJSProject/KJSWeb/Controllers/DownloadController.cs
rm /Users/mikeyoshino/gitRepos/KJSProject/KJSWeb/Views/Download/Start.cshtml
rmdir /Users/mikeyoshino/gitRepos/KJSProject/KJSWeb/Views/Download/
```

- [ ] **Step 4: Remove GeneratePublicDownloadToken from TokenGenService**

Open `KJSWeb/Services/TokenGenService.cs` and delete the `GeneratePublicDownloadToken` method (around line 51). It will look like:

```csharp
    public string GeneratePublicDownloadToken(string b2Path)
    {
        // ... method body ...
    }
```

Delete only that method. Leave `GenerateDownloadToken` intact — it's used by subscriber download links.

- [ ] **Step 5: Build to verify**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
dotnet build KJSWeb
```

Expected: Build succeeded, 0 errors. If there are compilation errors referencing `ExeIoService` or `DownloadController`, check that step 2 removed the DI line correctly.

- [ ] **Step 6: Commit**

```bash
git add -u
git commit -m "feat: remove exe.io service and free download controller"
```

---

## Task 6: Remove PublicDownloadUrls from controllers and model

**Files:**
- Modify: `KJSWeb/Controllers/HomeController.cs`
- Modify: `KJSWeb/Controllers/AsianScandalController.cs`
- Modify: `KJSWeb/Controllers/JGirlController.cs`
- Modify: `KJSWeb/Models/DownloadComponentModel.cs`

- [ ] **Step 1: HomeController — remove the PublicDownloadUrls generation block**

In `KJSWeb/Controllers/HomeController.cs`, find and delete this block (around lines 188-194):

```csharp
        // Always show free download buttons — exe.io link generated on first click
        if (post.OurDownloadLink != null && post.OurDownloadLink.Any())
        {
            var siteBase = $"{Request.Scheme}://{Request.Host.Value}";
            ViewBag.PublicDownloadUrls = post.OurDownloadLink
                .Select((_, i) => $"{siteBase}/download/public?postId={post.Id}&table=posts&part={i}")
                .ToList();
        }
```

- [ ] **Step 2: AsianScandalController — remove the PublicDownloadUrls generation block**

In `KJSWeb/Controllers/AsianScandalController.cs`, find and delete this identical block (around lines 111-118):

```csharp
        // Always show free download buttons — exe.io link generated on first click
        if (post.OurDownloadLink != null && post.OurDownloadLink.Any())
        {
            var siteBase = $"{Request.Scheme}://{Request.Host.Value}";
            ViewBag.PublicDownloadUrls = post.OurDownloadLink
                .Select((_, i) => $"{siteBase}/download/public?postId={post.Id}&table=posts&part={i}")
                .ToList();
        }
```

- [ ] **Step 3: JGirlController — remove the PublicDownloadUrls generation block**

In `KJSWeb/Controllers/JGirlController.cs`, find and delete this block (around lines 66-73):

```csharp
        // Always show free download buttons — exe.io link generated on first click
        if (originalDownloadLinks.Any())
        {
            var siteBase = $"{Request.Scheme}://{Request.Host.Value}";
            ViewBag.PublicDownloadUrls = originalDownloadLinks
                .Select((_, i) => $"{siteBase}/download/public?postId={post.Id}&table=jgirl_posts&part={i}")
                .ToList();
        }
```

- [ ] **Step 4: Remove PublicDownloadUrls from DownloadComponentModel**

Replace the contents of `KJSWeb/Models/DownloadComponentModel.cs` with:

```csharp
namespace KJSWeb.Models;

public class DownloadComponentModel
{
    public int DownloadCount { get; set; }
    public List<string> DownloadUrls { get; set; } = new();
    public bool IsSubscribed { get; set; }
}
```

- [ ] **Step 5: Build to verify**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
dotnet build KJSWeb
```

Expected: Build errors for `PublicDownloadUrls` references in the three Details views. That's expected — fix in the next step.

- [ ] **Step 6: Update Home/Details.cshtml — remove PublicDownloadUrls from model init**

In `KJSWeb/Views/Home/Details.cshtml`, find (around line 58):

```csharp
                @await Html.PartialAsync("_PremiumDownload", new KJSWeb.Models.DownloadComponentModel
                {
                    DownloadCount = Model.OurDownloadLink.Count,
                    DownloadUrls = ViewBag.DownloadUrls as List<string> ?? new List<string>(),
                    IsSubscribed = ViewBag.HasActiveSubscription == true,
                    PublicDownloadUrls = ViewBag.PublicDownloadUrls as List<string>
                })
```

Replace with:

```csharp
                @await Html.PartialAsync("_PremiumDownload", new KJSWeb.Models.DownloadComponentModel
                {
                    DownloadCount = Model.OurDownloadLink.Count,
                    DownloadUrls = ViewBag.DownloadUrls as List<string> ?? new List<string>(),
                    IsSubscribed = ViewBag.HasActiveSubscription == true
                })
```

- [ ] **Step 7: Update AsianScandal/Details.cshtml**

In `KJSWeb/Views/AsianScandal/Details.cshtml`, find (around line 59):

```csharp
                @await Html.PartialAsync("_PremiumDownload", new KJSWeb.Models.DownloadComponentModel
                {
                    DownloadCount = Model.OurDownloadLink.Count,
                    DownloadUrls = ViewBag.DownloadUrls as List<string> ?? new List<string>(),
                    IsSubscribed = ViewBag.HasActiveSubscription == true,
                    PublicDownloadUrls = ViewBag.PublicDownloadUrls as List<string>
                })
```

Replace with:

```csharp
                @await Html.PartialAsync("_PremiumDownload", new KJSWeb.Models.DownloadComponentModel
                {
                    DownloadCount = Model.OurDownloadLink.Count,
                    DownloadUrls = ViewBag.DownloadUrls as List<string> ?? new List<string>(),
                    IsSubscribed = ViewBag.HasActiveSubscription == true
                })
```

- [ ] **Step 8: Update JGirl/Details.cshtml**

In `KJSWeb/Views/JGirl/Details.cshtml`, find (around line 87):

```csharp
        @await Html.PartialAsync("_PremiumDownload", new KJSWeb.Models.DownloadComponentModel
        {
            DownloadCount = ...,
            DownloadUrls = ViewBag.DownloadUrls as List<string> ?? new List<string>(),
            IsSubscribed = ViewBag.HasActiveSubscription == true,
            PublicDownloadUrls = ViewBag.PublicDownloadUrls as List<string>
        })
```

Replace with:

```csharp
        @await Html.PartialAsync("_PremiumDownload", new KJSWeb.Models.DownloadComponentModel
        {
            DownloadCount = ...,
            DownloadUrls = ViewBag.DownloadUrls as List<string> ?? new List<string>(),
            IsSubscribed = ViewBag.HasActiveSubscription == true
        })
```

(Keep `DownloadCount` value exactly as it was — just remove the `PublicDownloadUrls` line.)

- [ ] **Step 9: Build to verify**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
dotnet build KJSWeb
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 10: Commit**

```bash
git add KJSWeb/Controllers/HomeController.cs KJSWeb/Controllers/AsianScandalController.cs \
        KJSWeb/Controllers/JGirlController.cs KJSWeb/Models/DownloadComponentModel.cs \
        KJSWeb/Views/Home/Details.cshtml KJSWeb/Views/AsianScandal/Details.cshtml \
        KJSWeb/Views/JGirl/Details.cshtml
git commit -m "feat: remove free download URL generation from controllers and views"
```

---

## Task 7: Rewrite _PremiumDownload.cshtml — locked UI + subscribe modal

**Files:**
- Modify: `KJSWeb/Views/Shared/_PremiumDownload.cshtml`

- [ ] **Step 1: Replace the entire file contents**

Replace `KJSWeb/Views/Shared/_PremiumDownload.cshtml` with:

```cshtml
@model KJSWeb.Models.DownloadComponentModel
@{
    var downloadUrls = Model.DownloadUrls ?? new List<string>();
    var dlCount = downloadUrls.Count;
    var isSubscribed = Model.IsSubscribed;
}

<style>
    .glass-card {
        background: rgba(17, 24, 39, 0.7);
        backdrop-filter: blur(16px);
        -webkit-backdrop-filter: blur(16px);
        border: 1px solid rgba(255, 255, 255, 0.05);
    }
    .premium-glow {
        position: relative;
    }
    .premium-glow::before {
        content: "";
        position: absolute;
        inset: -2px;
        background: linear-gradient(45deg, #FF4500, #ff8c00, #FF4500);
        border-radius: inherit;
        z-index: -1;
        opacity: 0;
        transition: opacity 0.3s ease;
        filter: blur(8px);
    }
    .premium-glow:hover::before {
        opacity: 0.6;
    }
</style>

<div class="mt-12 mb-8">
    @if (isSubscribed)
    {
        <!-- SUBSCRIBER VIEW -->
        <div class="glass-card rounded-2xl p-8 border-l-4 border-l-orange-accent shadow-2xl relative overflow-hidden">
            <div class="absolute -top-24 -right-24 w-48 h-48 bg-orange-accent/20 rounded-full blur-3xl pointer-events-none"></div>

            <div class="flex items-center gap-4 mb-6 relative z-10">
                <div class="w-12 h-12 rounded-full bg-orange-accent/10 flex items-center justify-center text-orange-accent">
                    <svg class="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13 10V3L4 14h7v7l9-11h-7z"></path></svg>
                </div>
                <div>
                    <h3 class="text-2xl font-black text-slate-900 dark:text-white m-0 tracking-tight">@Localizer["Premium"] @Localizer["Download"]</h3>
                    <p class="text-sm font-semibold text-green-500 mt-1 uppercase tracking-wider">@Localizer["Instant • Full Speed • No Ads"]</p>
                </div>
            </div>

            @if (downloadUrls.Any())
            {
                <div class="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 gap-4 relative z-10">
                    @for (int i = 0; i < downloadUrls.Count; i++)
                    {
                        <a href="@downloadUrls[i]" download class="premium-glow flex items-center justify-center gap-2 py-4 px-6 bg-slate-900 dark:bg-black text-white rounded-xl font-bold uppercase tracking-wider hover:-translate-y-1 transition-transform duration-300 border border-white/10 hover:border-orange-accent/50 group">
                            <svg class="w-5 h-5 group-hover:animate-bounce" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4"></path></svg>
                            @(dlCount > 1 ? $"{Localizer["Download File"].Value} Part {i + 1}" : Localizer["Download File"].Value)
                        </a>
                    }
                </div>
            }
        </div>
    }
    else
    {
        <!-- LOCKED VIEW: premium box shown but no working links -->
        <div id="subscribe-modal-overlay"
             class="fixed inset-0 z-50 flex items-center justify-center bg-black/70 backdrop-blur-sm"
             style="display:none !important">
        </div>

        <!-- Modal -->
        <div id="subscribe-modal"
             class="fixed inset-0 z-50 flex items-center justify-center p-4"
             style="display:none !important">
            <div class="relative bg-gray-900 border border-white/10 rounded-2xl shadow-2xl max-w-sm w-full p-8 text-center">
                <!-- Close button -->
                <button onclick="document.getElementById('subscribe-modal').style.cssText='display:none !important'; document.getElementById('subscribe-modal-overlay').style.cssText='display:none !important';"
                        class="absolute top-4 right-4 text-gray-400 hover:text-white transition-colors text-xl leading-none"
                        aria-label="Dismiss">&#x2715;</button>

                <div class="w-16 h-16 rounded-2xl bg-gradient-to-br from-orange-400 to-orange-600 flex items-center justify-center text-white mx-auto mb-6 shadow-lg shadow-orange-500/30">
                    <svg class="w-8 h-8" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13 10V3L4 14h7v7l9-11h-7z"></path></svg>
                </div>

                @if (User.Identity?.IsAuthenticated == true)
                {
                    <h2 class="text-xl font-black text-white mb-2">Your access has expired</h2>
                    <p class="text-gray-400 text-sm mb-6">Subscribe to restore instant, unlimited downloads.</p>
                    <a href="/subscription/pricing"
                       class="block w-full py-3 px-6 bg-orange-500 hover:bg-orange-600 text-white font-black uppercase tracking-wider rounded-xl transition-colors shadow-lg shadow-orange-500/20">
                        Subscribe from $5/month
                    </a>
                }
                else
                {
                    <h2 class="text-xl font-black text-white mb-2">Get 1 free day</h2>
                    <p class="text-gray-400 text-sm mb-6">Create a free account and get instant access for 24 hours — no credit card needed.</p>
                    <a href="/auth/signup"
                       class="block w-full py-3 px-6 bg-orange-500 hover:bg-orange-600 text-white font-black uppercase tracking-wider rounded-xl transition-colors shadow-lg shadow-orange-500/20 mb-3">
                        Create Free Account
                    </a>
                    <a href="/auth/login"
                       class="block w-full py-3 px-6 bg-white/5 hover:bg-white/10 text-gray-300 font-bold uppercase tracking-wider rounded-xl transition-colors text-sm">
                        Already have an account? Log in
                    </a>
                }
            </div>
        </div>

        <!-- Backdrop + show modal on load -->
        <script>
            (function () {
                var overlay = document.getElementById('subscribe-modal-overlay');
                var modal = document.getElementById('subscribe-modal');
                overlay.style.cssText = '';
                modal.style.cssText = '';
            })();
        </script>

        <!-- Locked premium box -->
        <div class="premium-glow relative bg-gradient-to-br from-slate-900 to-black rounded-2xl p-8 border border-white/10 shadow-2xl overflow-hidden max-w-lg mx-auto">
            <div class="absolute inset-0 bg-gradient-to-t from-orange-accent/10 to-transparent pointer-events-none"></div>

            <div class="flex items-center gap-4 mb-6 relative z-10">
                <div class="w-12 h-12 rounded-full bg-orange-accent/10 flex items-center justify-center text-orange-accent">
                    <svg class="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z"></path></svg>
                </div>
                <div>
                    <h3 class="text-2xl font-black text-white m-0 tracking-tight">@Localizer["Premium"] @Localizer["Download"]</h3>
                    <p class="text-sm font-semibold text-orange-400 mt-1 uppercase tracking-wider">@Localizer["Subscription Required"]</p>
                </div>
            </div>

            <div class="grid grid-cols-1 sm:grid-cols-2 gap-3 relative z-10 select-none">
                @for (int i = 0; i < Math.Max(Model.DownloadCount, 1); i++)
                {
                    <div class="flex items-center justify-center gap-2 py-4 px-6 bg-white/5 text-gray-500 rounded-xl font-bold uppercase tracking-wider border border-white/5 cursor-not-allowed">
                        <svg class="w-4 h-4 opacity-50" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z"></path></svg>
                        @(Model.DownloadCount > 1 ? $"{Localizer["Download File"].Value} Part {i + 1}" : Localizer["Download File"].Value)
                    </div>
                }
            </div>
        </div>
    }
</div>
```

- [ ] **Step 2: Build to verify**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
dotnet build KJSWeb
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add KJSWeb/Views/Shared/_PremiumDownload.cshtml
git commit -m "feat: replace free download card with locked premium box and subscribe modal"
```

---

## Task 8: Write design doc

**Files:**
- Create: `docs/superpowers/specs/2026-04-22-free-trial-design.md`

- [ ] **Step 1: Copy the approved spec from the plan file**

```bash
cp /Users/mikeyoshino/.claude/plans/current-we-have-exe-io-logical-pond.md \
   /Users/mikeyoshino/gitRepos/KJSProject/docs/superpowers/specs/2026-04-22-free-trial-design.md
```

- [ ] **Step 2: Commit**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
git add docs/superpowers/specs/2026-04-22-free-trial-design.md
git commit -m "docs: add free trial design spec"
```

---

## Verification Checklist

After all tasks are complete, verify end-to-end:

1. **New user registration (real email):** Register with a real email → Supabase `subscriptions` table has a new row with `plan=trial`, `status=active`, `expires_at ≈ now+1day`. Navigate to any post → download buttons are active and working.

2. **Disposable email blocked:** Try registering with `test@mailinator.com` → error "Please use a permanent email address" shown, no account created.

3. **Expired trial (authenticated):** In Supabase dashboard, set a trial row's `expires_at` to 1 hour ago. Revisit any post page → modal appears with "Your access has expired" and "Subscribe from $5/month" link. Click × → modal closes, locked grey buttons remain, no working download links.

4. **Unauthenticated visitor:** Log out, visit any post → modal appears with "Get 1 free day" and "Create Free Account" button linking to `/auth/signup`. "Already have an account? Log in" link also present.

5. **Existing user (no subscription):** Log in as an old account with no subscription row → same locked box + "Your access has expired" modal.

6. **No free download route:** Navigate to `/download/public?postId=xxx&table=posts&part=0` → 404 (route gone).

7. **Clean build:** `dotnet build KJSWeb` → 0 errors, 0 warnings about missing ExeIo references.
