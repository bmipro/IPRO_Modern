using IPRO.Entities;

namespace IPRO.Admin.Models;

public class StarterPageEditViewModel
{
    public WebsiteStarterPage Page { get; set; } = new();
    public List<BillingRule> Packages { get; set; } = new();
    public IReadOnlyList<string> BlockTypes { get; set; } = WebsiteBlockTypes.All;
}
