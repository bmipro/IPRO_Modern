using IPRO.Entities;

namespace IPRO.Web.Models;

public sealed record WebsitePageStarterPreset(string Key, string Name, string Description);

public static class WebsitePageStarterPresetCatalog
{
    public static readonly IReadOnlyList<WebsitePageStarterPreset> All = new[]
    {
        new WebsitePageStarterPreset("blank", "Blank page", "Start with one editable section."),
        new WebsitePageStarterPreset("about", "About page", "An introduction, trust builder, and clear next step."),
        new WebsitePageStarterPreset("services", "Services page", "A service overview with a clear call to action."),
        new WebsitePageStarterPreset("contact", "Contact page", "A straightforward contact page for new enquiries."),
        new WebsitePageStarterPreset("landing", "Landing page", "A focused marketing page with services and social proof.")
    };

    public static bool IsKnown(string? key)
    {
        return All.Any(preset => string.Equals(preset.Key, key, StringComparison.OrdinalIgnoreCase));
    }
}

public class WebsitePageEditViewModel
{
    public WebsitePage Page { get; set; } = new();
    public List<WebsitePage> AvailableParents { get; set; } = new();
    public List<WebsiteMediaAsset> MediaAssets { get; set; } = new();
    public IReadOnlyList<WebsiteStarterBanner> StarterBanners { get; set; } = WebsiteStarterBannerCatalog.All;
    public IReadOnlyList<string> BlockTypes { get; set; } = WebsiteBlockTypes.All;
    public IReadOnlyList<WebsitePageStarterPreset> StarterPresets { get; set; } = WebsitePageStarterPresetCatalog.All;
}
