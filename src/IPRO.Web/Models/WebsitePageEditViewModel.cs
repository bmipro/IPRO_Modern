using IPRO.Entities;

namespace IPRO.Web.Models;

public class WebsitePageEditViewModel
{
    public WebsitePage Page { get; set; } = new();
    public List<WebsitePage> AvailableParents { get; set; } = new();
    public List<WebsiteMediaAsset> MediaAssets { get; set; } = new();
    public IReadOnlyList<WebsiteStarterBanner> StarterBanners { get; set; } = WebsiteStarterBannerCatalog.All;
    public IReadOnlyList<string> BlockTypes { get; set; } = WebsiteBlockTypes.All;
}
