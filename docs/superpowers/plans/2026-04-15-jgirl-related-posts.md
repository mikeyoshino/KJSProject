# JGirl Related Posts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Display up to 6 related JGirl posts below each post's detail page, ranked by number of shared tags (most overlap first), with a same-source tie-break and a recency fallback when no tags exist.

**Architecture:** A new `GetRelatedJGirlPostsAsync` method on `SupabaseService` queries `jgirl_posts` via the PostgREST `ov` (array overlap) operator to fetch up to 30 tag-matched candidates, then ranks them in C# by shared-tag count. `JGirlController.Details` calls this in parallel with existing queries and passes results via `ViewBag`. A new `_RelatedPosts.cshtml` partial renders the grid below the tags section.

**Tech Stack:** ASP.NET Core MVC (.NET 10), Supabase REST API (PostgREST), Tailwind CSS, C#

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `KJSWeb/Services/SupabaseService.cs` | Modify | Add `GetRelatedJGirlPostsAsync` + `MapJGirlSearchDto` helper |
| `KJSWeb/Controllers/JGirlController.cs` | Modify | Fetch related posts in `Details`, pass via `ViewBag.RelatedPosts` |
| `KJSWeb/Views/JGirl/_RelatedPosts.cshtml` | Create | Partial view — 3-col thumbnail grid with title, source badge, shared-tag count |
| `KJSWeb/Views/JGirl/Details.cshtml` | Modify | Render `_RelatedPosts` partial after the tags section |

---

### Task 1: Add `GetRelatedJGirlPostsAsync` to SupabaseService

**Files:**
- Modify: `KJSWeb/Services/SupabaseService.cs` — insert after the `SearchJGirlPostsAsync` method (around line 491)

- [ ] **Step 1: Add `MapJGirlSearchDto` helper and `GetRelatedJGirlPostsAsync` method**

Open `KJSWeb/Services/SupabaseService.cs`. Find the line:
```csharp
    public async Task<List<JGirlPost>> SearchJGirlPostsAsync(string query, int limit = 6)
```

Insert the following block **above** that line:

```csharp
    public async Task<List<JGirlPost>> GetRelatedJGirlPostsAsync(Guid postId, List<string> tags, string source, int limit = 6)
    {
        // No tags → fall back to recent posts from same source
        if (!tags.Any())
        {
            using var fb = _httpClientFactory.CreateClient();
            var fbUrl = $"{_supabaseUrl}/rest/v1/jgirl_posts" +
                        $"?select=id,title,thumbnail_url,source,created_at,tags" +
                        $"&status=eq.published" +
                        $"&source=eq.{Uri.EscapeDataString(source)}" +
                        $"&id=neq.{postId}" +
                        $"&order=created_at.desc" +
                        $"&limit={limit}";
            var fbReq = new HttpRequestMessage(HttpMethod.Get, fbUrl);
            fbReq.Headers.Add("apikey", _supabaseKey);
            fbReq.Headers.Add("Authorization", $"Bearer {_supabaseKey}");
            var fbResp = await fb.SendAsync(fbReq);
            if (!fbResp.IsSuccessStatusCode) return new();
            var fbDtos = JsonSerializer.Deserialize<List<JGirlSearchDto>>(await fbResp.Content.ReadAsStringAsync());
            return fbDtos?.Select(MapJGirlSearchDto).ToList() ?? new();
        }

        using var http = _httpClientFactory.CreateClient();
        // Fetch a wider candidate pool (30) sorted by recency; re-rank by tag overlap in C#
        var tagJson = Uri.EscapeDataString("[" + string.Join(",", tags.Select(t => $"\"{t}\"")) + "]");
        var url = $"{_supabaseUrl}/rest/v1/jgirl_posts" +
                  $"?select=id,title,thumbnail_url,source,created_at,tags" +
                  $"&status=eq.published" +
                  $"&tags=ov.{tagJson}" +
                  $"&id=neq.{postId}" +
                  $"&order=created_at.desc" +
                  $"&limit=30";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("apikey", _supabaseKey);
        request.Headers.Add("Authorization", $"Bearer {_supabaseKey}");

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new();

        var dtos = JsonSerializer.Deserialize<List<JGirlSearchDto>>(await response.Content.ReadAsStringAsync());
        if (dtos == null) return new();

        var tagSet = tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return dtos
            .Select(MapJGirlSearchDto)
            .OrderByDescending(p => p.Tags.Count(t => tagSet.Contains(t)))
            .ThenByDescending(p => p.Source == source)
            .Take(limit)
            .ToList();
    }

    private JGirlPost MapJGirlSearchDto(JGirlSearchDto d) => new()
    {
        Id           = Guid.TryParse(d.id, out var g) ? g : Guid.Empty,
        Title        = d.title,
        ThumbnailUrl = d.thumbnail_url ?? "",
        Source       = d.source ?? "",
        CreatedAt    = DateTime.TryParse(d.created_at, out var dt) ? dt : DateTime.UtcNow,
        Tags         = d.tags?.ToList() ?? new(),
    };

```

> **Note:** `JGirlSearchDto` and `_supabaseKey`/`_supabaseUrl`/`_httpClientFactory` are already defined in this file — no new fields needed.

- [ ] **Step 2: Build and verify no compile errors**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject && dotnet build KJSProject.sln
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
git add KJSWeb/Services/SupabaseService.cs
git commit -m "feat: add GetRelatedJGirlPostsAsync with tag-overlap ranking"
```

---

### Task 2: Wire related posts into JGirlController.Details

**Files:**
- Modify: `KJSWeb/Controllers/JGirlController.cs` — `Details` action (line 49)

- [ ] **Step 1: Update the Details action to fetch related posts in parallel**

Open `KJSWeb/Controllers/JGirlController.cs`. Replace the `Details` action:

```csharp
[Route("post/{id}")]
public async Task<IActionResult> Details(string id)
{
    var post = await _supabase.GetJGirlPostByIdAsync(id);
    if (post == null) return NotFound();

    var workerBase = _config["CloudflareWorker:DownloadWorkerUrl"]?.TrimEnd('/') ?? "";
    var b2Base     = _config["B2:PublicBaseUrl"]?.TrimEnd('/') ?? "https://f005.backblazeb2.com/file/KJSProject";

    // Fetch related posts in parallel with subscription check
    var relatedTask = _supabase.GetRelatedJGirlPostsAsync(post.Id, post.Tags.ToList(), post.Source, limit: 6);

    // Capture original download links before URL rewriting (needed for token generation)
    var originalDownloadLinks = post.DownloadLinks.ToList();

    RewritePost(post);

    // Always show free download buttons — exe.io link generated on first click
    if (originalDownloadLinks.Any())
    {
        var siteBase = $"{Request.Scheme}://{Request.Host.Value}";
        ViewBag.PublicDownloadUrls = originalDownloadLinks
            .Select((_, i) => $"{siteBase}/download/public?postId={post.Id}&table=jgirl_posts&part={i}")
            .ToList();
    }

    ViewData["OgTitle"]    = post.Title;
    ViewData["OgImage"]    = !string.IsNullOrEmpty(post.ThumbnailUrl) ? post.ThumbnailUrl : post.Images.FirstOrDefault() ?? "";
    ViewData["Description"] = post.Tags.Any() ? string.Join(", ", post.Tags.Take(10)) : post.Title;
    ViewData["OgType"]     = "article";

    var userId = HttpContext.Session.GetString("user_id");
    if (!string.IsNullOrEmpty(userId))
    {
        var activeSub = await _supabase.GetActiveSubscriptionAsync(userId);
        ViewBag.HasActiveSubscription = activeSub != null;

        if (activeSub != null && originalDownloadLinks.Any())
        {
            ViewBag.DownloadUrls = originalDownloadLinks.Select(url =>
            {
                var clean = url.StartsWith(b2Base)
                    ? url[b2Base.Length..].TrimStart('/')
                    : url.TrimStart('/');
                var token = _tokenGen.GenerateDownloadToken(userId, clean);
                return $"{workerBase}/download?file={Uri.EscapeDataString(clean)}&token={Uri.EscapeDataString(token)}";
            }).ToList();
        }
    }
    else
    {
        ViewBag.HasActiveSubscription = false;
    }

    // Rewrite thumbnails for related posts
    var related = await relatedTask;
    foreach (var rp in related)
        rp.ThumbnailUrl = ResolveRelatedThumb(rp.ThumbnailUrl, workerBase, b2Base);

    ViewBag.RelatedPosts = related;

    return View(post);
}
```

Also add this private helper at the bottom of the controller (before the closing `}`):

```csharp
private static string ResolveRelatedThumb(string url, string workerBase, string b2Base)
{
    if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(workerBase)) return url;
    if (url.StartsWith(b2Base))
        return workerBase + url[b2Base.Length..];
    return url;
}
```

- [ ] **Step 2: Build and verify**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject && dotnet build KJSProject.sln
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
git add KJSWeb/Controllers/JGirlController.cs
git commit -m "feat: fetch related JGirl posts in Details action"
```

---

### Task 3: Create `_RelatedPosts.cshtml` partial

**Files:**
- Create: `KJSWeb/Views/JGirl/_RelatedPosts.cshtml`

- [ ] **Step 1: Create the partial view**

Create `KJSWeb/Views/JGirl/_RelatedPosts.cshtml` with this content:

```cshtml
@model List<KJSWeb.Models.JGirlPost>
@if (Model == null || !Model.Any()) { return; }

<section class="mt-10 border-t border-slate-100 dark:border-gray-700 pt-8">
    <h3 class="text-[11px] font-black uppercase tracking-[0.3em] border-b-2 border-black dark:border-gray-600 pb-2 mb-6">
        Related Posts
    </h3>
    <div class="grid grid-cols-2 sm:grid-cols-3 gap-4">
        @foreach (var post in Model)
        {
            <a asp-controller="JGirl" asp-action="Details" asp-route-id="@post.Id"
               class="group block rounded-sm overflow-hidden bg-slate-50 dark:bg-gray-800 hover:shadow-md transition-shadow duration-200">
                <!-- Thumbnail -->
                <div class="aspect-video overflow-hidden bg-slate-200 dark:bg-gray-700">
                    @if (!string.IsNullOrEmpty(post.ThumbnailUrl))
                    {
                        <img src="@post.ThumbnailUrl" alt="@post.Title"
                             class="w-full h-full object-cover group-hover:scale-105 transition-transform duration-300"
                             loading="lazy" />
                    }
                </div>
                <!-- Info -->
                <div class="p-3">
                    <p class="text-xs font-bold leading-tight line-clamp-2 group-hover:text-orange-accent transition-colors mb-2">
                        @post.Title
                    </p>
                    <div class="flex items-center gap-1.5 flex-wrap">
                        @if (!string.IsNullOrEmpty(post.Source))
                        {
                            <span class="text-[9px] font-black uppercase tracking-widest px-1.5 py-0.5 rounded-sm bg-slate-900 text-white">
                                @post.Source
                            </span>
                        }
                        @foreach (var tag in post.Tags.Take(2))
                        {
                            <span class="text-[9px] text-slate-400 font-semibold">#@tag</span>
                        }
                    </div>
                </div>
            </a>
        }
    </div>
</section>
```

- [ ] **Step 2: Build and verify**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject && dotnet build KJSProject.sln
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
git add KJSWeb/Views/JGirl/_RelatedPosts.cshtml
git commit -m "feat: add _RelatedPosts partial for JGirl detail page"
```

---

### Task 4: Render related posts in Details.cshtml

**Files:**
- Modify: `KJSWeb/Views/JGirl/Details.cshtml` — after the tags section (line 107)

- [ ] **Step 1: Add the partial render after the tags section**

Open `KJSWeb/Views/JGirl/Details.cshtml`. Find the closing `</div>` of the tags section (around line 107):

```cshtml
    <!-- Tags -->
    @if (Model.Tags.Any())
    {
        <section class="border-t border-slate-100 dark:border-gray-700 pt-6">
            <div class="flex flex-wrap gap-2">
                @foreach (var tag in Model.Tags)
                {
                    <span class="bg-slate-50 dark:bg-gray-700 text-slate-500 dark:text-gray-400 hover:bg-orange-accent hover:text-white px-3 py-1.5 rounded-sm text-[10px] font-bold uppercase tracking-wider transition-colors cursor-default">#@tag</span>
                }
            </div>
        </section>
    }

</div>
```

Replace with:

```cshtml
    <!-- Tags -->
    @if (Model.Tags.Any())
    {
        <section class="border-t border-slate-100 dark:border-gray-700 pt-6">
            <div class="flex flex-wrap gap-2">
                @foreach (var tag in Model.Tags)
                {
                    <span class="bg-slate-50 dark:bg-gray-700 text-slate-500 dark:text-gray-400 hover:bg-orange-accent hover:text-white px-3 py-1.5 rounded-sm text-[10px] font-bold uppercase tracking-wider transition-colors cursor-default">#@tag</span>
                }
            </div>
        </section>
    }

    <!-- Related Posts -->
    @await Html.PartialAsync("_RelatedPosts", ViewBag.RelatedPosts as List<KJSWeb.Models.JGirlPost> ?? new List<KJSWeb.Models.JGirlPost>())

</div>
```

- [ ] **Step 2: Build and verify**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject && dotnet build KJSProject.sln
```
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Run the app and verify end-to-end**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject && dotnet run --project KJSWeb
```

Open `http://localhost:5000/jgirl/post/ed9529cb-1827-4d40-934c-d26d083fb686` and verify:
- A "Related Posts" section appears below the tags
- Cards show thumbnail, title, source badge, and up to 2 tag chips
- Clicking a card navigates to that post's detail page
- Posts with more shared tags appear before posts with fewer
- If the current post has no tags, 6 recent same-source posts appear instead

- [ ] **Step 4: Final commit**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject
git add KJSWeb/Views/JGirl/Details.cshtml
git commit -m "feat: render related JGirl posts on detail page"
```

- [ ] **Step 5: Push**

```bash
cd /Users/mikeyoshino/gitRepos/KJSProject && git push origin master
```
