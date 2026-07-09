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
        if (newsletter == null || newsletter.Status != NewsLetterStatus.Scheduled) return;

        newsletter.Status = NewsLetterStatus.Sending;
        _uow.NewsLetters.Update(newsletter);
        await _uow.SaveChangesAsync();

        var subscribers = (await _uow.Clients.FindAsync(c =>
            c.AgentUserId == newsletter.AgentUserId && c.IsNewsletterSubscribed))
            .ToList();

        var recipients = subscribers
            .Where(c => !string.IsNullOrWhiteSpace(c.Email))
            .Select(c => new NewsLetterRecipient
            {
                NewsLetterId = newsletter.Id,
                ClientId = c.Id,
                Email = c.Email.Trim().ToLowerInvariant(),
                RecipientName = $"{c.FirstName} {c.LastName}".Trim(),
                Status = NewsLetterRecipientStatus.Queued
            })
            .ToList();

        newsletter.TotalRecipients = recipients.Count;
        await _uow.NewsLetterRecipients.AddRangeAsync(recipients);
        _uow.NewsLetters.Update(newsletter);
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
                    ["newsletter_recipient_id"] = recipient.Id.ToString(),
                    ["client_id"] = recipient.ClientId?.ToString() ?? string.Empty,
                    ["agent_user_id"] = newsletter.AgentUserId.ToString()
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

        newsletter.Status = sentCount > 0 ? NewsLetterStatus.Sent : NewsLetterStatus.Cancelled;
        newsletter.SentAt = DateTime.UtcNow;
        newsletter.TotalSent = sentCount;
        _uow.NewsLetters.Update(newsletter);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("Newsletter {Id} dispatched to {Count} recipients. Success: {Success}",
            newsletterId, recipients.Count, sentCount > 0);
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
