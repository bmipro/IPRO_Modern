using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Email;

public class ECardDispatcher
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IEmailConsentService _consent;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ECardDispatcher> _logger;

    public ECardDispatcher(IPRODbContext db, IEmailService email, IEmailConsentService consent, IConfiguration configuration, ILogger<ECardDispatcher> logger)
    {
        _db = db;
        _email = email;
        _consent = consent;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task DispatchAsync(int ecardId)
    {
        var card = await _db.ECards.FirstOrDefaultAsync(c => c.Id == ecardId);
        if (card == null || card.Status != ECardStatuses.Scheduled) return;

        var agent = await _db.AgentUsers.FirstOrDefaultAsync(a => a.Id == card.AgentUserId);
        if (agent == null) return;

        // Resolve the design even if SuperAdmin has since retired it -- a scheduled card must
        // still send with the artwork the agent picked, not silently fall back to something else.
        var design = await _db.ECardDesigns.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Key == card.Occasion);
        if (design == null)
        {
            _logger.LogError("E-card {ECardId} references unknown design '{Key}' -- not sending.", card.Id, card.Occasion);
            card.Status = ECardStatuses.Failed;
            card.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return;
        }

        card.Status = ECardStatuses.Sending;
        await _db.SaveChangesAsync();

        // Card artwork lives in the web app's wwwroot, so the email needs absolute URLs.
        var html = ECardHtmlComposer.Wrap(card, agent, design, IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration));
        var replyToName = $"{agent.FirstName} {agent.LastName}".Trim();

        var recipients = await _db.ECardRecipients
            .Where(r => r.ECardId == card.Id && r.Status == ECardRecipientStatuses.Queued)
            .ToListAsync();

        var sentCount = 0;
        var suppressedCount = 0;
        foreach (var recipient in recipients)
        {
            try
            {
                // Consent, checked per recipient at send time rather than when the card was
                // composed -- a client can unsubscribe in between, and a scheduled card can sit for
                // weeks. design.SendAfterUnsubscribe is the birthday/anniversary exemption, and it
                // still requires the client to have opted back in; EmailConsentService owns that
                // rule and this is not the place to second-guess it.
                var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == recipient.ClientId);
                if (client == null || _consent.IsSuppressed(client, EmailChannel.ECard, design.SendAfterUnsubscribe))
                {
                    recipient.Status = ECardRecipientStatuses.Failed;
                    recipient.FailureReason = "Recipient has unsubscribed from these emails.";
                    recipient.UpdatedAt = DateTime.UtcNow;
                    suppressedCount++;
                    continue;
                }

                // Every card now carries a working unsubscribe. Its absence was the one concrete
                // difference between cards and newsletters, and the likeliest reason cards were
                // landing in spam.
                var preferencesUrl = _consent.BuildPreferencesUrl(await _consent.GetOrCreateTokenAsync(client));

                var result = await _email.SendDetailedAsync(
                    recipient.Email,
                    recipient.RecipientName,
                    card.Subject,
                    // The visible unsubscribe line. The List-Unsubscribe header alone is not enough:
                    // mail clients show their own button at their discretion, so without this a
                    // recipient can open a card and have nothing to click.
                    EmailUnsubscribeFooter.AppendHtml(html, preferencesUrl),
                    // Plain-text alternative. Cards are a big image carrying ~10 words, which is a
                    // heavy spam signal on its own; sending HTML only made it worse. Observed
                    // 2026-08-08: every e-card to a SpamAssassin host arrived tagged ***SPAM***
                    // while text-based e-letters to the same mailbox reached the inbox.
                    ECardHtmlComposer.WrapText(card, agent, design, preferencesUrl),
                    customArgs: new Dictionary<string, string>
                    {
                        ["ipro_entity"] = "ecard",
                        ["ecard_id"] = card.Id.ToString(),
                        ["ecard_recipient_id"] = recipient.Id.ToString(),
                        ["client_id"] = recipient.ClientId.ToString(),
                        ["agent_user_id"] = card.AgentUserId.ToString()
                    },
                    replyToEmail: agent.Email,
                    replyToName: replyToName,
                    listUnsubscribeUrl: preferencesUrl);

                recipient.Status = result.Success ? ECardRecipientStatuses.Sent : ECardRecipientStatuses.Failed;
                recipient.SendGridMessageId = result.ProviderMessageId ?? string.Empty;
                recipient.SentAt = result.Success ? DateTime.UtcNow : null;
                recipient.FailureReason = result.Success ? string.Empty : result.Message;
                recipient.UpdatedAt = DateTime.UtcNow;

                if (result.Success) sentCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "E-card {ECardId} failed for recipient {RecipientId}", card.Id, recipient.Id);
                recipient.Status = ECardRecipientStatuses.Failed;
                recipient.FailureReason = ex.Message;
                recipient.UpdatedAt = DateTime.UtcNow;
            }
        }

        // "Sent" only if something actually went out. This was unconditional, so a card whose every
        // recipient was rejected still showed Status=Sent on the E-Cards list -- the per-recipient
        // rows recorded the failures honestly, but nothing surfaced them and the headline said the
        // opposite. PollDispatcher has always had this shape; cards and letters had not.
        card.Status = sentCount > 0 ? ECardStatuses.Sent : ECardStatuses.Failed;
        card.SentAt = sentCount > 0 ? DateTime.UtcNow : null;
        card.TotalSent = sentCount;
        card.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "E-card {ECardId} dispatched to {Count} recipients. Sent: {Sent}. Suppressed (unsubscribed): {Suppressed}",
            card.Id, recipients.Count, sentCount, suppressedCount);
    }
}
