namespace RgToB2Migrator.Configuration;

public class SupabaseSettings
{
    public string Url { get; set; } = "";
    public string ServiceKey { get; set; } = "";
}

public class RapidgatorSettings
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://rapidgator.net/api/v2";
    public int RequestDelayMs { get; set; } = 3000;
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

public class MigratorSettings
{
    public string TempFolder { get; set; } = "/tmp/rg-migrator";
    public int RateLimitDelayMs { get; set; } = 3000;
}
