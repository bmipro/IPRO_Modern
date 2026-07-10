using IPRO.Entities;

namespace IPRO.Web.Models;

public class CampaignGroupSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int ClientCount { get; set; }
    public int SubscriberCount { get; set; }
}

public class CampaignIndexViewModel
{
    public List<CampaignGroupSummary> Groups { get; set; } = new();
    public List<DripCampaign> Campaigns { get; set; } = new();
}

public class CampaignDetailsViewModel
{
    public DripCampaign Campaign { get; set; } = null!;
    public List<DripCampaignStep> Steps { get; set; } = new();
    public List<DripCampaignEnrollment> Enrollments { get; set; } = new();
    public List<CampaignGroupSummary> Groups { get; set; } = new();
    public List<Client> Clients { get; set; } = new();
    public List<NewsLetter> Newsletters { get; set; } = new();
}
