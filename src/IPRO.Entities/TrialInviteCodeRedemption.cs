namespace IPRO.Entities;

public class TrialInviteCodeRedemption
{
    public int Id { get; set; }
    public int TrialInviteCodeId { get; set; }
    public int AgentUserId { get; set; }
    public DateTime RedeemedAt { get; set; } = DateTime.UtcNow;

    public TrialInviteCode TrialInviteCode { get; set; } = null!;
    public AgentUser AgentUser { get; set; } = null!;
}
