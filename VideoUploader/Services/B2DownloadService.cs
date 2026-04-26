using VideoUploader.Configuration;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VideoUploader.Services;

public class B2DownloadService
{
    private readonly B2Settings _b2;
    private readonly ILogger<B2DownloadService> _logger;

    public B2DownloadService(IOptions<B2Settings> settings, ILogger<B2DownloadService> logger)
    {
        _b2 = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Downloads a B2 public URL to a local file path using the S3 SDK.
    /// Public URL format: {PublicBaseUrl}/{objectKey}
    /// e.g. https://f005.backblazeb2.com/file/KJSProject/posts/abc/file.zip
    /// </summary>
    public async Task<string> DownloadAsync(string b2Url, string destPath, CancellationToken ct = default)
    {
        var objectKey = ExtractObjectKey(b2Url);
        _logger.LogInformation("Downloading B2 object: {Key} → {Dest}", objectKey, destPath);

        var config = new AmazonS3Config
        {
            ServiceURL = _b2.ServiceUrl,
            AuthenticationRegion = _b2.Region,
        };

        using var s3 = new AmazonS3Client(_b2.ApplicationKeyId, _b2.ApplicationKey, config);

        var request = new GetObjectRequest
        {
            BucketName = _b2.BucketName,
            Key = objectKey
        };

        using var response = await s3.GetObjectAsync(request, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        await using var fs = File.Create(destPath);
        await response.ResponseStream.CopyToAsync(fs, ct);

        _logger.LogInformation("Downloaded {Bytes} bytes to {Dest}", fs.Length, destPath);
        return destPath;
    }

    private string ExtractObjectKey(string b2Url)
    {
        // Plain path stored in DB (no scheme) — use as-is
        if (!b2Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !b2Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return b2Url.TrimStart('/');

        var prefix = _b2.PublicBaseUrl.TrimEnd('/') + "/";
        if (b2Url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return b2Url[prefix.Length..];

        // Fallback: strip scheme+host+/file/{bucket}/
        var uri = new Uri(b2Url);
        var segments = uri.AbsolutePath.TrimStart('/').Split('/', 3);
        // segments[0]="file", segments[1]="KJSProject", segments[2]="rest/of/key"
        return segments.Length >= 3 ? segments[2] : uri.AbsolutePath.TrimStart('/');
    }
}
