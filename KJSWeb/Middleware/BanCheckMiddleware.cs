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
