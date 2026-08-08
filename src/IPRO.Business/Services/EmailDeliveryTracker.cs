using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Business.Services;

// Records SendGrid delivery events for every sender EXCEPT newsletters and drip campaigns, which
// already had their own recorder on NewsLetterService.
//
// Why this exists: E-Cards, E-Letters and Polls all tagged their sends with a recipient id in
// customArgs from the day they were written, but NewsletterController.SendGridEvents only ever
// looked for newsletter_recipient_id. Every delivered/open/click/bounce event for a card, letter or
// poll arrived at the webhook, matched nothing, and was dropped. That is why the "Delivered" column
// on the SuperAdmin Card & Letter Activity screen was blank for its entire existence -- not a
// rendering bug, just nothing ever written.
//
// One tracker rather than one per entity, so the event-name mapping cannot drift between senders.
public interface IEmailDeliveryTracker
{
    // entityKind matches the "ipro_entity" custom arg: ecard | eletter | poll | didyouknow.
    Task RecordAsync(string entityKind, int recipientId, string eventName, string? providerMessageId, string? reason, DateTime occurredAt);
}

public class EmailDeliveryTracker : IEmailDeliveryTracker
{
    private readonly IPRODbContext _db;
    private readonly ILogger<EmailDeliveryTracker> _logger;

    public EmailDeliveryTracker(IPRODbContext db, ILogger<EmailDeliveryTracker> logger)
    {
        _db = db;
        _logger = logger;
    }

    // The subset of SendGrid events worth persisting, normalized away from SendGrid's inconsistent
    // naming (it emits "open"/"click" singular but "delivered"/"processed" past-tense).
    private enum Outcome { Ignored, Sent, Delivered, Opened, Clicked, Bounced, Failed }

    private static Outcome Map(string? eventName) => (eventName ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "processed" or "sent" => Outcome.Sent,
        "delivered" => Outcome.Delivered,
        "open" or "opened" => Outcome.Opened,
        "click" or "clicked" => Outcome.Clicked,
        "bounce" or "blocked" => Outcome.Bounced,
        "dropped" or "deferred" or "spamreport" => Outcome.Failed,
        _ => Outcome.Ignored
    };

    // A bounce or a drop is terminal: a later "processed" event for the same message must not
    // overwrite it back to a healthy-looking state. Events can and do arrive out of order.
    private static bool IsTerminal(Outcome current) => current is Outcome.Bounced or Outcome.Failed;

    public async Task RecordAsync(string entityKind, int recipientId, string eventName, string? providerMessageId, string? reason, DateTime occurredAt)
    {
        var outcome = Map(eventName);
        if (outcome == Outcome.Ignored) return;

        var normalized = (eventName ?? string.Empty).Trim().ToLowerInvariant();

        switch ((entityKind ?? string.Empty).ToLowerInvariant())
        {
            case "ecard":
                await RecordECardAsync(recipientId, outcome, normalized, providerMessageId, reason, occurredAt);
                break;
            case "eletter":
                await RecordELetterAsync(recipientId, outcome, normalized, providerMessageId, reason, occurredAt);
                break;
            case "poll":
                await RecordPollAsync(recipientId, outcome, normalized, providerMessageId, reason, occurredAt);
                break;
            case "didyouknow":
                await RecordDidYouKnowAsync(recipientId, outcome, normalized, providerMessageId, reason, occurredAt);
                break;
            default:
                _logger.LogWarning("Unrecognised email entity kind '{EntityKind}' in SendGrid event; ignoring.", entityKind);
                break;
        }
    }

    private async Task RecordECardAsync(int id, Outcome outcome, string normalized, string? messageId, string? reason, DateTime at)
    {
        var recipient = await _db.ECardRecipients.FirstOrDefaultAsync(r => r.Id == id);
        if (recipient == null) return;

        recipient.LastEvent = normalized;
        recipient.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(messageId)) recipient.SendGridMessageId = messageId;

        var alreadyTerminal = recipient.Status == ECardRecipientStatuses.Failed;
        ApplyTimestamps(outcome, at, t => recipient.SentAt ??= t, t => recipient.DeliveredAt ??= t,
            t => recipient.OpenedAt ??= t, t => recipient.ClickedAt ??= t, t => recipient.BouncedAt ??= t);

        if (!alreadyTerminal && IsTerminal(outcome))
        {
            recipient.Status = ECardRecipientStatuses.Failed;
            recipient.FailureReason = reason ?? string.Empty;
        }
        else if (!alreadyTerminal && outcome != Outcome.Ignored)
        {
            recipient.Status = ECardRecipientStatuses.Sent;
        }

        await _db.SaveChangesAsync();
    }

    private async Task RecordELetterAsync(int id, Outcome outcome, string normalized, string? messageId, string? reason, DateTime at)
    {
        var recipient = await _db.ELetterRecipients.FirstOrDefaultAsync(r => r.Id == id);
        if (recipient == null) return;

        recipient.LastEvent = normalized;
        recipient.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(messageId)) recipient.SendGridMessageId = messageId;

        var alreadyTerminal = recipient.Status == ELetterRecipientStatuses.Failed;
        ApplyTimestamps(outcome, at, t => recipient.SentAt ??= t, t => recipient.DeliveredAt ??= t,
            t => recipient.OpenedAt ??= t, t => recipient.ClickedAt ??= t, t => recipient.BouncedAt ??= t);

        if (!alreadyTerminal && IsTerminal(outcome))
        {
            recipient.Status = ELetterRecipientStatuses.Failed;
            recipient.FailureReason = reason ?? string.Empty;
        }
        else if (!alreadyTerminal && outcome != Outcome.Ignored)
        {
            recipient.Status = ELetterRecipientStatuses.Sent;
        }

        await _db.SaveChangesAsync();
    }

    private async Task RecordPollAsync(int id, Outcome outcome, string normalized, string? messageId, string? reason, DateTime at)
    {
        var recipient = await _db.PollRecipients.FirstOrDefaultAsync(r => r.Id == id);
        if (recipient == null) return;

        recipient.LastEvent = normalized;
        recipient.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(messageId)) recipient.SendGridMessageId = messageId;

        ApplyTimestamps(outcome, at, t => recipient.SentAt ??= t, t => recipient.DeliveredAt ??= t,
            t => recipient.OpenedAt ??= t, t => recipient.ClickedAt ??= t, t => recipient.BouncedAt ??= t);

        // Responded is a stronger fact than any delivery event -- the client actually voted -- so a
        // late-arriving "delivered" must never demote it.
        var alreadyTerminal = recipient.Status is PollRecipientStatus.Failed or PollRecipientStatus.Responded;
        if (!alreadyTerminal && IsTerminal(outcome))
        {
            recipient.Status = PollRecipientStatus.Failed;
            recipient.FailedAt ??= at;
            recipient.FailureReason = reason ?? string.Empty;
        }
        else if (!alreadyTerminal)
        {
            recipient.Status = PollRecipientStatus.Sent;
        }

        await _db.SaveChangesAsync();
    }

    private async Task RecordDidYouKnowAsync(int id, Outcome outcome, string normalized, string? messageId, string? reason, DateTime at)
    {
        var item = await _db.DidYouKnowEmailQueueItems.FirstOrDefaultAsync(q => q.Id == id);
        if (item == null) return;

        item.LastEvent = normalized;
        if (!string.IsNullOrWhiteSpace(messageId)) item.SendGridMessageId = messageId;

        ApplyTimestamps(outcome, at, t => item.SentAtUtc ??= t, t => item.DeliveredAt ??= t,
            t => item.OpenedAt ??= t, t => item.ClickedAt ??= t, t => item.BouncedAt ??= t);

        var alreadyTerminal = item.Status == DidYouKnowQueueStatuses.Failed;
        if (!alreadyTerminal && IsTerminal(outcome))
        {
            item.Status = DidYouKnowQueueStatuses.Failed;
            item.FailureReason = reason ?? string.Empty;
        }
        else if (!alreadyTerminal)
        {
            item.Status = DidYouKnowQueueStatuses.Sent;
        }

        await _db.SaveChangesAsync();
    }

    // Timestamps are write-once (??=): the FIRST time an event type is seen is the interesting one.
    // SendGrid re-sends events on its own retry schedule and a client opening a card five times
    // should not keep moving OpenedAt forward.
    private static void ApplyTimestamps(
        Outcome outcome, DateTime at,
        Action<DateTime> sent, Action<DateTime> delivered,
        Action<DateTime> opened, Action<DateTime> clicked, Action<DateTime> bounced)
    {
        switch (outcome)
        {
            case Outcome.Sent: sent(at); break;
            // An open or a click proves delivery even if the delivered event itself never arrived
            // or arrived out of order, so backfill the earlier milestones rather than leaving gaps
            // that read as "opened but never delivered".
            case Outcome.Delivered: sent(at); delivered(at); break;
            case Outcome.Opened: sent(at); delivered(at); opened(at); break;
            case Outcome.Clicked: sent(at); delivered(at); opened(at); clicked(at); break;
            case Outcome.Bounced: bounced(at); break;
        }
    }
}
