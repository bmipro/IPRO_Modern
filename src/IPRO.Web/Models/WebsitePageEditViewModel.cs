using IPRO.Entities;

namespace IPRO.Web.Models;

public class WebsitePageEditViewModel
{
    public WebsitePage Page { get; set; } = new();
    public List<WebsitePage> AvailableParents { get; set; } = new();
    public List<WebsiteMediaAsset> MediaAssets { get; set; } = new();
    public IReadOnlyList<string> BlockTypes { get; set; } = WebsiteBlockTypes.All;
}
