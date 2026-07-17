namespace IPRO.Entities;

public class DripCampaignStepSend
{
    public int Id { get; set; }
    public int DripCampaignEnrollmentId { get; set; }
    public int DripCampaignStepId { get; set; }
    public int StepIndex { get; set; }
    public string Email { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public NewsLetterRecipientStatus Status { get; set; } = NewsLetterRecipientStatus.Queued;
    public string SendGridMessageId { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? ClickedAt { get; set; }
    public DateTime? BouncedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DripCampaignEnrollment DripCampaignEnrollment { get; set; } = null!;
    public DripCampaignStep DripCampaignStep { get; set; } = null!;
}
