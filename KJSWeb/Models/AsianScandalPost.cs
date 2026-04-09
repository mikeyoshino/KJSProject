using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System.Collections.Generic;

namespace KJSWeb.Models;

[Table("asianscandal_posts")]
public class AsianScandalPost : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("title")]
    public string Title { get; set; } = string.Empty;

    [Column("thumbnail_url")]
    public string ThumbnailUrl { get; set; } = string.Empty;

    [Column("content_html")]
    public string ContentHtml { get; set; } = string.Empty;

    [Column("source_url")]
    public string SourceUrl { get; set; } = string.Empty;

    [Column("categories")]
    public List<string> Categories { get; set; } = new();

    [Column("tags")]
    public List<string> Tags { get; set; } = new();

    [Column("original_rapidgator_url")]
    public List<string> OriginalRapidgatorUrls { get; set; } = new();

    [Column("our_download_link")]
    public List<string> OurDownloadLink { get; set; } = new();

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("view_count")]
    public long ViewCount { get; set; } = 0;

    [Column("status")]
    public string StatusRaw { get; set; } = "published";

    [System.Text.Json.Serialization.JsonIgnore]
    public PostStatus Status =>
        Enum.TryParse<PostStatus>(StatusRaw, ignoreCase: true, out var v) ? v : PostStatus.Published;
}
