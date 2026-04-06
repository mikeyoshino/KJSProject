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

public class GofileSettings
{
    // Account API token — required for folder creation and permanent file retention.
    // Get from: https://gofile.io/myProfile
    public string Token { get; set; } = "";

    // Optional: upload all posts under a specific folder in your account.
    // Leave empty to upload to account root.
    public string ParentFolderId { get; set; } = "";
}

public class MigratorSettings
{
    public string TempFolder { get; set; } = "/tmp/rg-migrator";
    public int RateLimitDelayMs { get; set; } = 3000;
}
