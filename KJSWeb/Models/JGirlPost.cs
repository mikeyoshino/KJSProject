using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace KJSWeb.Models;

[Table("jgirl_posts")]
public class JGirlPost : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("source")]
    public string Source { get; set; } = "";

    [Column("source_url")]
    public string SourceUrl { get; set; } = "";

    [Column("title")]
    public string Title { get; set; } = "";

    [Column("thumbnail_url")]
    public string ThumbnailUrl { get; set; } = "";

    [Column("images")]
    public List<string> Images { get; set; } = new();

    [Column("tags")]
    public List<string> Tags { get; set; } = new();

    [Column("original_download_links")]
    public List<string> OriginalDownloadLinks { get; set; } = new();

    [Column("download_links")]
    public List<string> DownloadLinks { get; set; } = new();

    [Column("download_status")]
    public string DownloadStatus { get; set; } = "";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("scraped_at")]
    public DateTime? ScrapedAt { get; set; }

    [Column("post_images")]
    public List<string> PostImages { get; set; } = new();

    [Column("status")]
    public string StatusRaw { get; set; } = "published";

    [System.Text.Json.Serialization.JsonIgnore]
    public PostStatus Status =>
        Enum.TryParse<PostStatus>(StatusRaw, ignoreCase: true, out var v) ? v : PostStatus.Published;
}
