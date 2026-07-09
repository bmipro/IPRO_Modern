using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.Extensions.Logging;

namespace IPRO.Email;

public class NewsLetterDispatcher
{
    private readonly IUnitOfWork _uow;
    private readonly IEmailService _email;
    private readonly ILogger<NewsLetterDispatcher> _logger;

    public NewsLetterDispatcher(IUnitOfWork uow, IEmailService email, ILogger<NewsLetterDispatcher> logger)
    {
        _uow = uow;
        _email = email;
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

        send.Status = NewsLetterSendStatus.Sending;
        _uow.NewsLetterSends.Update(send);
        await _uow.SaveChangesAsync();

        var subscribers = (await _uow.Clients.FindAsync(c =>
            c.AgentUserId == send.AgentUserId && c.IsNewsletterSubscribed))
            .ToList();

        var recipients = subscribers
            .Where(c => !string.IsNullOrWhiteSpace(c.Email))
            .Select(c => new NewsLetterRecipient
            {
                NewsLetterId = newsletter.Id,
                NewsLetterSendId = send.Id,
                ClientId = c.Id,
                Email = c.Email.Trim().ToLowerInvariant(),
                RecipientName = $"{c.FirstName} {c.LastName}".Trim(),
                Status = NewsLetterRecipientStatus.Queued
            })
            .ToList();

        send.TotalRecipients = recipients.Count;
        await _uow.NewsLetterRecipients.AddRangeAsync(recipients);
        _uow.NewsLetterSends.Update(send);
        await _uow.SaveChangesAsync();

        var sentCount = 0;
        foreach (var recipient in recipients)
        {
            var result = await _email.SendDetailedAsync(
                recipient.Email,
                recipient.RecipientName,
                newsletter.Subject,
                newsletter.HtmlBody,
                newsletter.TextBody,
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

    public async Task DispatchDripStepAsync(int campaignId, int stepIndex, string toEmail, string toName)
    {
        var campaign = await _uow.DripCampaigns.GetByIdAsync(campaignId);
        if (campaign == null || !campaign.IsActive) return;

        var steps = (await _uow.DripCampaignSteps.FindAsync(s => s.DripCampaignId == campaignId))
            .OrderBy(s => s.SortOrder).ToList();

        if (stepIndex >= steps.Count) return;
        var step = steps[stepIndex];

        await _email.SendAsync(toEmail, toName, step.Subject, step.HtmlBody);
        _logger.LogInformation("Drip step {Step} of campaign {Campaign} sent to {Email}", stepIndex, campaignId, toEmail);
    }
}
