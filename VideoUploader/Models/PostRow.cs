namespace VideoUploader.Models;

public class PostRow
{
    public Guid Id { get; set; }
    public List<string> OurDownloadLink { get; set; } = new();
}
