using System.Text.RegularExpressions;
using System.Linq;

namespace KJSWeb.Helpers
{
    public static class SeoHelper
    {
        public static string ToSlug(string title)
        {
            if (string.IsNullOrEmpty(title))
                return "";
            
            // Convert to lowercase
            string slug = title.ToLowerInvariant();
            
            // Remove diacritics using native string normalization
            slug = new string(slug
                .Normalize(System.Text.NormalizationForm.FormD)
                .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                .ToArray())
                .Normalize(System.Text.NormalizationForm.FormC);

            // Replace non-alphanumeric chars with space
            slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
            
            // Convert multiple spaces into one space
            slug = Regex.Replace(slug, @"\s+", " ").Trim();
            
            // Replace spaces with hyphens
            slug = Regex.Replace(slug, @"\s", "-");
            
            return slug;
        }
    }
}
