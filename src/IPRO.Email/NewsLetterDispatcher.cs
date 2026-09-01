using IPRO.Business.Services;
using IPRO.DataAccess.Repositories;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;

namespace IPRO.Email;

public class NewsLetterDispatcher
{
    private readonly IUnitOfWork _uow;
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NewsLetterDispatcher> _logger;

    public NewsLetterDispatcher(IUnitOfWork uow, IPRODbContext db, IEmailService email, IConfiguration configuration, ILogger<NewsLetterDispatcher> logger)
    {
        _uow = uow;
        _db = db;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    // Not called from anywhere today, but it is the shape the next person copies, so it obeys the
    // same rule as the job: select the ID ONLY, untracked. Materialising the send row here would put
    // a pre-claim copy in the shared change tracker and the claim inside DispatchSendAsync would be
    // silently written back over.
    public async Task DispatchAsync(int newsletterId)
    {
        var sendId = await _db.NewsLetterSends.AsNoTracking()
            .Where(s => s.NewsLetterId == newsletterId && s.Status == NewsLetterSendStatus.Scheduled)
            .OrderBy(s => s.ScheduledAt)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync();

        if (sendId == null) return;

        await DispatchSendAsync(sendId.Value);
    }

    public async Task DispatchSendAsync(int sendId)
    {
        // CLAIM FIRST, LOAD SECOND -- and for this sender the rule needs BOTH halves, because both
        // callers materialise the row before we are entered: the job iterates tracked send entities,
        // and NewsletterController's "send now" adds and saves the send then dispatches it on the
        // same scoped context. GetByIdAsync is FindAsync, which returns the tracked copy without
        // querying, so a read here would return Status=Scheduled no matter how early the claim ran.
        var heldAttempts = await SendClaims.TryClaimNewsletterSendAsync(_db, sendId, DateTime.UtcNow);
        if (heldAttempts == null) return;
        SendClaims.ForgetTracked<NewsLetterSend>(_db, sendId);

        // Untracked, and every write to this row below goes through a guarded conditional UPDATE.
        // Repository.Update marks EVERY column modified, which on this table would rewrite
        // TotalOpened/TotalClicked from a stale snapshot and undo webhook increments that landed
        // while the blast was running.
        var send = await _db.NewsLetterSends.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sendId);
        if (send == null) return;

        var newsletter = await _uow.NewsLetters.GetByIdAsync(send.NewsLetterId);
        if (newsletter == null)
        {
            // Before the claim this returned with the row still Scheduled. Now it is Sending, so
            // returning would leave it claimed: swept every 15 minutes, three times, before anyone
            // was told about a send that can never work. Retire it on the spot.
            await FailAndReleaseAsync(sendId, heldAttempts.Value, "its newsletter no longer exists");
            return;
        }

        var sendingAgent = await _uow.AgentUsers.GetByIdAsync(newsletter.AgentUserId);
        var newsletterReplyToName = sendingAgent == null ? null : $"{sendingAgent.FirstName} {sendingAgent.LastName}".Trim();
        var articles = await _uow.NewsLetterArticles.FindAsync(a => a.NewsLetterId == newsletter.Id);
        var sidebarCtas = NewsLetterSidebarCtas.FromJson(newsletter.SidebarCtasJson);
        var wrappedHtmlBody = sendingAgent == null ? newsletter.HtmlBody : NewsletterHtmlComposer.Wrap(newsletter, sendingAgent, GetBaseUrl(), articles, sidebarCtas);

        // RESUME GATE, ABOVE the audience query. If recipient rows exist this send's audience was
        // settled by an earlier pass, and re-resolving it would do two harmful things: add rows for
        // anyone who joined the target category since, and let the audience-failure branch below
        // stamp Failed over a send that had already delivered hundreds of emails.
        var existing = await _db.NewsLetterRecipients.Where(r => r.NewsLetterSendId == send.Id).ToListAsync();
        List<NewsLetterRecipient> recipients;

        if (existing.Count > 0)
        {
            recipients = existing.Where(r => r.Status == NewsLetterRecipientStatus.Queued).ToList();

            // Consent, re-checked at resume time. The audience query below applies it, but that ran
            // in the earlier pass -- somebody queued then who unsubscribed since must not be mailed
            // now. Same predicate as the audience query, so the two cannot drift.
            var stillConsenting = await _db.Clients
                .Where(c => c.AgentUserId == send.AgentUserId && c.IsNewsletterSubscribed && c.EmailOptOutAt == null)
                .Select(c => c.Id)
                .ToListAsync();
            var consenting = stillConsenting.ToHashSet();

            var withdrawn = recipients.Where(r => r.ClientId == null || !consenting.Contains(r.ClientId.Value)).ToList();
            foreach (var recipient in withdrawn)
            {
                recipient.Status = NewsLetterRecipientStatus.Failed;
                recipient.LastEvent = "suppressed";
                recipient.FailedAt = DateTime.UtcNow;
                recipient.FailureReason = "Recipient unsubscribed before this send could be completed.";
                recipient.UpdatedAt = DateTime.UtcNow;
            }
            if (withdrawn.Count > 0) await _uow.SaveChangesAsync();

            recipients = recipients.Except(withdrawn).ToList();

            _logger.LogInformation(
                "Newsletter send {SendId} resumed after an interrupted run: {Remaining} of {Total} still to mail, {Withdrawn} unsubscribed in the meantime.",
                send.Id, recipients.Count, existing.Count, withdrawn.Count);
        }
        else
        {
            var subscribers = await GetAudienceClientsAsync(send);
            if (subscribers == null)
            {
                // The client or category this send was aimed at has been deleted. Refuse rather than
                // guess: the alternative the code used to take was mailing the agent's whole list.
                // SentAt is deliberately left null -- nothing was sent, and the Sends list reads it
                // as the delivery timestamp.
                await FailAndReleaseAsync(sendId, heldAttempts.Value,
                    $"its {send.AudienceType} audience no longer exists (the client or category was " +
                    "deleted after the send was scheduled). Nothing was emailed");
                return;
            }

            recipients = subscribers
                .Where(c => !string.IsNullOrWhiteSpace(c.Email))
                .Select(c => new NewsLetterRecipient
                {
                    NewsLetterId = newsletter.Id,
                    NewsLetterSendId = send.Id,
                    ClientId = c.Id,
                    Email = c.Email.Trim().ToLowerInvariant(),
                    RecipientName = $"{c.FirstName} {c.LastName}".Trim(),
                    Status = NewsLetterRecipientStatus.Queued,
                    UnsubscribeToken = Guid.NewGuid().ToString("N")
                })
                .ToList();

            await _uow.NewsLetterRecipients.AddRangeAsync(recipients);
            await _uow.SaveChangesAsync();

            // First build only. A resume must not overwrite the original audience size.
            await _db.NewsLetterSends.Where(s => s.Id == send.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.TotalRecipients, recipients.Count));
        }

        var sentCount = 0;
        var lastHeartbeat = DateTime.UtcNow;
        foreach (var recipient in recipients)
        {
            // Time-based rather than every-N-recipients: one degraded SendGrid call can take a
            // minute or more, which is exactly when a claim is at risk of being judged stale.
            if (DateTime.UtcNow - lastHeartbeat > SendClaims.HeartbeatInterval)
            {
                lastHeartbeat = DateTime.UtcNow;
                if (!await SendClaims.HeartbeatNewsletterSendAsync(_db, send.Id, heldAttempts.Value, lastHeartbeat))
                {
                    // Another run owns this send now. Stop before mailing the overlap between our
                    // remaining list and theirs.
                    _logger.LogWarning(
                        "Newsletter send {SendId} was re-claimed by another run; abandoning this one after {Sent} sends.",
                        send.Id, sentCount);
                    return;
                }
            }

            try
            {
                var unsubscribeUrl = BuildUnsubscribeUrl(recipient.UnsubscribeToken);
                var result = await _email.SendDetailedAsync(
                    recipient.Email,
                    recipient.RecipientName,
                    newsletter.Subject,
                    AppendUnsubscribeHtml(wrappedHtmlBody, unsubscribeUrl),
                    AppendUnsubscribeText(newsletter.TextBody, unsubscribeUrl),
                    new Dictionary<string, string>
                    {
                        ["ipro_entity"] = "newsletter",
                        ["newsletter_id"] = newsletter.Id.ToString(),
                        ["newsletter_send_id"] = send.Id.ToString(),
                        ["newsletter_recipient_id"] = recipient.Id.ToString(),
                        ["client_id"] = recipient.ClientId?.ToString() ?? string.Empty,
                        ["agent_user_id"] = send.AgentUserId.ToString()
                    },
                    replyToEmail: sendingAgent?.Email,
                    replyToName: newsletterReplyToName,
                    listUnsubscribeUrl: unsubscribeUrl);

                recipient.Status = result.Success ? NewsLetterRecipientStatus.Sent : NewsLetterRecipientStatus.Failed;
                recipient.SendGridMessageId = result.ProviderMessageId ?? string.Empty;
                recipient.LastEvent = result.Success ? "processed" : "failed";
                recipient.SentAt = result.Success ? DateTime.UtcNow : null;
                recipient.FailedAt = result.Success ? null : DateTime.UtcNow;
                recipient.FailureReason = result.Success ? string.Empty : result.Message;
                recipient.UpdatedAt = DateTime.UtcNow;
                // No _uow.NewsLetterRecipients.Update() here: Repository.Update marks EVERY column
                // modified, which would rewrite DeliveredAt/OpenedAt/ClickedAt/LastEvent from this
                // run's snapshot over webhook events that landed mid-blast. The entity is tracked;
                // assignment is enough, and only genuinely-changed columns are written.

                if (result.Success)
                {
                    sentCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Newsletter send {SendId} failed for recipient {RecipientId}", send.Id, recipient.Id);
                recipient.Status = NewsLetterRecipientStatus.Failed;
                recipient.LastEvent = "failed";
                recipient.FailedAt = DateTime.UtcNow;
                recipient.FailureReason = ex.Message;
                recipient.UpdatedAt = DateTime.UtcNow;
            }

            // Persist THIS recipient before the next one. Deliberately outside the try: a save
            // failure is not a per-recipient problem to log and step over. SaveChangesAsync is
            // all-or-nothing, so a failed entity stays Modified and every later save in this loop
            // throws too -- swallowing it would mail the remaining hundreds and record every one of
            // them as a failure. Let it propagate; the claim keeps the send recoverable.
            await _uow.SaveChangesAsync();
        }

        // Counted from the RECIPIENT ROWS, never from this run's local counter. A resume that
        // finishes the last 100 of a 1,000-recipient blast would otherwise write TotalSent = 100 --
        // and if those last 100 all failed, would write Cancelled over a send that reached 900
        // people, erasing them from the record entirely.
        var sentTotal = await _db.NewsLetterRecipients.CountAsync(r =>
            r.NewsLetterSendId == send.Id && r.SentAt != null);

        var now = DateTime.UtcNow;
        var applied = await _db.NewsLetterSends
            .Where(s => s.Id == send.Id && s.ClaimAttempts == heldAttempts.Value)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, sentTotal > 0 ? NewsLetterSendStatus.Sent : NewsLetterSendStatus.Cancelled)
                // ??= so a resume does not re-date a send that first went out an hour ago.
                .SetProperty(s => s.SentAt, s => s.SentAt ?? now)
                .SetProperty(s => s.TotalSent, sentTotal)
                // The terminal status and the claim release in ONE statement: the row can never sit
                // in the "finished but still claimed" state the sweep would re-run.
                .SetProperty(s => s.ClaimedAt, (DateTime?)null));

        if (applied != 1)
        {
            _logger.LogWarning("Newsletter send {SendId} finished but was already re-claimed; leaving the new owner's state alone.", send.Id);
            return;
        }

        _logger.LogInformation(
            "Newsletter send {SendId} for newsletter {NewsletterId}: {Count} handled this pass, {Sent} sent this pass, {Total} sent in total.",
            send.Id, newsletter.Id, recipients.Count, sentCount, sentTotal);
    }

    // A send that cannot proceed at all. Terminal status and claim release in one guarded statement,
    // so it is never left Sending-and-claimed for the sweep to retry three times over 45 minutes
    // before reporting something that was never going to work.
    private async Task FailAndReleaseAsync(int sendId, int heldAttempts, string reason)
    {
        _logger.LogError("Newsletter send {SendId} was cancelled because {Reason}.", sendId, reason);

        var applied = await _db.NewsLetterSends
            .Where(s => s.Id == sendId && s.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, NewsLetterSendStatus.Failed)
                .SetProperty(s => s.TotalSent, 0)
                .SetProperty(s => s.ClaimedAt, (DateTime?)null));
        if (applied != 1) return;

        // See ECardDispatcher.FailAndReleaseAsync -- the same fan-out, same reason (441).
        var failureReason = $"Not sent: {reason}";
        var now = DateTime.UtcNow;
        await _db.NewsLetterRecipients
            .Where(r => r.NewsLetterSendId == sendId && r.Status == NewsLetterRecipientStatus.Queued)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, NewsLetterRecipientStatus.Failed)
                .SetProperty(r => r.FailureReason, failureReason)
                .SetProperty(r => r.UpdatedAt, now));
    }

    private string GetBaseUrl() => IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration);

    private string BuildUnsubscribeUrl(string token)
    {
        return $"{GetBaseUrl()}/Newsletter/Unsubscribe?token={WebUtility.UrlEncode(token)}";
    }

    private static string AppendUnsubscribeHtml(string htmlBody, string unsubscribeUrl)
    {
        var encodedUrl = WebUtility.HtmlEncode(unsubscribeUrl);
        var footer = $"""
            <div style="margin-top:32px;padding-top:16px;border-top:1px solid #dbe4f0;color:#64748b;font-family:Arial,sans-serif;font-size:12px;line-height:1.5;">
              You are receiving this email because you are subscribed to updates from your IPRO adviser.
              <br>
              <a href="{encodedUrl}" style="color:#2563eb;">Unsubscribe from future newsletters</a>
            </div>
            """;

        return $"{htmlBody}{Environment.NewLine}{footer}";
    }

    private static string AppendUnsubscribeText(string? textBody, string unsubscribeUrl)
    {
        return $"""
            {textBody ?? string.Empty}

            ---
            You are receiving this email because you are subscribed to updates from your IPRO adviser.
            Unsubscribe from future newsletters:
            {unsubscribeUrl}
            """;
    }

    // Returns null -- NOT an empty list -- when this send's audience no longer resolves, so the
    // caller can tell "nobody matched the filter" apart from "the filter itself is gone".
    private async Task<List<Client>?> GetAudienceClientsAsync(NewsLetterSend send)
    {
        // IsNewsletterSubscribed is the newsletter's own flag; EmailOptOutAt is the global "stop
        // everything" set by the unsubscribe link. Both must pass. Expressed here as a query filter
        // rather than via EmailConsentService because this runs in SQL over the whole client list --
        // the rule is the same one, and EmailConsentService.IsSuppressed remains the authority for
        // the per-recipient checks the other dispatchers make.
        var query = _db.Clients
            .Include(c => c.Categories)
            .Where(c => c.AgentUserId == send.AgentUserId
                        && c.IsNewsletterSubscribed
                        && c.EmailOptOutAt == null);

        // FAIL CLOSED. The old `_ => query` fell through to the agent's ENTIRE subscriber list
        // whenever the narrowing id was null -- and the ids go null on their own:
        // FK_NewsLetterSends_Clients_ClientId and the ClientCategoryId FK are both ON DELETE SET NULL.
        // So an agent who scheduled a newsletter for one client, then deleted that client before the
        // send ran, got it broadcast to everybody, while AudienceLabel still displayed the original
        // narrow audience -- the send history actively lied about what had happened.
        //
        // Widening an audience is never the safe default. Returning null here tells the caller to
        // fail the send instead.
        // Checking the row still EXISTS, not merely that the id is non-null. Today the FKs are
        // ON DELETE SET NULL so a null id is the signal -- but PollSends models the same thing with
        // no FK at all, where the id survives and dangles. Testing existence covers both, so this
        // stays correct if the constraints change (and they do: the ledger guard drops FKs on
        // purpose). Same rule enforced the same way in PollDispatcher.
        var targeted = send.AudienceType switch
        {
            NewsLetterAudienceType.AccountType =>
                send.ClientCategoryId.HasValue
                && await _db.ClientCategories.AnyAsync(cat => cat.Id == send.ClientCategoryId.Value)
                    ? query.Where(c => c.Categories.Any(cat => cat.Id == send.ClientCategoryId.Value))
                    : null,
            NewsLetterAudienceType.IndividualClient =>
                send.ClientId.HasValue
                && await _db.Clients.AnyAsync(c => c.Id == send.ClientId.Value)
                    ? query.Where(c => c.Id == send.ClientId.Value)
                    : null,
            // AllSubscribers (and any future member that genuinely means "everyone") keeps the
            // unnarrowed query -- that IS its audience, not a fallback.
            _ => query
        };

        if (targeted == null) return null;

        return await targeted.ToListAsync();
    }

    // JOBS-7 (2026-08-20): this used to swallow the send result -- the failure was recorded on
    // the step-send row and the method returned as if nothing happened, so the job blanked the
    // enrollment's error and advanced past the step the client never received. The caller now
    // gets the real outcome; a null return means the step was skipped (campaign gone/inactive).
    // Virtual for the M11 test double: the null return models the mid-run race (campaign or
    // step vanishing between the job's read and this method's own re-read), which no in-test
    // interleaving can produce deterministically against a real database.
    public virtual async Task<EmailSendResult?> DispatchDripStepAsync(int campaignId, int stepIndex, string toEmail, string toName, string? unsubscribeToken = null, int enrollmentId = 0)
    {
        var campaign = await _uow.DripCampaigns.GetByIdAsync(campaignId);
        if (campaign == null || !campaign.IsActive) return null;

        var sendingAgent = await _uow.AgentUsers.GetByIdAsync(campaign.AgentUserId);
        var replyToName = sendingAgent == null ? null : $"{sendingAgent.FirstName} {sendingAgent.LastName}".Trim();

        var steps = (await _uow.DripCampaignSteps.FindAsync(s => s.DripCampaignId == campaignId))
            .OrderBy(s => s.SortOrder).ToList();

        if (stepIndex >= steps.Count) return null;
        var step = steps[stepIndex];

        var stepSend = new DripCampaignStepSend
        {
            DripCampaignEnrollmentId = enrollmentId,
            DripCampaignStepId = step.Id,
            StepIndex = stepIndex,
            Email = toEmail.Trim().ToLowerInvariant(),
            RecipientName = toName,
            Status = NewsLetterRecipientStatus.Queued
        };
        _db.DripCampaignStepSends.Add(stepSend);
        await _db.SaveChangesAsync();

        var customArgs = new Dictionary<string, string>
        {
            ["ipro_entity"] = "drip_step",
            ["drip_step_send_id"] = stepSend.Id.ToString(),
            ["drip_campaign_id"] = campaignId.ToString(),
            ["enrollment_id"] = enrollmentId.ToString()
        };

        var sanitizedHtmlBody = IPRO.Business.Services.HtmlContentSanitizer.Sanitize(step.HtmlBody);

        EmailSendResult result;
        if (string.IsNullOrWhiteSpace(unsubscribeToken))
        {
            result = await _email.SendDetailedAsync(toEmail, toName, step.Subject, sanitizedHtmlBody, customArgs: customArgs, replyToEmail: sendingAgent?.Email, replyToName: replyToName);
        }
        else
        {
            var unsubscribeUrl = BuildUnsubscribeUrl(unsubscribeToken);
            result = await _email.SendDetailedAsync(toEmail, toName, step.Subject, AppendUnsubscribeHtml(sanitizedHtmlBody, unsubscribeUrl), customArgs: customArgs, replyToEmail: sendingAgent?.Email, replyToName: replyToName, listUnsubscribeUrl: unsubscribeUrl);
        }

        stepSend.Status = result.Success ? NewsLetterRecipientStatus.Sent : NewsLetterRecipientStatus.Failed;
        stepSend.SendGridMessageId = result.ProviderMessageId ?? string.Empty;
        stepSend.SentAt = result.Success ? DateTime.UtcNow : null;
        stepSend.FailedAt = result.Success ? null : DateTime.UtcNow;
        stepSend.FailureReason = result.Success ? string.Empty : result.Message;
        stepSend.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Drip step {Step} of campaign {Campaign} sent to {Email}. Success: {Success}", stepIndex, campaignId, toEmail, result.Success);
        return result;
    }
}
