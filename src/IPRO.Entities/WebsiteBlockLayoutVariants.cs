namespace IPRO.Entities;

public static class WebsiteBlockLayoutVariants
{
    public static readonly string[] Services = { "cards", "list", "icons" };
    public static readonly string[] Testimonials = { "grid", "featured", "list" };
    public static readonly string[] CallToAction = { "banner", "card", "split" };

    public static string[] AllowedFor(string blockType) => blockType switch
    {
        WebsiteBlockTypes.Services => Services,
        WebsiteBlockTypes.Testimonials => Testimonials,
        WebsiteBlockTypes.CallToAction => CallToAction,
        _ => Array.Empty<string>()
    };

    public static string Normalize(string blockType, string? variant)
    {
        var allowed = AllowedFor(blockType);
        if (allowed.Length == 0) return string.Empty;
        var value = variant?.Trim().ToLowerInvariant() ?? string.Empty;
        return allowed.Contains(value) ? value : string.Empty;
    }
}
