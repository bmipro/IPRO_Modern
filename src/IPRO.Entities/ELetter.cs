namespace IPRO.Entities;

// A starting-point letter managed by SuperAdmin. Unlike a card design this is only ever a seed:
// ELetter copies Subject and Body at create time and the agent edits them, so editing a template
// here changes what the next agent starts from and never rewrites a letter already sent.
public class ELetterTemplate
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class ELetterStatuses
{
    public const string Draft = "Draft";
    public const string Scheduled = "Scheduled";
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}

public class ELetter
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string TemplateKey { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Status { get; set; } = ELetterStatuses.Draft;
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
    public DateTime? ClaimedAt { get; set; }

    // Bounds retries, and guarantees the claim UPDATE always changes at least one column -- Pomelo
    // pins MySqlConnector with UseAffectedRows on, so a SET that changes nothing reports 0 rows and
    // the claim would silently fail on a stale reclaim where Status is already Sending.
    public int ClaimAttempts { get; set; }

    public AgentUser AgentUser { get; set; } = null!;
    public ICollection<ELetterRecipient> Recipients { get; set; } = new List<ELetterRecipient>();
}
