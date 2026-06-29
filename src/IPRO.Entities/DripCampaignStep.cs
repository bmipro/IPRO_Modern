namespace IPRO.Entities;

public class DripCampaignStep
{
    public int Id { get; set; }
    public int DripCampaignId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public int DelayDays { get; set; }
    public int SortOrder { get; set; }
    public DripCampaign DripCampaign { get; set; } = null!;
}
