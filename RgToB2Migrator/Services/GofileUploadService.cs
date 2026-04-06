using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using RgToB2Migrator.Configuration;

namespace RgToB2Migrator.Services;

/// <summary>
/// Uploads files to Gofile.io using the Gofile REST API.
///
/// Flow per post:
///   1. CreatePostFolderAsync  →  one folder per post, returns (folderId, downloadPageUrl)
///   2. UploadFileAsync        →  upload each processed file into that folder
///
/// Files are streamed directly from disk — no full load into memory, safe for 10 GB+ files.
/// </summary>
public class GofileUploadService
{
    private readonly GofileSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GofileUploadService> _logger;

    private const string ServersUrl = "https://api.gofile.io/servers";
    private const string CreateFolderUrl = "https://api.gofile.io/contents/createFolder";
    private const string GetAccountIdUrl = "https://api.gofile.io/accounts/getid";

    private string? _cachedRootFolderId;

    public GofileUploadService(
        IOptions<GofileSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<GofileUploadService> logger)
    {
        _settings = settings.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────
    //  Create a folder for a post
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a private Gofile folder for one post.
    /// Returns the folder ID (used when uploading files) and the public download page URL.
    /// </summary>
    public async Task<(string folderId, string downloadPageUrl)> CreatePostFolderAsync(
        string folderName, CancellationToken ct = default)
    {
        _logger.LogInformation("Creating Gofile folder: {Name}", folderName);

        using var http = _httpClientFactory.CreateClient("Gofile");

        var parentId = !string.IsNullOrWhiteSpace(_settings.ParentFolderId)
            ? _settings.ParentFolderId
            : await GetRootFolderIdAsync(ct);

        var body = new JObject
        {
            ["folderName"]    = folderName,
            ["parentFolderId"] = parentId
        };

        var request = new HttpRequestMessage(HttpMethod.Post, CreateFolderUrl)
        {
            Content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {_settings.Token}");

        var response = await http.SendAsync(request, ct);
        var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));

        if (json["status"]?.ToString() != "ok")
            throw new InvalidOperationException($"Gofile createFolder failed: {json}");

        var data = json["data"]!;
        var folderId = data["id"]!.ToString();
        var code = data["code"]!.ToString();
        var downloadPageUrl = $"https://gofile.io/d/{code}";

        _logger.LogInformation("Gofile folder created: {Url}", downloadPageUrl);
        return (folderId, downloadPageUrl);
    }

    // ──────────────────────────────────────────────────────────
    //  Upload one file into a folder
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads a single file to the specified Gofile folder.
    /// Uses StreamContent — the file is never fully loaded into memory.
    /// </summary>
    public async Task UploadFileAsync(
        string localFilePath, string fileName, string folderId, CancellationToken ct = default)
    {
        var fileSize = new FileInfo(localFilePath).Length;
        _logger.LogInformation("Uploading {FileName} ({Size:N0} bytes) to Gofile folder {FolderId}",
            fileName, fileSize, folderId);

        // Get best upload server
        var server = await GetBestServerAsync(ct);
        var uploadUrl = $"https://{server}.gofile.io/uploadFile";

        using var http = _httpClientFactory.CreateClient("Gofile");
        await using var fileStream = File.OpenRead(localFilePath);

        using var form = new MultipartFormDataContent();

        // Wrap stream to log upload progress every 10 seconds
        var progressStream = new ProgressStream(fileStream, fileSize, (done, total) =>
        {
            _logger.LogInformation("Uploading {File}: {Done:N0} / {Total:N0} MB ({Pct:F0}%)",
                fileName, done / 1_048_576, total / 1_048_576, 100.0 * done / total);
        });

        var fileContent = new StreamContent(progressStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        fileContent.Headers.ContentLength = fileSize;
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(_settings.Token), "token");
        form.Add(new StringContent(folderId), "folderId");

        var response = await http.PostAsync(uploadUrl, form, ct);
        var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));

        if (json["status"]?.ToString() != "ok")
            throw new InvalidOperationException($"Gofile upload failed for {fileName}: {json}");

        _logger.LogInformation("Uploaded {FileName} → fileId={FileId}",
            fileName, json["data"]?["fileId"]);
    }

    // ──────────────────────────────────────────────────────────
    //  Helper: get best upload server
    // ──────────────────────────────────────────────────────────

    private async Task<string> GetRootFolderIdAsync(CancellationToken ct)
    {
        if (_cachedRootFolderId != null) return _cachedRootFolderId;

        using var http = _httpClientFactory.CreateClient("Gofile");
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_settings.Token}");

        // Step 1: get account ID
        var idJson = JObject.Parse(await http.GetStringAsync(GetAccountIdUrl, ct));
        if (idJson["status"]?.ToString() != "ok")
            throw new InvalidOperationException($"Gofile getid failed: {idJson}");
        var accountId = idJson["data"]!["id"]!.ToString();

        // Step 2: get account details → rootFolder
        var detailJson = JObject.Parse(await http.GetStringAsync(
            $"https://api.gofile.io/accounts/{accountId}", ct));
        if (detailJson["status"]?.ToString() != "ok")
            throw new InvalidOperationException($"Gofile account details failed: {detailJson}");

        _cachedRootFolderId = detailJson["data"]!["rootFolder"]!.ToString();
        _logger.LogDebug("Gofile root folder ID: {Id}", _cachedRootFolderId);
        return _cachedRootFolderId;
    }

    // ──────────────────────────────────────────────────────────
    //  Progress-reporting stream wrapper
    // ──────────────────────────────────────────────────────────

    private sealed class ProgressStream(Stream inner, long totalBytes, Action<long, long> onProgress)
        : Stream
    {
        private long _sent;
        private DateTime _lastLog = DateTime.UtcNow;

        public override bool CanRead  => inner.CanRead;
        public override bool CanSeek  => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length   => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Report(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), ct);
            Report(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct);
            Report(read);
            return read;
        }

        private void Report(int bytes)
        {
            if (bytes <= 0 || totalBytes <= 0) return;
            _sent += bytes;
            if (DateTime.UtcNow - _lastLog >= TimeSpan.FromSeconds(10))
            {
                _lastLog = DateTime.UtcNow;
                onProgress(_sent, totalBytes);
            }
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private async Task<string> GetBestServerAsync(CancellationToken ct)
    {
        using var http = _httpClientFactory.CreateClient("Gofile");
        var json = JObject.Parse(await http.GetStringAsync(ServersUrl, ct));

        if (json["status"]?.ToString() != "ok")
            throw new InvalidOperationException($"Gofile servers endpoint failed: {json}");

        var servers = json["data"]?["servers"] as JArray;
        var server = servers?.FirstOrDefault()?["name"]?.ToString()
            ?? throw new InvalidOperationException("No Gofile servers returned");

        _logger.LogDebug("Using Gofile upload server: {Server}", server);
        return server;
    }
}
