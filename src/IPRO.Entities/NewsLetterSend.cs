namespace IPRO.Entities;

public enum NewsLetterSendStatus
{
    Scheduled,
    Sending,
    Sent,
    Cancelled,
    Failed
}

public enum NewsLetterAudienceType
{
    AllSubscribers,
    AccountType,
    SelectedClients,
    IndividualClient
}

public class NewsLetterSend
{
    public int Id { get; set; }
    public int NewsLetterId { get; set; }
    public int AgentUserId { get; set; }
    public NewsLetterAudienceType AudienceType { get; set; } = NewsLetterAudienceType.AllSubscribers;
    public string AudienceLabel { get; set; } = "All newsletter subscribers";
    public int? ClientCategoryId { get; set; }
    public int? ClientId { get; set; }
    public NewsLetterSendStatus Status { get; set; } = NewsLetterSendStatus.Scheduled;
    public DateTime ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int TotalRecipients { get; set; }
    public int TotalSent { get; set; }
    public int TotalOpened { get; set; }
    public int TotalClicked { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // -- atomic claim (see IPRO.DataAccess.SendClaims) --
    //
    // Status alone cannot arbitrate a race between two dispatch runs: both can read Scheduled, both
    // write Sending, and both mail the entire list. ClaimedAt is stamped in the SAME conditional
    // UPDATE that flips the status, so the loser's UPDATE matches nothing. It is also the staleness
    // marker -- a claim older than SendClaims.ClaimTimeout means the process died mid-send and the
    // work may be picked up again.
    //
    // Cleared to NULL on every terminal path, so a finished row can never look like a live claim.
    // Kept separate from Status for the same reason DidYouKnowEmailQueueItem.ClaimedAtUtc is:
    // "who owns this right now" and "what happened to it" are different questions.
    public DateTime? ClaimedAt { get; set; }

    // Bounds retries, and guarantees the claim UPDATE always changes at least one column -- Pomelo
    // pins MySqlConnector with UseAffectedRows on, so a SET that changes nothing reports 0 rows and
    // the claim would silently fail on a stale reclaim where Status is already Sending.
    public int ClaimAttempts { get; set; }

    public NewsLetter NewsLetter { get; set; } = null!;
    public AgentUser AgentUser { get; set; } = null!;
    public ClientCategory? ClientCategory { get; set; }
    public Client? Client { get; set; }
    public ICollection<NewsLetterRecipient> Recipients { get; set; } = new List<NewsLetterRecipient>();
}
