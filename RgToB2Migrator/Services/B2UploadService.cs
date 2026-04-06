using Amazon.S3;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RgToB2Migrator.Configuration;

namespace RgToB2Migrator.Services;

public class B2UploadService
{
    private readonly B2Settings _settings;
    private readonly ILogger<B2UploadService> _logger;

    public B2UploadService(IOptions<B2Settings> settings, ILogger<B2UploadService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Uploads a file to Backblaze B2 using the S3 compatible API.
    /// Returns the object key after successful upload.
    /// </summary>
    public async Task<string> UploadFileAsync(string filePath, string objectKey, CancellationToken ct = default)
    {
        _logger.LogInformation("Uploading {FilePath} to B2 Bucket {Bucket} as {ObjectKey}", filePath, _settings.BucketName, objectKey);

        var config = new AmazonS3Config
        {
            ServiceURL = _settings.ServiceUrl,
            AuthenticationRegion = _settings.Region, 
        };

        using var s3Client = new AmazonS3Client(_settings.ApplicationKeyId, _settings.ApplicationKey, config);
        using var transferUtility = new TransferUtility(s3Client);

        var request = new TransferUtilityUploadRequest
        {
            BucketName = _settings.BucketName,
            FilePath = filePath,
            Key = objectKey,
            PartSize = 10 * 1024 * 1024 // 10 MB parts
        };

        request.UploadProgressEvent += (sender, e) =>
        {
            if (e.PercentDone % 10 == 0)
            {
                _logger.LogDebug("Upload progress {ObjectKey}: {PercentDone}%", objectKey, e.PercentDone);
            }
        };

        try
        {
            await transferUtility.UploadAsync(request, ct);
            _logger.LogInformation("Upload completed for {ObjectKey}", objectKey);
            return objectKey;
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogError(ex, "S3 Upload failed for {FilePath}", objectKey);
            throw;
        }
    }
}
