namespace AbyssUploader.Configuration;

public class SupabaseSettings
{
    public string Url { get; set; } = "";
    public string ServiceKey { get; set; } = "";
}

public class B2Settings
{
    public string ApplicationKeyId { get; set; } = "";
    public string ApplicationKey { get; set; } = "";
    public string BucketName { get; set; } = "";
    public string Region { get; set; } = "us-east-005";
    public string ServiceUrl { get; set; } = "https://s3.us-east-005.backblazeb2.com";
    public string PublicBaseUrl { get; set; } = "";
}

public class AbyssSettings
{
    public string ApiKey { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "http://up.abyss.to";
    public double DailyLimitGb { get; set; } = 95;
    public int BatchSize { get; set; } = 50;
    public string TempFolder { get; set; } = "/tmp/abyss-uploader";
}
