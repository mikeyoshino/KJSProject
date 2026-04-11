using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace KJSWeb.Models;

[Table("support_tickets")]
public class SupportTicket : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    [Column("user_email")]
    public string UserEmail { get; set; } = string.Empty;

    [Column("title")]
    public string Title { get; set; } = string.Empty;

    [Column("category")]
    public string CategoryRaw { get; set; } = "Other";

    [System.Text.Json.Serialization.JsonIgnore]
    public TicketCategory Category =>
        Enum.TryParse<TicketCategory>(CategoryRaw, ignoreCase: true, out var v) ? v : TicketCategory.Other;

    [Column("status")]
    public string StatusRaw { get; set; } = "Open";

    [System.Text.Json.Serialization.JsonIgnore]
    public TicketStatus Status =>
        Enum.TryParse<TicketStatus>(StatusRaw, ignoreCase: true, out var v) ? v : TicketStatus.Open;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [Column("last_reply_at")]
    public DateTime? LastReplyAt { get; set; }
}
