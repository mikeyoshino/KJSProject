namespace KJSWeb.Models;

public static class SubscriptionPlans
{
    public static readonly Dictionary<string, (string Label, decimal PriceUsd, int DurationDays, string SaveText)> All = new()
    {
        ["monthly"] = ("1 Month",   5.00m,   30, ""),
        ["3month"]  = ("3 Months", 12.00m,   90, "Save 20%"),
        ["yearly"]  = ("1 Year",   40.00m,  365, "Save 33%"),
    };
}
