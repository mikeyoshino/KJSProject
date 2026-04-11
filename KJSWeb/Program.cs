var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<KJSWeb.Services.SupabaseService>();
builder.Services.AddSingleton<KJSWeb.Services.BlockonomicsService>();
builder.Services.AddSingleton<KJSWeb.Services.TokenGenService>();
builder.Services.AddScoped<KJSWeb.Services.AdminService>();
builder.Services.AddScoped<KJSWeb.Filters.AdminAuthFilter>();
builder.Services.AddSingleton<KJSWeb.Services.EmailService>();

// Add session support for auth
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(24);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.Name = "SCANDAL69_Session";
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

app.UseSession(); // Must be before Authorization

app.UseMiddleware<KJSWeb.Middleware.BanCheckMiddleware>();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
