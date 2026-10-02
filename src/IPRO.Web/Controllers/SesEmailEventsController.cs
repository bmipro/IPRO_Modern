using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace IPRO.Web.Controllers;

// 531 (2026-09-30): Amazon SES's delivery reports. Each stream's configuration set publishes its
// Delivery, Bounce, Complaint, Reject, DeliveryDelay and RenderingFailure events to one SNS topic,
// which posts them here, to /SesEmailEvents?secret=<Email:Ses:EventSecret>.
//
// Anonymous, like the ACS endpoint -- SNS cannot sign in -- so three things must hold before a word of
// a message is believed: the shared secret in the URL, the topic being ours (Email:Ses:EventTopicArn),
// and Amazon's signature over the message (SnsTrust). Then the report lands through the same
// correlation as ACS's (EmailEventCorrelation):
//
//  - a Permanent bounce suppresses the client across every channel -- the address does not exist;
//  - a complaint is recorded as "spamreport", which the recorders already turn into a full
//    suppression (JOBS-4): "this is spam" means stop. ACS never reported complaints (TODO 433); SES does;
//  - a delivery, a transient bounce, a delay or a rejection are recorded, and suppress nobody.
//
// A bounce or complaint that could not be processed answers 503, so SNS delivers it again: duplicated
// statistics are recoverable, an address we keep mailing after it bounced is what ends a sending account.
[AllowAnonymous]
public class SesEmailEventsController : Controller
{
    private readonly EmailEventCorrelation _correlation;
    private readonly EmailSettings _settings;
    private readonly ISnsTrust _trust;
    private readonly ILogger<SesEmailEventsController> _logger;

    public SesEmailEventsController(
        IPRODbContext db,
        INewsLetterService newsletters,
        IEmailDeliveryTracker deliveryTracker,
        IEmailConsentService consent,
        IOptions<EmailSettings> settings,
        ISnsTrust trust,
        ILogger<SesEmailEventsController> logger)
    {
        _correlation = new EmailEventCorrelation(db, newsletters, deliveryTracker, consent, logger);
        _settings = settings.Value;
        _trust = trust;
        _logger = logger;
    }

    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Index()
    {
        var ses = _settings.Ses;
        if (string.IsNullOrWhiteSpace(ses.EventSecret) || string.IsNullOrWhiteSpace(ses.EventTopicArn))
        {
            _logger.LogWarning("Rejected an SES event call: Email:Ses:EventSecret or Email:Ses:EventTopicArn is not configured.");
            return Unauthorized();
        }
        if (!FixedTimeEquals(Request.Query["secret"].ToString(), ses.EventSecret))
        {
            _logger.LogWarning("Rejected an SES event call: the shared secret did not match.");
            return Unauthorized();
        }

        string body;
        using (var reader = new StreamReader(Request.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        var envelope = SnsMessages.Parse(body);
        if (envelope == null) return BadRequest();

        // A plain 403, never Forbid(): on this app that is a cookie challenge, a redirect to sign-in.
        if (!string.Equals(envelope.TopicArn, ses.EventTopicArn, StringComparison.Ordinal))
        {
            _logger.LogWarning("Rejected an SNS message from topic {Topic}: not the SES event topic.", envelope.TopicArn);
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        if (!await _trust.VerifyAsync(envelope))
        {
            _logger.LogWarning("Rejected an SNS message ({MessageId}): its signature did not verify.", envelope.MessageId);
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        switch (envelope.Type)
        {
            case "SubscriptionConfirmation":
                if (string.IsNullOrWhiteSpace(envelope.SubscribeUrl)) return BadRequest();
                var confirmed = await _trust.ConfirmSubscriptionAsync(envelope.SubscribeUrl);
                if (confirmed) _logger.LogInformation("Confirmed the SNS subscription to {Topic}.", envelope.TopicArn);
                else _logger.LogWarning("Could not confirm the SNS subscription to {Topic}.", envelope.TopicArn);
                return confirmed ? Ok() : StatusCode(StatusCodes.Status502BadGateway);

            case "UnsubscribeConfirmation":
                _logger.LogWarning("The SNS subscription to {Topic} was removed; SES reports stop arriving until it is re-created.", envelope.TopicArn);
                return Ok();

            case "Notification":
                return await RecordAsync(envelope.Message) ? Ok() : StatusCode(StatusCodes.Status503ServiceUnavailable);

            default:
                return Ok();
        }
    }

    // ---- the pure decisions, testable without a request ---------------------------------------------

    // SES's event types onto the vocabulary the recorders already speak. Open and Click are not
    // published (iPro's own pixel and links count those); anything unknown is skipped, never guessed at.
    public static string? MapEvent(string? eventType, string? bounceType) => eventType?.Trim() switch
    {
        "Delivery" => "delivered",
        "Bounce" => IsPermanent(bounceType) ? "bounce" : "deferred",
        "Complaint" => "spamreport",
        "Reject" or "RenderingFailure" => "dropped",
        // 538: a DeliveryDelay is not an outcome. Amazon keeps trying until the event's expirationTime
        // and then reports a Delivery or a (transient) Bounce of its own. It used to map to "deferred",
        // which the recorders for invoices, cards, letters, polls and Did You Know treat as a final
        // failure: an email delayed by a busy mail server and delivered ten minutes later would have
        // read "could not be sent" for good. Skipped, so the row stays "report pending" until the
        // real outcome arrives.
        "DeliveryDelay" => null,
        "Send" => "processed",
        _ => null
    };

    // ONLY a permanent bounce: a full mailbox or a slow server is not a reason to stop mailing someone.
    public static bool IsHardBounce(string? eventType, string? bounceType) =>
        string.Equals(eventType?.Trim(), "Bounce", StringComparison.Ordinal) && IsPermanent(bounceType);

    private static bool IsPermanent(string? bounceType) =>
        string.Equals(bounceType?.Trim(), "Permanent", StringComparison.OrdinalIgnoreCase);

    // ---- one SES event --------------------------------------------------------------------------------

    // True to acknowledge. False only when a bounce or complaint could not be processed.
    private async Task<bool> RecordAsync(string message)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(message); }
        catch (JsonException)
        {
            _logger.LogWarning("An SES notification's message was not JSON; skipped.");
            return true;
        }

        using (document)
        {
            var root = document.RootElement;
            var eventType = ReadString(root, "eventType");
            if (eventType.Length == 0) eventType = ReadString(root, "notificationType");
            var mail = root.TryGetProperty("mail", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
            var messageId = mail.ValueKind == JsonValueKind.Object ? ReadString(mail, "messageId") : string.Empty;
            if (messageId.Length == 0) return true;

            var bounceType = root.TryGetProperty("bounce", out var b) && b.ValueKind == JsonValueKind.Object ? ReadString(b, "bounceType") : null;
            var mapped = MapEvent(eventType, bounceType);
            if (mapped == null) return true;

            var hardBounce = IsHardBounce(eventType, bounceType);
            var critical = hardBounce || eventType == "Complaint";
            try
            {
                var match = await _correlation.ResolveByMessageIdAsync(messageId);
                if (match == null) return true;   // iPro's own mail and untracked sends have no row

                var (reason, occurredAt) = Details(root, eventType, mail);
                await _correlation.RecordAsync(match.Value, mapped, messageId, reason, occurredAt);
                if (hardBounce) await _correlation.SuppressForHardBounceAsync(match.Value, messageId, "ses");
                return true;
            }
            catch (Exception ex)
            {
                if (critical)
                {
                    _logger.LogError(ex, "SES events: a {EventType} for message {MessageId} could not be processed. Withholding the " +
                        "acknowledgement so SNS delivers it again.", eventType, messageId);
                    return false;
                }
                _logger.LogError(ex, "SES events: a {EventType} for message {MessageId} could not be processed and was skipped.", eventType, messageId);
                return true;
            }
        }
    }

    // What happened and when, from the part of the event SES fills for its type.
    private static (string Reason, DateTime OccurredAt) Details(JsonElement root, string eventType, JsonElement mail)
    {
        var fallback = mail.ValueKind == JsonValueKind.Object ? ReadTimestamp(mail, "timestamp") : DateTime.UtcNow;
        JsonElement part;
        switch (eventType)
        {
            case "Bounce" when root.TryGetProperty("bounce", out part):
                var diagnostic = part.TryGetProperty("bouncedRecipients", out var recipients) && recipients.ValueKind == JsonValueKind.Array
                    ? recipients.EnumerateArray().Select(r => ReadString(r, "diagnosticCode")).FirstOrDefault(d => d.Length > 0)
                    : null;
                return (diagnostic ?? ReadString(part, "bounceSubType"), ReadTimestamp(part, "timestamp", fallback));
            case "Complaint" when root.TryGetProperty("complaint", out part):
                var feedback = ReadString(part, "complaintFeedbackType");
                return (feedback.Length > 0 ? $"complaint: {feedback}" : "complaint", ReadTimestamp(part, "timestamp", fallback));
            case "Delivery" when root.TryGetProperty("delivery", out part):
                return (string.Empty, ReadTimestamp(part, "timestamp", fallback));
            case "Reject" when root.TryGetProperty("reject", out part):
                return (ReadString(part, "reason"), fallback);
            case "DeliveryDelay" when root.TryGetProperty("deliveryDelay", out part):
                return (ReadString(part, "delayType"), ReadTimestamp(part, "timestamp", fallback));
            case "RenderingFailure" when root.TryGetProperty("failure", out part):
                return (ReadString(part, "errorMessage"), fallback);
            default:
                return (string.Empty, fallback);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static bool FixedTimeEquals(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented ?? string.Empty),
            Encoding.UTF8.GetBytes(expected ?? string.Empty));

    private static string ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static DateTime ReadTimestamp(JsonElement element, string property, DateTime? fallback = null) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed.UtcDateTime
            : fallback ?? DateTime.UtcNow;
}
