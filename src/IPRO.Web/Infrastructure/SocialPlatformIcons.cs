namespace IPRO.Web.Infrastructure;

// 465 (2026-09-08): the one map from a footer social-link platform to its icon and its name. The
// footer and the Social links block both draw from it, so adding a platform is one edit.
// Platforms are the WebsiteFooterSettings.KnownPlatforms set: facebook, linkedin, instagram,
// twitter-x, youtube, other. Brand icons are Font Awesome "fab"; "other" is a plain link icon.
public static class SocialPlatformIcons
{
    private static readonly Dictionary<string, (string Icon, string Label)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["facebook"] = ("fab fa-facebook", "Facebook"),
        ["linkedin"] = ("fab fa-linkedin", "LinkedIn"),
        ["instagram"] = ("fab fa-instagram", "Instagram"),
        ["twitter-x"] = ("fab fa-x-twitter", "X"),
        ["youtube"] = ("fab fa-youtube", "YouTube"),
        ["other"] = ("fas fa-link", "Website"),
    };

    public static string IconClass(string? platform) =>
        Map.TryGetValue((platform ?? string.Empty).Trim(), out var v) ? v.Icon : Map["other"].Icon;

    public static string Label(string? platform) =>
        Map.TryGetValue((platform ?? string.Empty).Trim(), out var v) ? v.Label : Map["other"].Label;
}
