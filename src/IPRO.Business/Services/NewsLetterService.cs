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

    public async Task<NewsLetter?> DuplicateAsync(int id, int agentId)
    {
        var source = await _uow.NewsLetters.GetByIdAsync(id);
        if (source == null || source.AgentUserId != agentId)
        {
            return null;
        }

        var copy = new NewsLetter
        {
            AgentUserId = agentId,
            Subject = $"{source.Subject} (copy)",
            HtmlBody = source.HtmlBody,
            TextBody = source.TextBody,
            Status = NewsLetterStatus.Draft,
            CreatedAt = DateTime.UtcNow
        };

        await _uow.NewsLetters.AddAsync(copy);
        await _uow.SaveChangesAsync();

        var articles = await _uow.NewsLetterArticles.FindAsync(a => a.NewsLetterId == source.Id);
        foreach (var article in articles)
        {
            await _uow.NewsLetterArticles.AddAsync(new NewsLetterArticle
            {
                NewsLetterId = copy.Id,
                Title = article.Title,
                Content = article.Content,
                ImageUrl = article.ImageUrl,
                SortOrder = article.SortOrder
            });
        }

        await _uow.SaveChangesAsync();
        return copy;
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
            await ScheduleSendAsync(id, nl.AgentUserId, scheduledAt);
        }
    }

    public async Task<NewsLetterSend?> ScheduleSendAsync(int newsletterId, int agentId, DateTime scheduledAt, NewsLetterAudienceType audienceType = NewsLetterAudienceType.AllSubscribers, int? clientCategoryId = null, int? clientId = null)
    {
        var nl = await _uow.NewsLetters.GetByIdAsync(newsletterId);
        if (nl == null || nl.AgentUserId != agentId)
        {
            return null;
        }

        var send = new NewsLetterSend
        {
            NewsLetterId = newsletterId,
            AgentUserId = agentId,
            AudienceType = audienceType,
            AudienceLabel = await GetAudienceLabelAsync(audienceType, agentId, clientCategoryId, clientId),
            ClientCategoryId = audienceType == NewsLetterAudienceType.AccountType ? clientCategoryId : null,
            ClientId = audienceType == NewsLetterAudienceType.IndividualClient ? clientId : null,
            Status = NewsLetterSendStatus.Scheduled,
            ScheduledAt = scheduledAt,
            CreatedAt = DateTime.UtcNow
        };

        await _uow.NewsLetterSends.AddAsync(send);

        nl.Status = NewsLetterStatus.Draft;
        nl.ScheduledAt = null;
        nl.SentAt = null;
        _uow.NewsLetters.Update(nl);

        await _uow.SaveChangesAsync();
        return send;
    }

    public async Task<bool> CancelSendAsync(int sendId, int agentId)
    {
        var send = await _uow.NewsLetterSends.GetByIdAsync(sendId);
        if (send == null || send.AgentUserId != agentId || send.Status != NewsLetterSendStatus.Scheduled)
        {
            return false;
        }

        send.Status = NewsLetterSendStatus.Cancelled;
        _uow.NewsLetterSends.Update(send);
        await _uow.SaveChangesAsync();
        return true;
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

    public Task<IEnumerable<NewsLetterRecipient>> GetRecipientsForSendAsync(int sendId) =>
        _uow.NewsLetterRecipients.FindAsync(r => r.NewsLetterSendId == sendId);

    public Task<IEnumerable<NewsLetterSend>> GetSendsAsync(int newsletterId) =>
        _uow.NewsLetterSends.FindAsync(s => s.NewsLetterId == newsletterId);

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
                if (!IsTerminalFailure(recipient.Status) && recipient.Status != NewsLetterRecipientStatus.Delivered && recipient.Status != NewsLetterRecipientStatus.Opened && recipient.Status != NewsLetterRecipientStatus.Clicked)
                {
                    recipient.Status = NewsLetterRecipientStatus.Sent;
                }
                recipient.SentAt ??= occurredAt;
                break;
            case "delivered":
                if (!IsTerminalFailure(recipient.Status))
                {
                    recipient.Status = NewsLetterRecipientStatus.Delivered;
                    recipient.FailureReason = string.Empty;
                }
                recipient.DeliveredAt ??= occurredAt;
                break;
            case "open":
            case "opened":
                if (!IsTerminalFailure(recipient.Status))
                {
                    recipient.Status = NewsLetterRecipientStatus.Opened;
                    recipient.FailureReason = string.Empty;
                }
                recipient.OpenedAt ??= occurredAt;
                break;
            case "click":
            case "clicked":
                if (!IsTerminalFailure(recipient.Status))
                {
                    recipient.Status = NewsLetterRecipientStatus.Clicked;
                    recipient.FailureReason = string.Empty;
                }
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
                if (recipient.ClientId.HasValue)
                {
                    var client = await _uow.Clients.GetByIdAsync(recipient.ClientId.Value);
                    if (client != null)
                    {
                        client.IsNewsletterSubscribed = false;
                        client.UpdatedAt = DateTime.UtcNow;
                        _uow.Clients.Update(client);
                    }
                }
                break;
        }

        _uow.NewsLetterRecipients.Update(recipient);

        if (recipient.NewsLetterSendId.HasValue)
        {
            var send = await _uow.NewsLetterSends.GetByIdAsync(recipient.NewsLetterSendId.Value);
            if (send != null)
            {
                var recipients = (await _uow.NewsLetterRecipients.FindAsync(r => r.NewsLetterSendId == send.Id)).ToList();
                send.TotalSent = recipients.Count(r => r.SentAt.HasValue || r.DeliveredAt.HasValue || r.OpenedAt.HasValue || r.ClickedAt.HasValue);
                send.TotalOpened = recipients.Count(r => r.OpenedAt.HasValue || r.ClickedAt.HasValue);
                _uow.NewsLetterSends.Update(send);
            }
        }
        else
        {
            var newsletter = await _uow.NewsLetters.GetByIdAsync(recipient.NewsLetterId);
            if (newsletter != null)
            {
                var recipients = (await _uow.NewsLetterRecipients.FindAsync(r => r.NewsLetterId == recipient.NewsLetterId)).ToList();
                newsletter.TotalSent = recipients.Count(r => r.SentAt.HasValue || r.DeliveredAt.HasValue || r.OpenedAt.HasValue || r.ClickedAt.HasValue);
                newsletter.TotalOpened = recipients.Count(r => r.OpenedAt.HasValue || r.ClickedAt.HasValue);
                _uow.NewsLetters.Update(newsletter);
            }
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

    private async Task<string> GetAudienceLabelAsync(NewsLetterAudienceType audienceType, int agentId, int? clientCategoryId, int? clientId)
    {
        if (audienceType == NewsLetterAudienceType.AccountType && clientCategoryId.HasValue)
        {
            var category = await _uow.ClientCategories.GetByIdAsync(clientCategoryId.Value);
            if (category != null && category.AgentUserId == agentId)
            {
                return $"Account type: {category.Name}";
            }
        }

        if (audienceType == NewsLetterAudienceType.IndividualClient && clientId.HasValue)
        {
            var client = await _uow.Clients.GetByIdAsync(clientId.Value);
            if (client != null && client.AgentUserId == agentId)
            {
                var name = $"{client.FirstName} {client.LastName}".Trim();
                return string.IsNullOrWhiteSpace(name) ? client.Email : name;
            }
        }

        return audienceType switch
        {
            NewsLetterAudienceType.AccountType => "Selected account type",
            NewsLetterAudienceType.SelectedClients => "Selected clients",
            NewsLetterAudienceType.IndividualClient => "Individual client",
            _ => "All newsletter subscribers"
        };
    }

    private static bool IsTerminalFailure(NewsLetterRecipientStatus status) =>
        status is NewsLetterRecipientStatus.Bounced
            or NewsLetterRecipientStatus.Dropped
            or NewsLetterRecipientStatus.Failed
            or NewsLetterRecipientStatus.Unsubscribed;
}
