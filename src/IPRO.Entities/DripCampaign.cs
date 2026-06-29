namespace IPRO.Entities;

public class DripCampaign
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser AgentUser { get; set; } = null!;
    public ICollection<DripCampaignStep> Steps { get; set; } = new List<DripCampaignStep>();
}
