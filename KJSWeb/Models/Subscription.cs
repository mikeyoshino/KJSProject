using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace KJSWeb.Models;

[Table("subscriptions")]
public class Subscription : BaseModel
{
    [PrimaryKey("id", false)]
    [Column("id")]
    public string Id { get; set; } = "";

    [Column("user_id")]
    public string UserId { get; set; } = "";

    [Column("plan")]
    public string Plan { get; set; } = "";  // "monthly", "3month", "yearly"

    [Column("amount_usd")]
    public decimal AmountUsd { get; set; }

    [Column("amount_btc")]
    public decimal? AmountBtc { get; set; }

    [Column("btc_address")]
    public string BtcAddress { get; set; } = "";

    [Column("txid")]
    public string? Txid { get; set; }

    [Column("status")]
    public string Status { get; set; } = "pending"; // "pending", "confirming", "active", "expired"

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("paid_at")]
    public DateTime? PaidAt { get; set; }

    [Column("expires_at")]
    public DateTime? ExpiresAt { get; set; }
}
