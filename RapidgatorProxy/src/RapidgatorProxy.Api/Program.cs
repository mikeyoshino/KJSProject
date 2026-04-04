using System.Net;
using Microsoft.EntityFrameworkCore;
using RapidgatorProxy.Api.BackgroundServices;
using RapidgatorProxy.Api.Configuration;
using RapidgatorProxy.Api.Data;
using RapidgatorProxy.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration binding
// ---------------------------------------------------------------------------
builder.Services.Configure<RapidgatorSettings>(builder.Configuration.GetSection("Rapidgator"));
builder.Services.Configure<ProxySettings>(builder.Configuration.GetSection("Proxy"));
builder.Services.Configure<CacheSettings>(builder.Configuration.GetSection("Cache"));
builder.Services.Configure<SupabaseSettings>(builder.Configuration.GetSection("Supabase"));
builder.Services.Configure<CorsSettings>(builder.Configuration.GetSection("Cors"));

// ---------------------------------------------------------------------------
// SQLite + EF Core
// ---------------------------------------------------------------------------
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "rg-proxy.db");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// ---------------------------------------------------------------------------
// HttpClient: Rapidgator (with optional proxy)
// ---------------------------------------------------------------------------
var proxyConfig = builder.Configuration.GetSection("Proxy").Get<ProxySettings>();
builder.Services.AddHttpClient("RapidgatorProxy", client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler();
    if (!string.IsNullOrEmpty(proxyConfig?.Address))
    {
        handler.Proxy = new WebProxy(proxyConfig.Address);
        if (!string.IsNullOrEmpty(proxyConfig.Username))
            handler.Proxy.Credentials = new NetworkCredential(proxyConfig.Username, proxyConfig.Password);
        handler.UseProxy = true;
    }
    return handler;
});

// HttpClient: Supabase (no proxy needed)
builder.Services.AddHttpClient("Supabase");

// ---------------------------------------------------------------------------
// Application Services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<RapidgatorApiClient>();
builder.Services.AddSingleton<FileDownloadService>();
builder.Services.AddSingleton<CacheManagerService>();
builder.Services.AddSingleton<DownloadCoordinator>();
builder.Services.AddScoped<AuthService>();

// Background services
builder.Services.AddHostedService<CacheCleanupService>();

// ---------------------------------------------------------------------------
// CORS
// ---------------------------------------------------------------------------
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (corsOrigins.Length > 0)
        {
            policy.WithOrigins(corsOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
        else
        {
            // Development: allow all origins
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    });
});

builder.Services.AddControllers();

// ---------------------------------------------------------------------------
// Build & Configure Pipeline
// ---------------------------------------------------------------------------
var app = builder.Build();

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// Ensure cache directory exists
var cacheDir = builder.Configuration.GetValue<string>("Cache:Directory") ?? "C:\\temp\\rg-downloads";
Directory.CreateDirectory(cacheDir);

app.UseCors();
app.MapControllers();

app.Logger.LogInformation("RapidgatorProxy API starting...");
app.Logger.LogInformation("Cache directory: {CacheDir}", cacheDir);
app.Logger.LogInformation("Database: {DbPath}", dbPath);

app.Run();
