using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Email;

public class ELetterDispatcher
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IEmailConsentService _consent;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ELetterDispatcher> _logger;

    public ELetterDispatcher(IPRODbContext db, IEmailService email, IEmailConsentService consent, IConfiguration configuration, ILogger<ELetterDispatcher> logger)
    {
        _db = db;
        _email = email;
        _consent = consent;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task DispatchAsync(int eletterId)
    {
        // CLAIM FIRST, LOAD SECOND -- see the matching note in ECardDispatcher. ELettersController
        // materialises this row and calls us on the same scoped context.
        var heldAttempts = await SendClaims.TryClaimELetterAsync(_db, eletterId, DateTime.UtcNow);
        if (heldAttempts == null) return;
        SendClaims.ForgetTracked<ELetter>(_db, eletterId);

        var letter = await _db.ELetters.AsNoTracking().FirstOrDefaultAsync(l => l.Id == eletterId);
        if (letter == null) return;

        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == letter.AgentUserId);
        if (agent == null)
        {
            // Used to return with the row still Scheduled. Now it is claimed, so returning would
            // leave it stuck for the sweep to retry three times before saying anything.
            await FailAndReleaseAsync(eletterId, heldAttempts.Value, "the sending agent record no longer exists");
            return;
        }

        var replyToName = $"{agent.FirstName} {agent.LastName}".Trim();

        var recipients = await _db.ELetterRecipients
            .Where(r => r.ELetterId == letter.Id && r.Status == ELetterRecipientStatuses.Queued)
            .ToListAsync();

        // Queued-only, so a resumed claim never re-mails. That depends on the per-iteration save at
        // the bottom of this loop -- without it every row stays Queued until the end and a crash at
        // 90% would send the whole list again.
        var sentCount = 0;
        var suppressedCount = 0;
        EmailSendResult? paused = null;   // 491; 493 carries the whole result
        var lastHeartbeat = DateTime.UtcNow;
        foreach (var recipient in recipients)
        {
            if (DateTime.UtcNow - lastHeartbeat > SendClaims.HeartbeatInterval)
            {
                lastHeartbeat = DateTime.UtcNow;
                if (!await SendClaims.HeartbeatELetterAsync(_db, letter.Id, heldAttempts.Value, lastHeartbeat))
                {
                    _logger.LogWarning(
                        "E-letter {ELetterId} was re-claimed by another run; abandoning this one after {Sent} sends.",
                        letter.Id, sentCount);
                    return;
                }
            }

            try
            {
                // Unlike an e-card (one identical body for everyone), a letter's subject and body
                // are merge-resolved per recipient, so both are rebuilt inside the loop.
                var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == recipient.ClientId);
                if (client == null)
                {
                    recipient.Status = ELetterRecipientStatuses.Failed;
                    recipient.FailureReason = "Client no longer exists.";
                    recipient.UpdatedAt = DateTime.UtcNow;
                    continue;
                }

                // No greeting exemption here: an e-letter is correspondence, never a birthday card,
                // so an unsubscribed client never receives one.
                if (_consent.IsSuppressed(client, EmailChannel.ELetter))
                {
                    recipient.Status = ELetterRecipientStatuses.Failed;
                    recipient.FailureReason = "Recipient has unsubscribed from these emails.";
                    recipient.UpdatedAt = DateTime.UtcNow;
                    suppressedCount++;
                    continue;
                }

                var preferencesUrl = _consent.BuildPreferencesUrl(await _consent.GetOrCreateTokenAsync(client));

                var subject = MergeFieldResolver.ResolveText(letter.Subject, client, agent);
                var html = ELetterHtmlComposer.Wrap(letter, agent, client);

                // 488: the platform's own open pixel and click redirect, keyed by a per-recipient token
                // minted here (a resumed send keeps the one its Queued rows already carry).
                if (string.IsNullOrEmpty(recipient.TrackingToken)) recipient.TrackingToken = EmailTrackingLinks.NewToken();
                var trackedHtml = EmailTrackingLinks.IsEnabled(_configuration)
                    ? EmailTrackingLinks.Instrument(EmailUnsubscribeFooter.AppendHtml(html, preferencesUrl), "eletter",
                        recipient.TrackingToken, IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration),
                        EmailTrackingLinks.SigningKey(_configuration))
                    : EmailUnsubscribeFooter.AppendHtml(html, preferencesUrl);

                var result = await _email.SendDetailedAsync(
                    recipient.Email,
                    recipient.RecipientName,
                    subject,
                    // Visible unsubscribe line -- see the note in ECardDispatcher.
                    trackedHtml,
                    // Plain-text alternative -- see the note in ECardDispatcher.
                    ELetterHtmlComposer.WrapText(letter, agent, client, preferencesUrl),
                    customArgs: new Dictionary<string, string>
                    {
                        ["ipro_entity"] = "eletter",
                        ["eletter_id"] = letter.Id.ToString(),
                        ["eletter_recipient_id"] = recipient.Id.ToString(),
                        ["client_id"] = recipient.ClientId.ToString(),
                        ["agent_user_id"] = letter.AgentUserId.ToString()
                    },
                    replyToEmail: agent.Email,
                    replyToName: replyToName,
                    listUnsubscribeUrl: preferencesUrl);

                // 491: "not right now" -- a throttle (429), a 5xx, a timeout -- is not this recipient's
                // fault. Leave the row Queued, end this pass, hand the send back to the schedule.
                if (!result.Success && result.IsTransient)
                {
                    paused = result;
                    break;
                }

                recipient.Status = result.Success ? ELetterRecipientStatuses.Sent : ELetterRecipientStatuses.Failed;
                recipient.SendGridMessageId = result.ProviderMessageId ?? string.Empty;
                recipient.SentAt = result.Success ? DateTime.UtcNow : null;
                recipient.FailureReason = result.Success ? string.Empty : result.Message;
                recipient.UpdatedAt = DateTime.UtcNow;

                if (result.Success) sentCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "E-letter {ELetterId} failed for recipient {RecipientId}", letter.Id, recipient.Id);
                recipient.Status = ELetterRecipientStatuses.Failed;
                recipient.FailureReason = ex.Message;
                recipient.UpdatedAt = DateTime.UtcNow;
            }

            // Outside the try on purpose -- see the matching note in ECardDispatcher. A save failure
            // must end the run, not be logged and stepped over.
            await _db.SaveChangesAsync();
        }

        if (paused != null)
        {
            await PauseForRetryAsync(letter.Id, heldAttempts.Value, sentCount, paused);
            return;
        }

        // Derived from the recipient rows, not from this run's counter: on a resume the local count
        // is only what THIS pass sent, and writing it would under-report a send that reached
        // hundreds of people -- or flip it to Failed because the tail end failed.
        var sentTotal = await _db.ELetterRecipients
            .CountAsync(r => r.ELetterId == letter.Id && r.Status == ELetterRecipientStatuses.Sent);

        // See the matching note in ECardDispatcher: "Sent" was set even when every recipient failed.
        var finalStatus = sentTotal > 0 ? ELetterStatuses.Sent : ELetterStatuses.Failed;
        var finalSentAt = sentTotal > 0 ? (letter.SentAt ?? DateTime.UtcNow) : letter.SentAt;
        var now = DateTime.UtcNow;

        var applied = await _db.ELetters
            .Where(l => l.Id == letter.Id && l.ClaimAttempts == heldAttempts.Value)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, finalStatus)
                .SetProperty(l => l.SentAt, finalSentAt)
                .SetProperty(l => l.TotalSent, sentTotal)
                .SetProperty(l => l.UpdatedAt, now)
                .SetProperty(l => l.ClaimedAt, (DateTime?)null));

        if (applied != 1)
        {
            _logger.LogWarning("E-letter {ELetterId} finished but was already re-claimed; leaving the new owner's state alone.", letter.Id);
            return;
        }

        _logger.LogInformation(
            "E-letter {ELetterId} dispatched to {Count} recipients. Sent this pass: {Sent}. Sent in total: {Total}. Suppressed (unsubscribed): {Suppressed}",
            letter.Id, recipients.Count, sentCount, sentTotal, suppressedCount);
    }

    // 491: the provider said "not right now" (a throttle, a 5xx, a timeout) part-way through the list.
    // Nothing is marked Failed: the recipients not yet reached stay Queued, this pass ends, and the
    // send goes back to Scheduled with its claim released, so the minutely job claims it again as a
    // FRESH claim -- no attempt spent -- and resumes exactly the Queued rows. EmailSendGate makes
    // this rare; this is the safety net under it. Guarded on the held attempt count like every other
    // write to the send row, so a run that was re-claimed mid-send cannot undo the new owner's work.
    private async Task PauseForRetryAsync(int eletterId, int heldAttempts, int sentThisPass, EmailSendResult paused)
    {
        // 493: a deferral (no send slot inside the gate's bound) is the expected rhythm of a launch-day
        // blast -- once a minute for most of an hour -- so it logs at Information; anything else the
        // provider said is worth a Warning. Either way the running total is written, so the activity
        // screen reads "In progress, 150 sent" rather than "Scheduled, 0 sent".
        if (paused.IsDeferred)
            _logger.LogInformation("E-letter {SendId} paused after {Sent} sends this pass: {Reason}", eletterId, sentThisPass, paused.Message);
        else
            _logger.LogWarning("E-letter {SendId} paused after {Sent} sends this pass: {Reason}. Left for the next run.", eletterId, sentThisPass, paused.Message);
        var sentTotal = await _db.ELetterRecipients.CountAsync(r => r.ELetterId == eletterId && r.Status == ELetterRecipientStatuses.Sent);
        await _db.ELetters
            .Where(x => x.Id == eletterId && x.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, ELetterStatuses.Scheduled)
                .SetProperty(x => x.TotalSent, sentTotal)
                .SetProperty(x => x.ClaimedAt, (DateTime?)null));
    }

    private async Task FailAndReleaseAsync(int eletterId, int heldAttempts, string reason)
    {
        _logger.LogError("E-letter {ELetterId} cannot be sent because {Reason}; marking Failed.", eletterId, reason);

        var applied = await _db.ELetters
            .Where(l => l.Id == eletterId && l.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, ELetterStatuses.Failed)
                .SetProperty(l => l.UpdatedAt, DateTime.UtcNow)
                .SetProperty(l => l.ClaimedAt, (DateTime?)null));
        if (applied != 1) return;

        // See ECardDispatcher.FailAndReleaseAsync -- the same fan-out, same reason (441).
        var failureReason = $"Not sent: {reason}";
        var now = DateTime.UtcNow;
        await _db.ELetterRecipients
            .Where(r => r.ELetterId == eletterId && r.Status == ELetterRecipientStatuses.Queued)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, ELetterRecipientStatuses.Failed)
                .SetProperty(r => r.FailureReason, failureReason)
                .SetProperty(r => r.UpdatedAt, now));
    }
}
