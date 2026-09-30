namespace IPRO.Entities;

public static class WebsiteBlockLayoutVariants
{
    public static readonly string[] Services = { "cards", "list", "icons" };
    public static readonly string[] CallToAction = { "banner", "card", "split" };
    public static readonly string[] Text = { "image-left", "image-right" };
    public static readonly string[] Reviews = { "badge", "banner" };
    public static readonly string[] TestimonialForm = { "list", "grid" };
    public static readonly string[] Maps = { "full", "narrow" };
    // 535: masonry keeps each photo's own shape; slideshow shows one large photo at a time.
    public static readonly string[] Gallery = { "grid", "carousel", "masonry", "slideshow" };

    public static string[] AllowedFor(string blockType) => blockType switch
    {
        WebsiteBlockTypes.Services => Services,
        WebsiteBlockTypes.CallToAction => CallToAction,
        WebsiteBlockTypes.Text => Text,
        WebsiteBlockTypes.Reviews => Reviews,
        WebsiteBlockTypes.TestimonialForm => TestimonialForm,
        WebsiteBlockTypes.Maps => Maps,
        WebsiteBlockTypes.Gallery => Gallery,
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
