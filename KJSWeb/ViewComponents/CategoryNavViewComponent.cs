using Microsoft.AspNetCore.Mvc;
using KJSWeb.Services;

namespace KJSWeb.ViewComponents;

public class CategoryNavViewComponent : ViewComponent
{
    private readonly SupabaseService _supabase;

    public CategoryNavViewComponent(SupabaseService supabase)
    {
        _supabase = supabase;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var categories = await _supabase.GetCategoriesAsync();
        return View(categories);
    }
}
