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
        var articles = await _uow.NewsLetterArticles.FindAsync(a => a.NewsLetterId == newsletter.Id);
        var sidebarCtas = NewsLetterSidebarCtas.FromJson(newsletter.SidebarCtasJson);
        var wrappedHtmlBody = sendingAgent == null ? newsletter.HtmlBody : NewsletterHtmlComposer.Wrap(newsletter, sendingAgent, GetBaseUrl(), articles, sidebarCtas);

        send.Status = NewsLetterSendStatus.Sending;
        _uow.NewsLetterSends.Update(send);
        await _uow.SaveChangesAsync();

        var subscribers = await GetAudienceClientsAsync(send);

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
                });

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

        send.Status = sentCount > 0 ? NewsLetterSendStatus.Sent : NewsLetterSendStatus.Cancelled;
        send.SentAt = DateTime.UtcNow;
        send.TotalSent = sentCount;
        _uow.NewsLetterSends.Update(send);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("Newsletter send {SendId} for newsletter {NewsletterId} dispatched to {Count} recipients. Success: {Success}",
            send.Id, newsletter.Id, recipients.Count, sentCount > 0);
    }

    private string GetBaseUrl()
    {
        var baseUrl = _configuration["App:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Contains("yourdomain.com", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = "https://ipro-prod-web.azurewebsites.net";
        }

        return baseUrl.TrimEnd('/');
    }

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

    private async Task<List<Client>> GetAudienceClientsAsync(NewsLetterSend send)
    {
        var query = _db.Clients
            .Include(c => c.Categories)
            .Where(c => c.AgentUserId == send.AgentUserId && c.IsNewsletterSubscribed);

        query = send.AudienceType switch
        {
            NewsLetterAudienceType.AccountType when send.ClientCategoryId.HasValue =>
                query.Where(c => c.Categories.Any(cat => cat.Id == send.ClientCategoryId.Value)),
            NewsLetterAudienceType.IndividualClient when send.ClientId.HasValue =>
                query.Where(c => c.Id == send.ClientId.Value),
            _ => query
        };

        return await query.ToListAsync();
    }

    public async Task DispatchDripStepAsync(int campaignId, int stepIndex, string toEmail, string toName, string? unsubscribeToken = null, int enrollmentId = 0)
    {
        var campaign = await _uow.DripCampaigns.GetByIdAsync(campaignId);
        if (campaign == null || !campaign.IsActive) return;

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

        EmailSendResult result;
        if (string.IsNullOrWhiteSpace(unsubscribeToken))
        {
            result = await _email.SendDetailedAsync(toEmail, toName, step.Subject, step.HtmlBody, customArgs: customArgs);
        }
        else
        {
            var unsubscribeUrl = BuildUnsubscribeUrl(unsubscribeToken);
            result = await _email.SendDetailedAsync(toEmail, toName, step.Subject, AppendUnsubscribeHtml(step.HtmlBody, unsubscribeUrl), customArgs: customArgs);
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
