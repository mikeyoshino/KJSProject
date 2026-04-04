using System.ComponentModel.DataAnnotations;

namespace RapidgatorProxy.Api.Models;

public class DownloadEntry
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RapidgatorUrl { get; set; } = "";
    public string CachedFileName { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public long FileSize { get; set; }
    public long DownloadedBytes { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
    public string? ErrorMessage { get; set; }
    public string? RequestedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastAccessedAt { get; set; }
}
