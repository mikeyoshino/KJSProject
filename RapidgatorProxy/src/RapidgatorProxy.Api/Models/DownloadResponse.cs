namespace RapidgatorProxy.Api.Models;

public class DownloadResponse
{
    public string DownloadId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? FileName { get; set; }
    public long? FileSize { get; set; }
    public double? Progress { get; set; }
    public long? DownloadedBytes { get; set; }
    public int? EstimatedSecondsRemaining { get; set; }
    public string? Error { get; set; }
}
