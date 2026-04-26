using Newtonsoft.Json;

namespace KJSWeb.Models;

public class StreamVideo
{
    [JsonProperty("video_id")]
    public string VideoId { get; set; } = "";

    [JsonProperty("filename")]
    public string Filename { get; set; } = "";
}
