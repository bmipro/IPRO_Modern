using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteFooterSettings
{
    public string CopyrightText { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public bool ShowDisclaimer { get; set; }
    public string DisclaimerText { get; set; } = string.Empty;
    public List<WebsiteSocialLink> SocialLinks { get; set; } = new();
    public List<WebsiteFooterLink> LegalLinks { get; set; } = new();

    public static readonly string[] KnownPlatforms = { "facebook", "linkedin", "instagram", "twitter-x", "youtube", "other" };

    public static WebsiteFooterSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<WebsiteFooterSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            value.CopyrightText = value.CopyrightText?.Trim() ?? string.Empty;
            value.Phone = value.Phone?.Trim() ?? string.Empty;
            value.Email = value.Email?.Trim() ?? string.Empty;
            value.Address = value.Address?.Trim() ?? string.Empty;
            value.DisclaimerText = value.DisclaimerText?.Trim() ?? string.Empty;
            value.SocialLinks ??= new();
            value.LegalLinks ??= new();
            foreach (var link in value.SocialLinks)
            {
                link.Platform = Normalize(link.Platform, KnownPlatforms, "other");
            }
            return value;
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    private static string Normalize(string? value, IEnumerable<string> allowed, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? normalized : fallback;
    }
}

public class WebsiteSocialLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Platform { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public class WebsiteFooterLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
