using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace KJSWeb.Controllers;

public class LanguageController : Controller
{
    private static readonly HashSet<string> _supported = ["en", "ja"];

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Set(string culture, string returnUrl)
    {
        if (!_supported.Contains(culture))
            culture = "en";

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { IsEssential = true } // no MaxAge = browser-session cookie
        );

        return LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
    }
}
