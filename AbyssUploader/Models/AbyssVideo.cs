using Newtonsoft.Json;

namespace AbyssUploader.Models;

public class AbyssVideo
{
    [JsonProperty("slug")]
    public string Slug { get; set; } = "";

    [JsonProperty("filename")]
    public string Filename { get; set; } = "";
}
