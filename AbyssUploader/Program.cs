using AbyssUploader;
using AbyssUploader.Configuration;
using AbyssUploader.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureAppConfiguration((context, config) =>
{
    config
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true)
        .AddEnvironmentVariables();
});

builder.ConfigureServices((context, services) =>
{
    services.Configure<SupabaseSettings>(context.Configuration.GetSection("Supabase"));
    services.Configure<B2Settings>(context.Configuration.GetSection("B2"));
    services.Configure<AbyssSettings>(context.Configuration.GetSection("AbyssUploader"));

    services.AddHttpClient("Abyss", client => client.Timeout = Timeout.InfiniteTimeSpan);

    services.AddSingleton<SupabaseMigrationService>();
    services.AddSingleton<B2DownloadService>();
    services.AddSingleton<AbyssUploadService>();
    services.AddSingleton<AbyssOrchestrator>();
});

builder.ConfigureLogging((_, logging) =>
{
    logging.ClearProviders();
    logging.AddConsole();
});

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var orchestrator = host.Services.GetRequiredService<AbyssOrchestrator>();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.LogInformation("Cancellation requested");
    cts.Cancel();
};

try
{
    await orchestrator.RunAsync(cts.Token);
    logger.LogInformation("AbyssUploader completed successfully");
}
catch (OperationCanceledException)
{
    logger.LogInformation("AbyssUploader cancelled");
}
catch (Exception ex)
{
    logger.LogError(ex, "AbyssUploader failed");
    Environment.Exit(1);
}
