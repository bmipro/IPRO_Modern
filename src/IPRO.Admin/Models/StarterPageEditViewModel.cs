using IPRO.Entities;

namespace IPRO.Admin.Models;

public class StarterPageEditViewModel
{
    public WebsiteStarterPage Page { get; set; } = new();
    public List<BillingRule> Packages { get; set; } = new();
    public IReadOnlyList<string> BlockTypes { get; set; } = WebsiteBlockTypes.All;

    // 470: the starter articles a Did You Know block on this page may show (this business type or "All").
    public List<WebsiteStarterArticle> StarterArticles { get; set; } = new();
}
