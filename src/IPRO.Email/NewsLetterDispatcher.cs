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

    public async Task DispatchAsync(int newsletterId)
    {
        var newsletter = await _uow.NewsLetters.GetByIdAsync(newsletterId);
        if (newsletter == null) return;

        var send = (await _uow.NewsLetterSends.FindAsync(s =>
                s.NewsLetterId == newsletterId &&
                s.Status == NewsLetterSendStatus.Scheduled))
            .OrderBy(s => s.ScheduledAt)
            .FirstOrDefault();

        if (send == null)
        {
            return;
        }

        await DispatchSendAsync(send.Id);
    }

    public async Task DispatchSendAsync(int sendId)
    {
        var send = await _uow.NewsLetterSends.GetByIdAsync(sendId);
        if (send == null || send.Status != NewsLetterSendStatus.Scheduled) return;

        var newsletter = await _uow.NewsLetters.GetByIdAsync(send.NewsLetterId);
        if (newsletter == null) return;

        var sendingAgent = await _uow.AgentUsers.GetByIdAsync(newsletter.AgentUserId);
        var newsletterReplyToName = sendingAgent == null ? null : $"{sendingAgent.FirstName} {sendingAgent.LastName}".Trim();
        var articles = await _uow.NewsLetterArticles.FindAsync(a => a.NewsLetterId == newsletter.Id);
        var sidebarCtas = NewsLetterSidebarCtas.FromJson(newsletter.SidebarCtasJson);
        var wrappedHtmlBody = sendingAgent == null ? newsletter.HtmlBody : NewsletterHtmlComposer.Wrap(newsletter, sendingAgent, GetBaseUrl(), articles, sidebarCtas);

        send.Status = NewsLetterSendStatus.Sending;
        _uow.NewsLetterSends.Update(send);
        await _uow.SaveChangesAsync();

        var subscribers = await GetAudienceClientsAsync(send);
        if (subscribers == null)
        {
            // The client or category this send was aimed at has been deleted. Refuse rather than
            // guess: the alternative the code used to take was mailing the agent's whole list.
            send.Status = NewsLetterSendStatus.Failed;
            send.TotalSent = 0;
            // SentAt deliberately left null -- nothing was sent, and the Sends list reads it as the
            // delivery timestamp.
            _uow.NewsLetterSends.Update(send);
            await _uow.SaveChangesAsync();
            _logger.LogError(
                "Newsletter send {SendId} was cancelled: its {AudienceType} audience no longer exists " +
                "(the client or category was deleted after the send was scheduled). Nothing was emailed.",
                send.Id, send.AudienceType);
            return;
        }

        var recipients = subscribers
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

        send.TotalRecipients = recipients.Count;
        await _uow.NewsLetterRecipients.AddRangeAsync(recipients);
        _uow.NewsLetterSends.Update(send);
        await _uow.SaveChangesAsync();

        var sentCount = 0;
        foreach (var recipient in recipients)
        {
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
                _uow.NewsLetterRecipients.Update(recipient);

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
                _uow.NewsLetterRecipients.Update(recipient);
            }
        }

        send.Status = sentCount > 0 ? NewsLetterSendStatus.Sent : NewsLetterSendStatus.Cancelled;
        send.SentAt = DateTime.UtcNow;
        send.TotalSent = sentCount;
        _uow.NewsLetterSends.Update(send);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("Newsletter send {SendId} for newsletter {NewsletterId} dispatched to {Count} recipients. Success: {Success}",
            send.Id, newsletter.Id, recipients.Count, sentCount > 0);
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

    public async Task DispatchDripStepAsync(int campaignId, int stepIndex, string toEmail, string toName, string? unsubscribeToken = null, int enrollmentId = 0)
    {
        var campaign = await _uow.DripCampaigns.GetByIdAsync(campaignId);
        if (campaign == null || !campaign.IsActive) return;

        var sendingAgent = await _uow.AgentUsers.GetByIdAsync(campaign.AgentUserId);
        var replyToName = sendingAgent == null ? null : $"{sendingAgent.FirstName} {sendingAgent.LastName}".Trim();

        var steps = (await _uow.DripCampaignSteps.FindAsync(s => s.DripCampaignId == campaignId))
            .OrderBy(s => s.SortOrder).ToList();

        if (stepIndex >= steps.Count) return;
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
    }
}
