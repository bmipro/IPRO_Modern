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
    private readonly IEmailConsentService _consent;

    public EmailDeliveryTracker(IPRODbContext db, ILogger<EmailDeliveryTracker> logger, IEmailConsentService consent)
    {
        _db = db;
        _logger = logger;
        _consent = consent;
    }

    // The subset of SendGrid events worth persisting, normalized away from SendGrid's inconsistent
    // naming (it emits "open"/"click" singular but "delivered"/"processed" past-tense).
    private enum Outcome { Ignored, Sent, Delivered, Opened, Clicked, Bounced, Failed, Unsubscribed }

    private static Outcome Map(string? eventName) => (eventName ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "processed" or "sent" => Outcome.Sent,
        "delivered" => Outcome.Delivered,
        "open" or "opened" => Outcome.Opened,
        "click" or "clicked" => Outcome.Clicked,
        "bounce" or "blocked" => Outcome.Bounced,
        "dropped" or "deferred" or "spamreport" => Outcome.Failed,
        // These used to fall through to Ignored and be discarded at the top of RecordAsync, so an
        // unsubscribe reported by SendGrid -- including the one-click header in every card, letter
        // and poll -- suppressed nothing on any of the four channels (JOBS-4).
        "unsubscribe" or "group_unsubscribe" => Outcome.Unsubscribed,
        _ => Outcome.Ignored
    };

    // A bounce or a drop is terminal: a later "processed" event for the same message must not
    // overwrite it back to a healthy-looking state. Events can and do arrive out of order.
    //
    // Unsubscribed is deliberately NOT terminal. Terminal means "the send failed", and it writes
    // Status = Failed plus a FailureReason. An unsubscribe means the opposite: the mail arrived, was
    // read, and the person acted on it. Marking those recipients Failed would silently corrupt the
    // Delivered column this class was written to populate. It suppresses future mail; it does not
    // rewrite the history of a delivery that succeeded.
    private static bool IsTerminal(Outcome current) => current is Outcome.Bounced or Outcome.Failed;

    public async Task RecordAsync(string entityKind, int recipientId, string eventName, string? providerMessageId, string? reason, DateTime occurredAt)
    {
        var outcome = Map(eventName);
        if (outcome == Outcome.Ignored) return;

        var normalized = (eventName ?? string.Empty).Trim().ToLowerInvariant();

        // Each recorder hands back the client the recipient row belongs to, so suppression can be
        // decided ONCE below instead of four times. A fifth sender added to this switch inherits
        // consent automatically; it cannot forget, which is precisely how the four existing ones
        // ended up mailing people who had complained.
        int? clientId;
        switch ((entityKind ?? string.Empty).ToLowerInvariant())
        {
            case "ecard":
                clientId = await RecordECardAsync(recipientId, outcome, normalized, providerMessageId, reason, occurredAt);
                break;
            case "eletter":
                clientId = await RecordELetterAsync(recipientId, outcome, normalized, providerMessageId, reason, occurredAt);
                break;
            case "poll":
                clientId = await RecordPollAsync(recipientId, outcome, normalized, providerMessageId, reason, occurredAt);
                break;
            case "didyouknow":
                clientId = await RecordDidYouKnowAsync(recipientId, outcome, normalized, providerMessageId, reason, occurredAt);
                break;
            case "invoice":
                clientId = await RecordInvoiceEmailAsync(recipientId, outcome, normalized, providerMessageId, reason, occurredAt);
                break;
            default:
                _logger.LogWarning("Unrecognised email entity kind '{EntityKind}' in SendGrid event; ignoring.", entityKind);
                return;
        }

        await SuppressIfRequestedAsync(clientId, outcome, normalized, entityKind);
    }

    // A spam complaint and an unsubscribe are the same instruction in law: stop mailing this person.
    // "dropped" and "deferred" also map to Outcome.Failed but are SendGrid's own delivery problems,
    // not a decision by the recipient, so the normalized name is checked rather than the outcome.
    private async Task SuppressIfRequestedAsync(int? clientId, Outcome outcome, string normalized, string? entityKind)
    {
        var recipientAsked = outcome == Outcome.Unsubscribed || (outcome == Outcome.Failed && normalized == "spamreport");
        if (!recipientAsked || !clientId.HasValue) return;

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId.Value);
        if (client == null) return;

        var result = await _consent.SuppressAllAsync(client, $"sendgrid:{normalized}:{entityKind}");
        if (!result.WasAlreadySuppressed)
        {
            _logger.LogInformation(
                "SendGrid '{Event}' on a {EntityKind} suppressed all email for client {ClientId}.",
                normalized, entityKind, client.Id);
        }
    }

    private async Task<int?> RecordECardAsync(int id, Outcome outcome, string normalized, string? messageId, string? reason, DateTime at)
    {
        var recipient = await _db.ECardRecipients.FirstOrDefaultAsync(r => r.Id == id);
        if (recipient == null) return null;

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
        else if (!alreadyTerminal && outcome is not (Outcome.Ignored or Outcome.Unsubscribed))
        {
            recipient.Status = ECardRecipientStatuses.Sent;
        }

        await _db.SaveChangesAsync();
        return recipient.ClientId;
    }

    private async Task<int?> RecordELetterAsync(int id, Outcome outcome, string normalized, string? messageId, string? reason, DateTime at)
    {
        var recipient = await _db.ELetterRecipients.FirstOrDefaultAsync(r => r.Id == id);
        if (recipient == null) return null;

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
        else if (!alreadyTerminal && outcome is not (Outcome.Ignored or Outcome.Unsubscribed))
        {
            recipient.Status = ELetterRecipientStatuses.Sent;
        }

        await _db.SaveChangesAsync();
        return recipient.ClientId;
    }

    private async Task<int?> RecordPollAsync(int id, Outcome outcome, string normalized, string? messageId, string? reason, DateTime at)
    {
        var recipient = await _db.PollRecipients.FirstOrDefaultAsync(r => r.Id == id);
        if (recipient == null) return null;

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
        else if (!alreadyTerminal && outcome != Outcome.Unsubscribed)
        {
            recipient.Status = PollRecipientStatus.Sent;
        }

        await _db.SaveChangesAsync();
        return recipient.ClientId;
    }

    private async Task<int?> RecordDidYouKnowAsync(int id, Outcome outcome, string normalized, string? messageId, string? reason, DateTime at)
    {
        var item = await _db.DidYouKnowEmailQueueItems.FirstOrDefaultAsync(q => q.Id == id);
        if (item == null) return null;

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
        else if (!alreadyTerminal && outcome != Outcome.Unsubscribed)
        {
            item.Status = DidYouKnowQueueStatuses.Sent;
        }

        await _db.SaveChangesAsync();
        return item.ClientId;
    }

    // Timestamps are write-once (??=): the FIRST time an event type is seen is the interesting one.
    // SendGrid re-sends events on its own retry schedule and a client opening a card five times
    // should not keep moving OpenedAt forward.
    // 452: an invoice email -- the send, a resend, or an overdue reminder. Same milestones as the
    // four channels above; Bounced and Failed are terminal here too.
    private async Task<int?> RecordInvoiceEmailAsync(int id, Outcome outcome, string normalized, string? messageId, string? reason, DateTime at)
    {
        var row = await _db.ClientInvoiceEmails.FirstOrDefaultAsync(e => e.Id == id);
        if (row == null) return null;

        row.LastEvent = normalized;
        row.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(messageId)) row.ProviderMessageId = messageId;

        var alreadyTerminal = row.Status is ClientInvoiceEmailStatus.Failed or ClientInvoiceEmailStatus.Bounced;
        ApplyTimestamps(outcome, at, t => row.SentAt ??= t, t => row.DeliveredAt ??= t,
            t => row.OpenedAt ??= t, t => row.ClickedAt ??= t, t => row.BouncedAt ??= t);

        if (!alreadyTerminal && outcome == Outcome.Bounced)
        {
            row.Status = ClientInvoiceEmailStatus.Bounced;
            row.FailureReason = Clip(reason);
        }
        else if (!alreadyTerminal && outcome == Outcome.Failed)
        {
            row.Status = ClientInvoiceEmailStatus.Failed;
            row.FailedAt ??= at;
            row.FailureReason = Clip(reason);
        }
        else if (!alreadyTerminal && outcome is Outcome.Delivered or Outcome.Opened or Outcome.Clicked)
        {
            row.Status = ClientInvoiceEmailStatus.Delivered;
        }
        else if (!alreadyTerminal && outcome == Outcome.Sent && row.Status != ClientInvoiceEmailStatus.Delivered)
        {
            row.Status = ClientInvoiceEmailStatus.Sent;
        }

        await _db.SaveChangesAsync();
        return row.ClientId;
    }

    private static string Clip(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= 500 ? value : value[..500];

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
