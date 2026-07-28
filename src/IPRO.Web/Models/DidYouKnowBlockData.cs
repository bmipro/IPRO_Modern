namespace IPRO.Web.Models;

public class DidYouKnowBlockData
{
    public string CampaignName { get; set; } = string.Empty;
    public List<string> Teasers { get; set; } = new();
    public string LayoutStyle { get; set; } = "auto";
}
