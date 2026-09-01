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
        // CLAIM FIRST, LOAD SECOND, and nothing is read before the claim answers. ECardsController
        // materialises this row and then calls us on the same scoped context, so an earlier read
        // would hand back its pre-claim copy and every later decision would be made on stale data.
        var heldAttempts = await SendClaims.TryClaimECardAsync(_db, ecardId, DateTime.UtcNow);
        if (heldAttempts == null)
        {
            // Someone else owns this send, or it has exhausted its retries. Not an error.
            return;
        }
        SendClaims.ForgetTracked<ECard>(_db, ecardId);

        // AsNoTracking throughout for the card row: every write to it below goes through a
        // conditional UPDATE guarded on our claim, so there is deliberately no tracked copy that a
        // later SaveChangesAsync could write back.
        var card = await _db.ECards.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ecardId);
        if (card == null) return;

        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == card.AgentUserId);
        if (agent == null)
        {
            // Before the claim existed this path simply returned and left the row Scheduled. Now the
            // row is Sending, so returning would leave it claimed -- swept every 15 minutes, three
            // times, before anyone heard about it. Fail it here instead.
            await FailAndReleaseAsync(ecardId, heldAttempts.Value,
                "the sending agent record no longer exists");
            return;
        }

        // Resolve the design even if SuperAdmin has since retired it -- a scheduled card must
        // still send with the artwork the agent picked, not silently fall back to something else.
        var design = await _db.ECardDesigns.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Key == card.Occasion);
        if (design == null)
        {
            await FailAndReleaseAsync(ecardId, heldAttempts.Value,
                $"it references unknown design '{card.Occasion}'");
            return;
        }

        // Card artwork lives in the web app's wwwroot, so the email needs absolute URLs.
        var html = ECardHtmlComposer.Wrap(card, agent, design, IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration));
        var replyToName = $"{agent.FirstName} {agent.LastName}".Trim();

        var recipients = await _db.ECardRecipients
            .Where(r => r.ECardId == card.Id && r.Status == ECardRecipientStatuses.Queued)
            .ToListAsync();

        // Only Queued rows, so a resumed claim never re-mails anyone. That guarantee depends on the
        // per-iteration save at the bottom of this loop: without it every recipient stays Queued in
        // the database until the very end, and a crash at 90% would re-mail the whole list.
        var sentCount = 0;
        var suppressedCount = 0;
        var lastHeartbeat = DateTime.UtcNow;
        foreach (var recipient in recipients)
        {
            // Time-based, not every-N-recipients: one degraded SendGrid call can take a minute or
            // more, and that is exactly when a claim is at risk of being judged stale.
            if (DateTime.UtcNow - lastHeartbeat > SendClaims.HeartbeatInterval)
            {
                lastHeartbeat = DateTime.UtcNow;
                if (!await SendClaims.HeartbeatECardAsync(_db, card.Id, heldAttempts.Value, lastHeartbeat))
                {
                    // The sweep gave this send to another runner while we were mailing. Stop now --
                    // continuing would mail the overlap between our list and theirs twice.
                    _logger.LogWarning(
                        "E-card {ECardId} was re-claimed by another run; abandoning this one after {Sent} sends.",
                        card.Id, sentCount);
                    return;
                }
            }

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

            // Persist THIS recipient before moving on. Deliberately outside the try: a save failure
            // is not a per-recipient problem to be logged and stepped over -- SaveChangesAsync is
            // all-or-nothing, so the failed entity stays Modified and every later save in this loop
            // would throw too. Swallowing it would mail the rest of the list and record every one of
            // them as a failure. Let it propagate; the claim keeps the send recoverable.
            await _db.SaveChangesAsync();
        }

        // Counts come from the RECIPIENT ROWS, not from this run's local counter. On a resumed send
        // sentCount is only what THIS pass managed; writing it would report 40 for a card that
        // reached 900 people, and would flip a mostly-successful card to Failed if the tail end
        // happened to fail.
        var sentTotal = await _db.ECardRecipients
            .CountAsync(r => r.ECardId == card.Id && r.Status == ECardRecipientStatuses.Sent);

        // "Sent" only if something actually went out. This was unconditional, so a card whose every
        // recipient was rejected still showed Status=Sent on the E-Cards list -- the per-recipient
        // rows recorded the failures honestly, but nothing surfaced them and the headline said the
        // opposite. PollDispatcher has always had this shape; cards and letters had not.
        var finalStatus = sentTotal > 0 ? ECardStatuses.Sent : ECardStatuses.Failed;
        // ??= so a resume does not re-date a card that first went out an hour ago.
        var finalSentAt = sentTotal > 0 ? (card.SentAt ?? DateTime.UtcNow) : card.SentAt;
        var now = DateTime.UtcNow;

        // One guarded UPDATE: the terminal status AND the claim release together, so the row can
        // never exist in the state "finished but still claimed" that the sweep would re-run. The
        // ClaimAttempts guard means a run that was robbed mid-send cannot stamp its own outcome over
        // the live owner's work.
        var applied = await _db.ECards
            .Where(c => c.Id == card.Id && c.ClaimAttempts == heldAttempts.Value)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, finalStatus)
                .SetProperty(c => c.SentAt, finalSentAt)
                .SetProperty(c => c.TotalSent, sentTotal)
                .SetProperty(c => c.UpdatedAt, now)
                .SetProperty(c => c.ClaimedAt, (DateTime?)null));

        if (applied != 1)
        {
            _logger.LogWarning("E-card {ECardId} finished but was already re-claimed; leaving the new owner's state alone.", card.Id);
            return;
        }

        _logger.LogInformation(
            "E-card {ECardId} dispatched to {Count} recipients. Sent this pass: {Sent}. Sent in total: {Total}. Suppressed (unsubscribed): {Suppressed}",
            card.Id, recipients.Count, sentCount, sentTotal, suppressedCount);
    }

    // A send that cannot proceed at all. Writes the terminal status and clears the claim in one
    // guarded statement, so it is never left Sending-and-claimed for the sweep to retry three times
    // over 45 minutes before reporting something that was never going to work.
    private async Task FailAndReleaseAsync(int ecardId, int heldAttempts, string reason)
    {
        _logger.LogError("E-card {ECardId} cannot be sent because {Reason}; marking Failed.", ecardId, reason);

        var applied = await _db.ECards
            .Where(c => c.Id == ecardId && c.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, ECardStatuses.Failed)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow)
                .SetProperty(c => c.ClaimedAt, (DateTime?)null));
        if (applied != 1) return;

        // Fan the reason out to every row that was still waiting (441). Before this, the reason went
        // ONLY to the log: the parent read Failed, its recipients stayed Queued, the Issue column was
        // blank and the Failed count was 0 -- four contradictory signals and nothing to act on. The
        // per-recipient paths already set Status=Failed WITH a FailureReason; this makes the
        // cannot-proceed path agree with them, so count, pill and Issue derive from the same rows.
        // Guarded on the claim like the parent write: a run that was robbed mid-send must not stamp
        // the live owner's recipients.
        var failureReason = $"Not sent: {reason}";
        var now = DateTime.UtcNow;
        await _db.ECardRecipients
            .Where(r => r.ECardId == ecardId && r.Status == ECardRecipientStatuses.Queued)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, ECardRecipientStatuses.Failed)
                .SetProperty(r => r.FailureReason, failureReason)
                .SetProperty(r => r.UpdatedAt, now));
    }
}
