using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RgToB2Migrator.Configuration;
using RgToB2Migrator.Services;

// ── Parse CLI args ─────────────────────────────────────────────────────────────
// Usage:
//   dotnet run                  → migrate all pending posts
//   dotnet run -- --limit 10   → migrate at most 10 posts then stop
int? limit = null;
for (int i = 0; i < args.Length; i++)
{
    if ((args[i] == "--limit" || args[i] == "-n") && i + 1 < args.Length)
    {
        if (int.TryParse(args[i + 1], out var n) && n > 0)
            limit = n;
        else
        {
            Console.Error.WriteLine($"Invalid --limit value: {args[i + 1]}");
            Environment.Exit(1);
        }
    }
}

// ── Build host ─────────────────────────────────────────────────────────────────
var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureAppConfiguration((context, config) =>
{
    config
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true)
        .AddEnvironmentVariables();
});

builder.ConfigureServices((context, services) =>
{
    services.Configure<SupabaseSettings>(context.Configuration.GetSection("Supabase"));
    services.Configure<RapidgatorSettings>(context.Configuration.GetSection("Rapidgator"));
    services.Configure<GofileSettings>(context.Configuration.GetSection("Gofile"));
    services.Configure<MigratorSettings>(context.Configuration.GetSection("Migrator"));

    // Infinite timeout on both clients — downloads and uploads can be 10 GB+
    services.AddHttpClient("Rapidgator", client => client.Timeout = Timeout.InfiniteTimeSpan);
    services.AddHttpClient("Gofile",     client => client.Timeout = Timeout.InfiniteTimeSpan);

    services.AddSingleton<RapidgatorDownloadService>();
    services.AddSingleton<FileProcessingService>();
    services.AddSingleton<GofileUploadService>();
    services.AddSingleton<SupabaseMigrationService>();
    services.AddSingleton<MigrationOrchestrator>();
});

builder.ConfigureLogging((context, logging) =>
{
    logging.ClearProviders();
    logging.AddConsole();
});

var host = builder.Build();

// Ensure temp folder exists
var migratorSettings = host.Services.GetRequiredService<IConfiguration>().GetSection("Migrator").Get<MigratorSettings>();
if (migratorSettings != null && !string.IsNullOrEmpty(migratorSettings.TempFolder))
    Directory.CreateDirectory(migratorSettings.TempFolder);

// ── Run ────────────────────────────────────────────────────────────────────────
var logger = host.Services.GetRequiredService<ILogger<Program>>();
var orchestrator = host.Services.GetRequiredService<MigrationOrchestrator>();

// Debug: confirm what config values are actually loaded
var config = host.Services.GetRequiredService<IConfiguration>();
var supabaseUrl = config["Supabase:Url"] ?? "(empty)";
var serviceKey  = config["Supabase:ServiceKey"] ?? "(empty)";
var keyPreview  = serviceKey.Length > 20
    ? serviceKey[..10] + "..." + serviceKey[^10..]
    : "(too short or empty)";
logger.LogInformation("Supabase URL: {Url}", supabaseUrl);
logger.LogInformation("Service key loaded: {Preview} (length={Length})", keyPreview, serviceKey.Length);

if (limit.HasValue)
    logger.LogInformation("Run limit: {Limit} post(s)", limit.Value);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.LogInformation("Cancellation requested");
    cts.Cancel();
};

try
{
    await orchestrator.RunAsync(limit, cts.Token);
    logger.LogInformation("Migration completed successfully");
}
catch (OperationCanceledException)
{
    logger.LogInformation("Migration canceled");
}
catch (Exception ex)
{
    logger.LogError(ex, "Migration failed");
    Environment.Exit(1);
}
