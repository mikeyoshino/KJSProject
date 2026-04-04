using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System.Collections.Generic;

namespace KJSWeb.Models;

[Table("posts")]
public class Post : BaseModel
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

    [Column("original_rapidgator_url")]
    public List<string> OriginalRapidgatorUrls { get; set; } = new();

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
