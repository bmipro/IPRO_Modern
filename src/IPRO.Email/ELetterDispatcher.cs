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
        var letter = await _db.ELetters.FirstOrDefaultAsync(l => l.Id == eletterId);
        if (letter == null || letter.Status != ELetterStatuses.Scheduled) return;

        var agent = await _db.AgentUsers.FirstOrDefaultAsync(a => a.Id == letter.AgentUserId);
        if (agent == null) return;

        letter.Status = ELetterStatuses.Sending;
        await _db.SaveChangesAsync();

        var replyToName = $"{agent.FirstName} {agent.LastName}".Trim();

        var recipients = await _db.ELetterRecipients
            .Where(r => r.ELetterId == letter.Id && r.Status == ELetterRecipientStatuses.Queued)
            .ToListAsync();

        var sentCount = 0;
        var suppressedCount = 0;
        foreach (var recipient in recipients)
        {
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
                    html,
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
        }

        // See the matching note in ECardDispatcher: "Sent" was set even when every recipient failed.
        letter.Status = sentCount > 0 ? ELetterStatuses.Sent : ELetterStatuses.Failed;
        letter.SentAt = sentCount > 0 ? DateTime.UtcNow : null;
        letter.TotalSent = sentCount;
        letter.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "E-letter {ELetterId} dispatched to {Count} recipients. Sent: {Sent}. Suppressed (unsubscribed): {Suppressed}",
            letter.Id, recipients.Count, sentCount, suppressedCount);
    }
}
