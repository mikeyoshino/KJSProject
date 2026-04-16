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
var keysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "keys");
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("SCANDAL69");

// Cookie authentication — replaces AddDistributedMemoryCache + AddSession.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name       = "SCANDAL69_Auth";
        options.Cookie.HttpOnly   = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite   = SameSiteMode.Lax;
        options.ExpireTimeSpan    = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
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
