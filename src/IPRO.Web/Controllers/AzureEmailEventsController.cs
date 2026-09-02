using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

// TODO 433 -- the second half of the ACS migration. This is the Event Grid counterpart to
// NewsletterController.SendGridEvents, which stays in place because Email:Provider can flip back.
//
// THE STRUCTURAL DIFFERENCE. SendGrid echoed our own ids back to us in custom args, so the webhook
// could read newsletter_recipient_id and route directly. ACS returns only its own messageId, so
// correlation goes the other way: every dispatcher already persists the send's ProviderMessageId on
// its recipient row, and we look the row up by that id across all six senders. Miss one table and
// that sender's events are silently discarded -- the exact bug that left the Card & Letter
// "Delivered" columns blank for their entire existence until 2026-08-08.
//
// WHAT ACS DOES NOT SEND, AND WHY IT MATTERS. Two event types, seven statuses, and NONE of them is
// a spam complaint or an unsubscribe. Under SendGrid, "mark as spam" in Gmail reached us as
// spamreport and suppressed the client across every channel; ACS will never tell us. Our own
// unsubscribe links are provider-independent and still carry consent, so the legal path is intact
// -- but the complaint-driven path is genuinely gone. Recorded as a known gap in TODO 433 rather
// than pretended away.
//
// THE COMPENSATION (owner-approved 2026-08-31). A hard bounce now suppresses, which nothing did
// before: SendGrid's bounce set a status and stopped there. A months-long bounce rate is precisely
// what got the SendGrid account terminated, so continuing to mail an address that does not exist is
// the behaviour that destroys sender reputation. Quarantined and FilteredSpam deliberately do NOT
// suppress -- those are the RECEIVING ORGANISATION's filter, not the person's choice, and cutting
// someone off because their employer's filter caught one newsletter would be actively wrong.
[AllowAnonymous]
public class AzureEmailEventsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly INewsLetterService _newsletters;
    private readonly IEmailDeliveryTracker _deliveryTracker;
    private readonly IEmailConsentService _consent;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureEmailEventsController> _logger;

    public AzureEmailEventsController(
        IPRODbContext db,
        INewsLetterService newsletters,
        IEmailDeliveryTracker deliveryTracker,
        IEmailConsentService consent,
        IConfiguration configuration,
        ILogger<AzureEmailEventsController> logger)
    {
        _db = db;
        _newsletters = newsletters;
        _deliveryTracker = deliveryTracker;
        _consent = consent;
        _configuration = configuration;
        _logger = logger;
    }

    private const string DeliveryReportEvent = "Microsoft.Communication.EmailDeliveryReportReceived";
    private const string EngagementEvent = "Microsoft.Communication.EmailEngagementTrackingReportReceived";
    private const string ValidationEvent = "Microsoft.EventGrid.SubscriptionValidationEvent";

    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Index()
    {
        // Event Grid cannot authenticate as a user, so this action is anonymous and must carry its
        // own secret. Compared in fixed time: a length-or-prefix comparison on a shared secret is
        // a timing oracle.
        var expected = _configuration["Email:AzureEventWebhookSecret"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            _logger.LogWarning("Rejected ACS email event call: Email:AzureEventWebhookSecret is not configured.");
            return Unauthorized();
        }
        var presented = Request.Query["secret"].ToString();
        if (!FixedTimeEquals(presented, expected))
        {
            _logger.LogWarning("Rejected ACS email event call: shared secret did not match.");
            return Unauthorized();
        }

        string rawBody;
        using (var reader = new StreamReader(Request.Body))
        {
            rawBody = await reader.ReadToEndAsync();
        }

        JsonDocument document;
        try { document = JsonDocument.Parse(rawBody); }
        catch (JsonException) { return BadRequest(); }

        using (document)
        {
            var events = document.RootElement;
            if (events.ValueKind != JsonValueKind.Array) return BadRequest();

            var suppressionEventFailed = false;

            foreach (var item in events.EnumerateArray())
            {
                var eventType = ReadString(item, "eventType");

                // The handshake. Event Grid will not deliver a single real event until the endpoint
                // echoes this back, and it arrives in the same array shape as everything else.
                if (string.Equals(eventType, ValidationEvent, StringComparison.OrdinalIgnoreCase))
                {
                    var code = item.TryGetProperty("data", out var vd) ? ReadValidationCode(eventType, vd.GetRawText()) : null;
                    if (code is not null)
                    {
                        _logger.LogInformation("ACS email events: completed the Event Grid subscription validation handshake.");
                        return Ok(new { validationResponse = code });
                    }
                    continue;
                }

                // Read the status OUTSIDE the guard: the catch has to know whether what just failed
                // was the event class that drives suppression, and it cannot ask afterwards (H8).
                string statusForClassification = string.Empty;
                try
                {
                    if (item.TryGetProperty("data", out var d0))
                        statusForClassification = ReadString(d0, "status");
                }
                catch { /* classification is best-effort */ }

                try
                {
                    if (!item.TryGetProperty("data", out var data)) continue;

                    var messageId = ReadString(data, "messageId");
                    if (string.IsNullOrWhiteSpace(messageId)) continue;

                    string? mapped;
                    string reason;
                    DateTime occurredAt;
                    var isDelivery = string.Equals(eventType, DeliveryReportEvent, StringComparison.OrdinalIgnoreCase);

                    if (isDelivery)
                    {
                        var status = ReadString(data, "status");
                        mapped = MapDeliveryStatus(status);
                        reason = data.TryGetProperty("deliveryStatusDetails", out var det)
                            ? ReadString(det, "statusMessage")
                            : string.Empty;
                        occurredAt = ReadTimestamp(data, "deliveryAttemptTimeStamp");
                    }
                    else if (string.Equals(eventType, EngagementEvent, StringComparison.OrdinalIgnoreCase))
                    {
                        mapped = MapEngagementType(ReadString(data, "engagementType"));
                        reason = string.Empty;
                        occurredAt = ReadTimestamp(data, "userActionTimeStamp");
                    }
                    else
                    {
                        continue; // an event type we do not consume
                    }

                    // An unrecognised status is IGNORED, never coerced: writing "dropped" for
                    // something Microsoft adds later would mark good mail as failed.
                    if (mapped is null) continue;

                    var match = await ResolveByMessageIdAsync(messageId);
                    if (match is null)
                    {
                        // Normal and harmless: transactional mail (receipts, reminders, lead
                        // notifications) has no tracked recipient row.
                        continue;
                    }

                    await RecordAsync(match.Value, mapped, messageId, reason, occurredAt);

                    if (isDelivery && ShouldSuppressOnStatus(ReadString(data, "status")))
                    {
                        await SuppressForHardBounceAsync(match.Value, messageId);
                    }
                }
                catch (Exception ex)
                {
                    if (ShouldSuppressOnStatus(statusForClassification))
                    {
                        suppressionEventFailed = true;
                        _logger.LogError(ex,
                            "ACS email events: a HARD BOUNCE could not be processed. Withholding the " +
                            "acknowledgement so Event Grid redelivers -- duplicated delivery statistics " +
                            "are recoverable, an address we keep mailing after it bounced is what ends " +
                            "a sending account.");
                    }
                    else
                    {
                        _logger.LogError(ex, "ACS email events: one event in the batch could not be processed and was skipped.");
                    }
                }
            }

            if (!ShouldAcknowledge(suppressionEventFailed))
            {
                // 503, not 500: "try me again", not "this request was malformed".
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }

        return Ok();
    }

    // ---- the pure decisions, testable without a request ---------------------------------------

    // ACS's seven delivery statuses onto the vocabulary the recorders already speak. Anything
    // unrecognised returns null and is skipped rather than guessed at.
    public static string? MapDeliveryStatus(string? acsStatus) => acsStatus?.Trim().ToLowerInvariant() switch
    {
        "delivered" => "delivered",
        "bounced" => "bounce",          // hard bounce: the address or domain does not exist
        "suppressed" => "dropped",      // ACS refused to send: this address hard-bounced before
        "failed" => "dropped",          // terminal non-delivery
        "quarantined" => "dropped",     // the receiver quarantined it as spam/bulk/phishing
        "filteredspam" => "dropped",    // the receiver rejected it as spam
        "expanded" => "processed",      // distribution group expanded; delivery still in flight
        _ => null
    };

    public static string? MapEngagementType(string? engagementType) => engagementType?.Trim().ToLowerInvariant() switch
    {
        "view" => "open",
        "click" => "click",
        _ => null
    };

    // ONLY a hard bounce. Quarantined and FilteredSpam are the receiving organisation's filter, not
    // the recipient's decision -- see the header comment.
    public static bool ShouldSuppressOnStatus(string? acsStatus) =>
        string.Equals(acsStatus?.Trim(), "Bounced", StringComparison.OrdinalIgnoreCase);

    // 200 tells Event Grid "recorded -- never send it again". Only an unprocessed event that drives
    // suppression may withhold it.
    public static bool ShouldAcknowledge(bool suppressionEventFailed) => !suppressionEventFailed;

    public static string? ReadValidationCode(string? eventType, string? dataJson)
    {
        if (!string.Equals(eventType, ValidationEvent, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(dataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            return doc.RootElement.TryGetProperty("validationCode", out var v) ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // ---- correlation --------------------------------------------------------------------------

    public enum TrackedKind { Newsletter, DripStep, ECard, ELetter, Poll, DidYouKnow, Invoice }
    public readonly record struct TrackedMatch(TrackedKind Kind, int Id, int? ClientId);

    // ACS gives only its message id, so every table whose dispatcher persists ProviderMessageId has
    // to be searched. Ordered cheapest-first by expected volume; the first hit wins because a
    // message id belongs to exactly one send.
    private async Task<TrackedMatch?> ResolveByMessageIdAsync(string messageId)
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

    // The SAME three consumers the SendGrid webhook feeds -- nothing here re-implements recording.
    private Task RecordAsync(TrackedMatch match, string mappedEvent, string messageId, string reason, DateTime occurredAt) =>
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

    private async Task SuppressForHardBounceAsync(TrackedMatch match, string messageId)
    {
        if (match.ClientId is not int clientId) return;

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId);
        if (client is null) return;

        var result = await _consent.SuppressAllAsync(client, $"acs:bounced:{match.Kind.ToString().ToLowerInvariant()}");
        if (!result.WasAlreadySuppressed)
        {
            _logger.LogWarning(
                "ACS reported a HARD BOUNCE for client {ClientId} (message {MessageId}); suppressed across every " +
                "channel. The address does not exist -- continuing to mail it is what ends a sending account.",
                clientId, messageId);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static bool FixedTimeEquals(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented ?? string.Empty),
            Encoding.UTF8.GetBytes(expected ?? string.Empty));

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static DateTime ReadTimestamp(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed.UtcDateTime
            : DateTime.UtcNow;
}
