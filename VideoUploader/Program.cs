using VideoUploader;
using VideoUploader.Configuration;
using VideoUploader.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    services.Configure<BunnySettings>(context.Configuration.GetSection("Bunny"));

    services.AddSingleton<SupabaseMigrationService>();
    services.AddSingleton<B2DownloadService>();

    services.AddHttpClient("Bunny", (sp, client) =>
    {
        var settings = sp.GetRequiredService<IOptions<BunnySettings>>().Value;
        client.DefaultRequestHeaders.Add("AccessKey", settings.ApiKey);
        client.Timeout = Timeout.InfiniteTimeSpan;
    });

    services.AddSingleton<BunnyUploadService>();
    services.AddSingleton<VideoOrchestrator>();
});

builder.ConfigureLogging((_, logging) =>
{
    logging.ClearProviders();
    logging.AddConsole();
});

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var orchestrator = host.Services.GetRequiredService<VideoOrchestrator>();

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
    logger.LogInformation("VideoUploader completed successfully");
}
catch (OperationCanceledException)
{
    logger.LogInformation("VideoUploader cancelled");
}
catch (Exception ex)
{
    logger.LogError(ex, "VideoUploader failed");
    Environment.Exit(1);
}
