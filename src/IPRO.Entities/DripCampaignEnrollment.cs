namespace IPRO.Entities;

public enum DripCampaignEnrollmentStatus
{
    Active,
    Completed,
    Cancelled,
    Failed
}

public class DripCampaignEnrollment
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int DripCampaignId { get; set; }
    public int ClientId { get; set; }
    public int? ClientCategoryId { get; set; }
    public DripCampaignEnrollmentStatus Status { get; set; } = DripCampaignEnrollmentStatus.Active;
    public int NextStepIndex { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime NextSendAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? LastSentAt { get; set; }
    public string LastError { get; set; } = string.Empty;
    public string UnsubscribeToken { get; set; } = string.Empty;

    public AgentUser AgentUser { get; set; } = null!;
    public DripCampaign DripCampaign { get; set; } = null!;
    public Client Client { get; set; } = null!;
    public ClientCategory? ClientCategory { get; set; }
}
