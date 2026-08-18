using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Email;

public class ELetterDispatcher
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IEmailConsentService _consent;
    private readonly ILogger<ELetterDispatcher> _logger;

    public ELetterDispatcher(IPRODbContext db, IEmailService email, IEmailConsentService consent, ILogger<ELetterDispatcher> logger)
    {
        _db = db;
        _email = email;
        _consent = consent;
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

                var result = await _email.SendDetailedAsync(
                    recipient.Email,
                    recipient.RecipientName,
                    subject,
                    // Visible unsubscribe line -- see the note in ECardDispatcher.
                    EmailUnsubscribeFooter.AppendHtml(html, preferencesUrl),
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

    private async Task FailAndReleaseAsync(int eletterId, int heldAttempts, string reason)
    {
        _logger.LogError("E-letter {ELetterId} cannot be sent because {Reason}; marking Failed.", eletterId, reason);

        await _db.ELetters
            .Where(l => l.Id == eletterId && l.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, ELetterStatuses.Failed)
                .SetProperty(l => l.UpdatedAt, DateTime.UtcNow)
                .SetProperty(l => l.ClaimedAt, (DateTime?)null));
    }
}
