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
