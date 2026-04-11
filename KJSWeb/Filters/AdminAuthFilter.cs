using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

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
        var session = context.HttpContext.Session;
        var userId = session.GetString("user_id");

        if (string.IsNullOrEmpty(userId))
        {
            context.Result = new RedirectResult("/auth/login?returnUrl=/crm");
            return;
        }

        var userEmail = session.GetString("user_email") ?? "";
        var adminEmails = _config.GetSection("Admin:Emails").Get<string[]>() ?? Array.Empty<string>();

        if (!adminEmails.Contains(userEmail, StringComparer.OrdinalIgnoreCase))
        {
            context.Result = new StatusCodeResult(403);
            return;
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
