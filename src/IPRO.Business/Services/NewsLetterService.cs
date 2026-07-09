using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;

namespace IPRO.Business.Services;

public class NewsLetterService : INewsLetterService
{
    private readonly IUnitOfWork _uow;
    public NewsLetterService(IUnitOfWork uow) => _uow = uow;

    public Task<IEnumerable<NewsLetter>> GetByAgentAsync(int agentId) =>
        _uow.NewsLetters.FindAsync(n => n.AgentUserId == agentId);

    public Task<NewsLetter?> GetByIdAsync(int id) =>
        _uow.NewsLetters.GetByIdAsync(id);

    public async Task<NewsLetter> CreateAsync(NewsLetter newsletter)
    {
        newsletter.Status = NewsLetterStatus.Draft;
        newsletter.CreatedAt = DateTime.UtcNow;
        await _uow.NewsLetters.AddAsync(newsletter);
        await _uow.SaveChangesAsync();
        return newsletter;
    }

    public async Task UpdateAsync(NewsLetter newsletter)
    {
        _uow.NewsLetters.Update(newsletter);
        await _uow.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var nl = await _uow.NewsLetters.GetByIdAsync(id);
        if (nl != null) { _uow.NewsLetters.Remove(nl); await _uow.SaveChangesAsync(); }
    }

    public async Task ScheduleAsync(int id, DateTime scheduledAt)
    {
        var nl = await _uow.NewsLetters.GetByIdAsync(id);
        if (nl != null)
        {
            nl.Status = NewsLetterStatus.Scheduled;
            nl.ScheduledAt = scheduledAt;
            _uow.NewsLetters.Update(nl);
            await _uow.SaveChangesAsync();
        }
    }

    public async Task MarkAsSentAsync(int id, int totalSent)
    {
        var nl = await _uow.NewsLetters.GetByIdAsync(id);
        if (nl != null)
        {
            nl.Status = NewsLetterStatus.Sent;
            nl.SentAt = DateTime.UtcNow;
            nl.TotalSent = totalSent;
            _uow.NewsLetters.Update(nl);
            await _uow.SaveChangesAsync();
        }
    }

    public Task<IEnumerable<NewsLetterRecipient>> GetRecipientsAsync(int newsletterId) =>
        _uow.NewsLetterRecipients.FindAsync(r => r.NewsLetterId == newsletterId);

    public async Task RecordRecipientEventAsync(int recipientId, string eventName, string? providerMessageId, string? reason, DateTime occurredAt)
    {
        var recipient = await _uow.NewsLetterRecipients.GetByIdAsync(recipientId);
        if (recipient == null) return;

        var normalizedEvent = (eventName ?? string.Empty).Trim().ToLowerInvariant();
        recipient.LastEvent = normalizedEvent;
        recipient.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(providerMessageId))
        {
            recipient.SendGridMessageId = providerMessageId;
        }

        switch (normalizedEvent)
        {
            case "processed":
            case "sent":
                recipient.Status = NewsLetterRecipientStatus.Sent;
                recipient.SentAt ??= occurredAt;
                break;
            case "delivered":
                recipient.Status = NewsLetterRecipientStatus.Delivered;
                recipient.DeliveredAt ??= occurredAt;
                break;
            case "open":
            case "opened":
                recipient.Status = NewsLetterRecipientStatus.Opened;
                recipient.OpenedAt ??= occurredAt;
                break;
            case "click":
            case "clicked":
                recipient.Status = NewsLetterRecipientStatus.Clicked;
                recipient.ClickedAt ??= occurredAt;
                recipient.OpenedAt ??= occurredAt;
                break;
            case "bounce":
            case "bounced":
                recipient.Status = NewsLetterRecipientStatus.Bounced;
                recipient.BouncedAt ??= occurredAt;
                recipient.FailureReason = reason ?? recipient.FailureReason;
                break;
            case "dropped":
                recipient.Status = NewsLetterRecipientStatus.Dropped;
                recipient.FailedAt ??= occurredAt;
                recipient.FailureReason = reason ?? recipient.FailureReason;
                break;
            case "deferred":
                recipient.Status = NewsLetterRecipientStatus.Deferred;
                recipient.FailureReason = reason ?? recipient.FailureReason;
                break;
            case "spamreport":
            case "unsubscribe":
            case "group_unsubscribe":
                recipient.Status = NewsLetterRecipientStatus.Unsubscribed;
                recipient.FailureReason = reason ?? recipient.FailureReason;
                break;
        }

        _uow.NewsLetterRecipients.Update(recipient);

        var newsletter = await _uow.NewsLetters.GetByIdAsync(recipient.NewsLetterId);
        if (newsletter != null)
        {
            var recipients = (await _uow.NewsLetterRecipients.FindAsync(r => r.NewsLetterId == recipient.NewsLetterId)).ToList();
            newsletter.TotalSent = recipients.Count(r => r.SentAt.HasValue || r.DeliveredAt.HasValue || r.OpenedAt.HasValue || r.ClickedAt.HasValue);
            newsletter.TotalOpened = recipients.Count(r => r.OpenedAt.HasValue || r.ClickedAt.HasValue);
            _uow.NewsLetters.Update(newsletter);
        }

        await _uow.SaveChangesAsync();
    }

    public Task<IEnumerable<NewsLetterArticle>> GetArticlesAsync(int newsletterId) =>
        _uow.NewsLetterArticles.FindAsync(a => a.NewsLetterId == newsletterId);

    public async Task AddArticleAsync(NewsLetterArticle article)
    {
        await _uow.NewsLetterArticles.AddAsync(article);
        await _uow.SaveChangesAsync();
    }

    public async Task RemoveArticleAsync(int articleId)
    {
        var article = await _uow.NewsLetterArticles.GetByIdAsync(articleId);
        if (article != null) { _uow.NewsLetterArticles.Remove(article); await _uow.SaveChangesAsync(); }
    }
}
