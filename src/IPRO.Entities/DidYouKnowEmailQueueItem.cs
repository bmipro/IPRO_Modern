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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
