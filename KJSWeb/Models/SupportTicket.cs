using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace KJSWeb.Models;

[Table("support_tickets")]
public class SupportTicket : BaseModel
{
    [PrimaryKey("id", false)]
    public string Id { get; set; } = string.Empty;

    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    [Column("user_email")]
    public string UserEmail { get; set; } = string.Empty;

    [Column("title")]
    public string Title { get; set; } = string.Empty;

    [Column("category")]
    public string CategoryRaw { get; set; } = "Other";

    [System.Text.Json.Serialization.JsonIgnore]
    public TicketCategory Category => ParseCategory(CategoryRaw);

    [Column("status")]
    public string StatusRaw { get; set; } = "Open";

    [System.Text.Json.Serialization.JsonIgnore]
    public TicketStatus Status => ParseStatus(StatusRaw);

    private static TicketStatus ParseStatus(string statusRaw)
    {
        if (string.IsNullOrEmpty(statusRaw)) return TicketStatus.Open;

        return statusRaw.ToLower() switch
        {
            "open" => TicketStatus.Open,
            "in_progress" or "inprogress" => TicketStatus.InProgress,
            "resolved" => TicketStatus.Resolved,
            _ => TicketStatus.Open
        };
    }

    private static TicketCategory ParseCategory(string categoryRaw)
    {
        if (string.IsNullOrEmpty(categoryRaw)) return TicketCategory.Other;

        return categoryRaw.ToLower() switch
        {
            "payment" => TicketCategory.Payment,
            "download" => TicketCategory.Download,
            "account" => TicketCategory.Account,
            "other" => TicketCategory.Other,
            _ => TicketCategory.Other
        };
    }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [Column("last_reply_at")]
    public DateTime? LastReplyAt { get; set; }
}
