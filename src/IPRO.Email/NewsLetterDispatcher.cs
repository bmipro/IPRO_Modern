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

        var subscribers = await _uow.Clients.FindAsync(c =>
            c.AgentUserId == newsletter.AgentUserId && c.IsNewsletterSubscribed);

        var recipients = subscribers
            .Select(c => new EmailRecipient(c.Email, $"{c.FirstName} {c.LastName}"))
            .ToList();

        newsletter.TotalRecipients = recipients.Count;
        var success = await _email.SendBulkAsync(recipients, newsletter.Subject, newsletter.HtmlBody, newsletter.TextBody);

        newsletter.Status = success ? NewsLetterStatus.Sent : NewsLetterStatus.Cancelled;
        newsletter.SentAt = DateTime.UtcNow;
        newsletter.TotalSent = success ? recipients.Count : 0;
        _uow.NewsLetters.Update(newsletter);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("Newsletter {Id} dispatched to {Count} recipients. Success: {Success}",
            newsletterId, recipients.Count, success);
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
