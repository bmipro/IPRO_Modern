namespace IPRO.Entities;

public class DidYouKnowEmailQueueItem
{
    public int Id { get; set; }
    public int ArticleId { get; set; }
    public int ClientId { get; set; }
    public DateTime ScheduledForUtc { get; set; }

    // Set when a dispatch run takes ownership, BEFORE the email is attempted. Separate from SentAtUtc
    // so the two states are distinguishable: "someone is working on this" is not the same as "this
    // was delivered". A claim that never reaches SentAtUtc means the process died mid-send, and the
    // item becomes eligible again after ClaimTimeout (see DidYouKnowEmailDispatchJob).
    //
    // The first version of this used SentAtUtc alone as the claim, which made a crash or an ordinary
    // deploy restart silently destroy that email with no record and no retry.
    public DateTime? ClaimedAtUtc { get; set; }

    public DateTime? SentAtUtc { get; set; }

    // Outcome + delivery tracking. Before 2026-08-08 a retired queue item recorded only SentAtUtc,
    // so a REJECTED send (bad address, rate limit) looked identical to a delivered one once the row
    // was retired -- the rejection existed solely as a log line. Status/FailureReason make the
    // outcome durable, and the rest is filled in by the SendGrid event webhook.
    public string Status { get; set; } = DidYouKnowQueueStatuses.Queued;
    public string SendGridMessageId { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public string LastEvent { get; set; } = string.Empty;
    public DateTime? DeliveredAt { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? ClickedAt { get; set; }
    public DateTime? BouncedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class DidYouKnowQueueStatuses
{
    public const string Queued = "Queued";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}
