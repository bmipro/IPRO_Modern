using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// From a provider's message id to the send it belongs to, and from there into the records. Moved out of
// AzureEmailEventsController on 2026-09-30 (531) so the Amazon SES endpoint correlates through the
// SAME code: a second copy would be the next table somebody forgets.
//
// ACS gives only its message id (SES gives it too), so every table whose dispatcher persists the send's
// ProviderMessageId has to be searched. Miss one table and that sender's events are silently discarded
// -- the exact bug that left the Card & Letter "Delivered" columns blank for their entire existence
// until 2026-08-08.
public sealed class EmailEventCorrelation
{
    private readonly IPRODbContext _db;
    private readonly INewsLetterService _newsletters;
    private readonly IEmailDeliveryTracker _deliveryTracker;
    private readonly IEmailConsentService _consent;
    private readonly ILogger _logger;

    public EmailEventCorrelation(IPRODbContext db, INewsLetterService newsletters, IEmailDeliveryTracker deliveryTracker,
        IEmailConsentService consent, ILogger logger)
    {
        _db = db;
        _newsletters = newsletters;
        _deliveryTracker = deliveryTracker;
        _consent = consent;
        _logger = logger;
    }

    public enum TrackedKind { Newsletter, DripStep, ECard, ELetter, Poll, DidYouKnow, Invoice }
    public readonly record struct TrackedMatch(TrackedKind Kind, int Id, int? ClientId);

    // Ordered cheapest-first by expected volume; the first hit wins because a message id belongs to
    // exactly one send.
    public async Task<TrackedMatch?> ResolveByMessageIdAsync(string messageId)
    {
        var newsletter = await _db.NewsLetterRecipients.AsNoTracking()
            .Where(r => r.SendGridMessageId == messageId)
            .Select(r => new { r.Id, r.ClientId }).FirstOrDefaultAsync();
        if (newsletter != null) return new TrackedMatch(TrackedKind.Newsletter, newsletter.Id, newsletter.ClientId);

        // A drip send has no ClientId of its own -- it reaches the person through the enrollment,
        // so the client is joined in. Without this join a hard bounce on a drip step would record
        // the status and suppress nobody.
        var drip = await _db.DripCampaignStepSends.AsNoTracking()
            .Where(r => r.SendGridMessageId == messageId)
            .Join(_db.DripCampaignEnrollments.AsNoTracking(),
                  send => send.DripCampaignEnrollmentId,
                  enrollment => enrollment.Id,
                  (send, enrollment) => new { send.Id, enrollment.ClientId })
            .FirstOrDefaultAsync();
        if (drip != null) return new TrackedMatch(TrackedKind.DripStep, drip.Id, drip.ClientId);

        var ecard = await _db.ECardRecipients.AsNoTracking()
            .Where(r => r.SendGridMessageId == messageId)
            .Select(r => new { r.Id, r.ClientId }).FirstOrDefaultAsync();
        if (ecard != null) return new TrackedMatch(TrackedKind.ECard, ecard.Id, ecard.ClientId);

        var eletter = await _db.ELetterRecipients.AsNoTracking()
            .Where(r => r.SendGridMessageId == messageId)
            .Select(r => new { r.Id, r.ClientId }).FirstOrDefaultAsync();
        if (eletter != null) return new TrackedMatch(TrackedKind.ELetter, eletter.Id, eletter.ClientId);

        var poll = await _db.PollRecipients.AsNoTracking()
            .Where(r => r.SendGridMessageId == messageId)
            .Select(r => new { r.Id, r.ClientId }).FirstOrDefaultAsync();
        if (poll != null) return new TrackedMatch(TrackedKind.Poll, poll.Id, poll.ClientId);

        var dyk = await _db.DidYouKnowEmailQueueItems.AsNoTracking()
            .Where(r => r.SendGridMessageId == messageId)
            .Select(r => new { r.Id, r.ClientId }).FirstOrDefaultAsync();
        if (dyk != null) return new TrackedMatch(TrackedKind.DidYouKnow, dyk.Id, dyk.ClientId);

        // 452: invoice emails -- the send, a resend, or an overdue reminder.
        var invoiceEmail = await _db.ClientInvoiceEmails.AsNoTracking()
            .Where(e => e.ProviderMessageId == messageId)
            .Select(e => new { e.Id, e.ClientId }).FirstOrDefaultAsync();
        if (invoiceEmail != null) return new TrackedMatch(TrackedKind.Invoice, invoiceEmail.Id, invoiceEmail.ClientId);

        return null;
    }

    // The SAME three consumers the SendGrid webhook feeds -- nothing here re-implements recording. A
    // "spamreport" suppresses the client across every channel inside those consumers (JOBS-4).
    public Task RecordAsync(TrackedMatch match, string mappedEvent, string messageId, string reason, DateTime occurredAt) =>
        match.Kind switch
        {
            TrackedKind.Newsletter => _newsletters.RecordRecipientEventAsync(match.Id, mappedEvent, messageId, reason, occurredAt),
            TrackedKind.DripStep => _newsletters.RecordDripStepEventAsync(match.Id, mappedEvent, messageId, reason, occurredAt),
            TrackedKind.ECard => _deliveryTracker.RecordAsync("ecard", match.Id, mappedEvent, messageId, reason, occurredAt),
            TrackedKind.ELetter => _deliveryTracker.RecordAsync("eletter", match.Id, mappedEvent, messageId, reason, occurredAt),
            TrackedKind.Poll => _deliveryTracker.RecordAsync("poll", match.Id, mappedEvent, messageId, reason, occurredAt),
            TrackedKind.DidYouKnow => _deliveryTracker.RecordAsync("didyouknow", match.Id, mappedEvent, messageId, reason, occurredAt),
            TrackedKind.Invoice => _deliveryTracker.RecordAsync("invoice", match.Id, mappedEvent, messageId, reason, occurredAt),
            _ => Task.CompletedTask
        };

    // A hard bounce: the address does not exist. Continuing to mail it is what ends a sending account
    // (it ended the SendGrid one), so the client is suppressed across every channel. `provider` is the
    // short name that goes into the suppression's source ("acs", "ses").
    public async Task SuppressForHardBounceAsync(TrackedMatch match, string messageId, string provider)
    {
        if (match.ClientId is not int clientId) return;

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId);
        if (client is null) return;

        var result = await _consent.SuppressAllAsync(client, $"{provider}:bounced:{match.Kind.ToString().ToLowerInvariant()}");
        if (!result.WasAlreadySuppressed)
        {
            _logger.LogWarning(
                "{Provider} reported a HARD BOUNCE for client {ClientId} (message {MessageId}); suppressed across every " +
                "channel. The address does not exist -- continuing to mail it is what ends a sending account.",
                provider.ToUpperInvariant(), clientId, messageId);
        }
    }
}
