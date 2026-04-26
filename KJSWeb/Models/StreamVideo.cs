using System.Text.Json.Serialization;

namespace KJSWeb.Models;

public class StreamVideo
{
    [JsonPropertyName("video_id")]
    public string VideoId { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";
}
