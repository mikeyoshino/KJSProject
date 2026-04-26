using System.Text.Json.Serialization;

namespace KJSWeb.Models;

public class AbyssVideo
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";
}
