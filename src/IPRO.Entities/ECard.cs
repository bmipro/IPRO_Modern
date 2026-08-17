namespace IPRO.Entities;

public static class ECardStatuses
{
    public const string Draft = "Draft";
    public const string Scheduled = "Scheduled";
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}

public class ECard
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    // Holds an ECardTemplateCatalog key (e.g. "halloween-3"). Column kept as `Occasion` from the
    // original schema so no migration is needed; the catalog maps the key to its occasion.
    public string Occasion { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = ECardStatuses.Draft;
    public DateTime ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int TotalRecipients { get; set; }
    public int TotalSent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

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

    public AgentUser AgentUser { get; set; } = null!;
    public ICollection<ECardRecipient> Recipients { get; set; } = new List<ECardRecipient>();
}
