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
    // 554: what a search result shows for this page as things stand (PublicSeoText): the grey text in
    // the two search boxes when the owner has written nothing of their own. Empty for a page not saved yet.
    public string SearchTitle { get; set; } = string.Empty;
    public string SearchDescription { get; set; } = string.Empty;
    public List<WebsitePage> AvailableParents { get; set; } = new();
    public List<WebsiteMediaAsset> MediaAssets { get; set; } = new();
    public List<PollSurvey> AvailableSentPolls { get; set; } = new();
    public List<PollSurvey> AvailableVotePolls { get; set; } = new();
    public List<AgentDocument> AvailableAgentDocuments { get; set; } = new();
    public List<WebsiteForm> AvailableForms { get; set; } = new();
    public List<Article> AvailableArticles { get; set; } = new();
    public IReadOnlyList<WebsiteStarterBanner> StarterBanners { get; set; } = WebsiteStarterBannerCatalog.All;
    public IReadOnlyList<string> BlockTypes { get; set; } = WebsiteBlockTypes.All;
    public IReadOnlyList<WebsitePageStarterPreset> StarterPresets { get; set; } = WebsitePageStarterPresetCatalog.All;
}
