namespace RgToB2Migrator.Models;

public class MigrationPostsRow
{
    public Guid Id { get; set; }
    public List<string> OriginalRapidgatorUrls { get; set; } = new();
    public List<string> OurDownloadLink { get; set; } = new();
    public string DownloadStatus { get; set; } = "pending";
}

public class MigrationAsianScandalRow
{
    public Guid Id { get; set; }
    public List<string> OriginalRapidgatorUrls { get; set; } = new();
    public List<string> OurDownloadLink { get; set; } = new();
    public string DownloadStatus { get; set; } = "pending";
}
