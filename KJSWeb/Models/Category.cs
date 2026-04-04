using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace KJSWeb.Models;

[Table("unique_categories")]
public class Category : BaseModel
{
    [Column("name")]
    public string Name { get; set; } = string.Empty;
}
