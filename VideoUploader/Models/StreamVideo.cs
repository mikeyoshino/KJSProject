using Newtonsoft.Json;

namespace VideoUploader.Models;

public class StreamVideo
{
    [JsonProperty("video_id")]
    public string VideoId { get; set; } = "";

    [JsonProperty("filename")]
    public string Filename { get; set; } = "";
}
