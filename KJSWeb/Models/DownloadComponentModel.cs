namespace KJSWeb.Models;

public class DownloadComponentModel
{
    public int DownloadCount { get; set; }
    public List<string> DownloadUrls { get; set; } = new();
    public bool IsSubscribed { get; set; }
    public List<string>? PublicDownloadUrls { get; set; }
}
