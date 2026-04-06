using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using RgToB2Migrator.Configuration;
using System.Text.RegularExpressions;

namespace RgToB2Migrator.Services;

/// <summary>Thrown when Rapidgator reports daily bandwidth is exhausted.</summary>
public sealed class RapidgatorTrafficExceededException(string message)
    : Exception($"Rapidgator daily traffic exceeded: {message}");


public class RapidgatorDownloadService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RapidgatorSettings _settings;
    private readonly ILogger<RapidgatorDownloadService> _logger;
    private readonly MigratorSettings _migratorSettings;
    private string? _sessionId;
    private DateTime _sessionExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    public RapidgatorDownloadService(
        IHttpClientFactory httpClientFactory,
        IOptions<RapidgatorSettings> settings,
        IOptions<MigratorSettings> migratorSettings,
        ILogger<RapidgatorDownloadService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _migratorSettings = migratorSettings.Value;
        _logger = logger;
    }

    private HttpClient CreateClient() => _httpClientFactory.CreateClient("Rapidgator");

    public async Task<string> GetSessionIdAsync(CancellationToken ct = default)
    {
        if (_sessionId != null && DateTime.UtcNow < _sessionExpiry)
            return _sessionId;

        await _loginLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_sessionId != null && DateTime.UtcNow < _sessionExpiry)
                return _sessionId;

            var url = $"{_settings.ApiBaseUrl}/user/login?login={Uri.EscapeDataString(_settings.Username)}&password={Uri.EscapeDataString(_settings.Password)}";

            using var client = CreateClient();
            var response = await client.GetStringAsync(url, ct);
            var json = JObject.Parse(response);

            // API v2 returns "token" (previously "session_id")
            var sid = json["response"]?["token"]?.ToString()
                   ?? json["response"]?["session_id"]?.ToString();
            if (string.IsNullOrEmpty(sid))
                throw new InvalidOperationException($"Rapidgator login failed: {response}");

            var isPremium = json["response"]?["user"]?["is_premium"]?.Value<bool>() ?? false;
            if (!isPremium)
                _logger.LogWarning("Rapidgator account does not report is_premium=true — downloads may fail if subscription is inactive");

            _sessionId = sid;
            _sessionExpiry = DateTime.UtcNow.AddMinutes(50);
            _logger.LogInformation("Rapidgator login successful, session valid until {Expiry}", _sessionExpiry);
            return _sessionId;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async Task<(string downloadUrl, string fileName, long fileSize)> GetDownloadLinkAsync(
        string rapidgatorUrl, CancellationToken ct = default)
    {
        var sessionId = await GetSessionIdAsync(ct);
        var url = $"{_settings.ApiBaseUrl}/file/download?sid={sessionId}&url={Uri.EscapeDataString(rapidgatorUrl)}";

        using var client = CreateClient();
        var response = await client.GetStringAsync(url, ct);

        // Rapidgator returns two concatenated JSON objects (401 session check + 200 result).
        // Read all of them and prefer the one with status=200.
        var json = ParseBestJsonObject(response);

        // Check for known API error codes before attempting to read the download URL
        var status = json["status"]?.Value<int>() ?? 200;
        if (status != 200)
        {
            var message = json["details"]?.ToString()
                       ?? json["message"]?.ToString()
                       ?? json.ToString();

            if (status == 406 ||
                message.Contains("traffic", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("bandwidth", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("limit", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "⛔ RAPIDGATOR DAILY TRAFFIC LIMIT EXCEEDED. " +
                    "Your solo plan (~55 GB/day) is exhausted. " +
                    "Wait until midnight UTC and run again. Raw response: {Response}", response);

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine();
                Console.WriteLine("╔══════════════════════════════════════════════════════╗");
                Console.WriteLine("║   ⛔  RAPIDGATOR DAILY TRAFFIC LIMIT EXCEEDED       ║");
                Console.WriteLine("╠══════════════════════════════════════════════════════╣");
                Console.WriteLine("║  Your solo plan (~55 GB/day) is exhausted.          ║");
                Console.WriteLine("║  Wait until midnight UTC then run again.            ║");
                Console.WriteLine("║  Already-migrated posts will be skipped.            ║");
                Console.WriteLine("╚══════════════════════════════════════════════════════╝");
                Console.WriteLine();
                Console.ResetColor();

                throw new RapidgatorTrafficExceededException(message);
            }

            throw new InvalidOperationException($"Rapidgator API error (status={status}): {message}");
        }

        var downloadUrl = json["response"]?["download_url"]?.ToString()
            ?? throw new InvalidOperationException($"No download URL in response: {response}");
        var fileName = json["response"]?["filename"]?.ToString() ?? "unknown";
        var fileSize = json["response"]?["file_size"]?.Value<long>() ?? 0;

        return (downloadUrl, fileName, fileSize);
    }

    public static bool IsFolderUrl(string url) =>
        url.Contains("rapidgator.net/folder/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Expands a Rapidgator folder URL into individual file URLs by calling the folder API.
    /// </summary>
    public async Task<List<string>> GetFolderFileUrlsAsync(string folderUrl, CancellationToken ct = default)
    {
        var match = Regex.Match(folderUrl, @"rapidgator\.net/folder/([a-f0-9]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new InvalidOperationException($"Cannot extract folder ID from: {folderUrl}");

        var folderId = match.Groups[1].Value;
        var sessionId = await GetSessionIdAsync(ct);
        var url = $"{_settings.ApiBaseUrl}/folder/content?sid={sessionId}&folder_id={folderId}";

        using var client = CreateClient();
        var response = await client.GetStringAsync(url, ct);
        _logger.LogDebug("Folder content response: {Response}", response);

        var json = ParseBestJsonObject(response);
        var status = json["status"]?.Value<int>() ?? 200;
        if (status != 200)
            throw new InvalidOperationException(
                $"Rapidgator folder API error (status={status}): {json["details"]}");

        var files = json["response"]?["files"] as JArray ?? [];
        var urls = files
            .Select(f => f["url"]?.ToString() ?? f["link"]?.ToString() ?? "")
            .Where(u => !string.IsNullOrEmpty(u))
            .ToList();

        _logger.LogInformation("Folder {FolderId} contains {Count} file(s)", folderId, urls.Count);
        return urls;
    }

    /// <summary>
    /// Rapidgator API may return multiple concatenated JSON objects in one response.
    /// Returns the object with the highest status priority: 200 > anything else.
    /// </summary>
    private static JObject ParseBestJsonObject(string response)
    {
        var objects = new List<JObject>();
        using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(response))
            { SupportMultipleContent = true };
        while (reader.Read())
            objects.Add(JObject.Load(reader));

        // Prefer status=200, otherwise take the last object
        return objects.FirstOrDefault(o => o["status"]?.Value<int>() == 200)
            ?? objects.Last();
    }

    public async Task DownloadFileAsync(string downloadUrl, string destPath, CancellationToken ct = default)
    {
        var tempPath = destPath + ".tmp";

        try
        {
            // Ensure directory exists
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            using var client = CreateClient();
            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Download failed with status {response.StatusCode} for {downloadUrl}");

            var totalBytes = response.Content.Headers.ContentLength ?? 0;

            await using var networkStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempPath,
                FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 * 1024 * 1024,
                useAsync: true);

            var buffer = new byte[1 * 1024 * 1024]; // 1 MB
            long downloaded = 0;
            var lastLog = DateTime.UtcNow;
            int read;

            while ((read = await networkStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;

                if (DateTime.UtcNow - lastLog >= TimeSpan.FromSeconds(10))
                {
                    lastLog = DateTime.UtcNow;
                    if (totalBytes > 0)
                        _logger.LogInformation("Downloading {File}: {Done:N0} / {Total:N0} MB ({Pct:F0}%)",
                            Path.GetFileName(destPath),
                            downloaded / 1_048_576, totalBytes / 1_048_576,
                            100.0 * downloaded / totalBytes);
                    else
                        _logger.LogInformation("Downloading {File}: {Done:N0} MB",
                            Path.GetFileName(destPath), downloaded / 1_048_576);
                }
            }

            // Move temp file to final location
            if (File.Exists(destPath))
                File.Delete(destPath);
            File.Move(tempPath, destPath);

            _logger.LogInformation("Downloaded {File}: {Size:N0} MB",
                Path.GetFileName(destPath), downloaded / 1_048_576);
        }
        catch (Exception ex)
        {
            // Clean up temp file on error
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); }
                catch { /* ignore cleanup errors */ }

            _logger.LogError(ex, "Failed to download file to {Path}", destPath);
            throw;
        }
    }
}
