namespace RapidgatorProxy.Api.Configuration;

public class RapidgatorSettings
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://rapidgator.net/api";
    public int MaxConcurrentDownloads { get; set; } = 5;
}

public class ProxySettings
{
    public string Address { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class CacheSettings
{
    public string Directory { get; set; } = "/var/cache/rg-downloads";
    public int MaxSizeGB { get; set; } = 20;
    public int FileExpiryHours { get; set; } = 24;
    public int CleanupIntervalMinutes { get; set; } = 30;
    public long MaxSizeBytes => (long)MaxSizeGB * 1024 * 1024 * 1024;
}

public class SupabaseSettings
{
    public string Url { get; set; } = "";
    public string Key { get; set; } = "";
    public string ServiceKey { get; set; } = "";
}

public class CorsSettings
{
    public string[] AllowedOrigins { get; set; } = [];
}
