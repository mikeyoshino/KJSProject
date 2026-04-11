using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace KJSWeb.Models;

[Table("ticket_messages")]
public class TicketMessage : BaseModel
{
    [PrimaryKey("id", false)]
    public string Id { get; set; } = string.Empty;

    [Column("ticket_id")]
    public string TicketId { get; set; } = string.Empty;

    [Column("sender_id")]
    public string SenderId { get; set; } = string.Empty;

    [Column("is_admin")]
    public bool IsAdmin { get; set; } = false;

    [Column("message")]
    public string Message { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
