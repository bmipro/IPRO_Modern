using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.Services;

public class NewsLetterService : INewsLetterService
{
    private readonly IUnitOfWork _uow;
    private readonly IEmailConsentService _consent;

    // The SAME context the UnitOfWork wraps. Taken directly for the two places that need a
    // conditional UPDATE -- cancelling a send has to be atomic against a dispatcher's claim, and the
    // repository abstraction has no way to express that.
    private readonly IPRODbContext _db;

    public NewsLetterService(IUnitOfWork uow, IEmailConsentService consent, IPRODbContext db)
    {
        _uow = uow;
        _consent = consent;
        _db = db;
    }

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

    // DeleteAsync was removed on 2026-08-15 (auditor 5, F13). It had NO callers, and wiring it up
    // would have been destructive: FK_NewsLetterRecipients_NewsLetters is ON DELETE CASCADE, so
    // deleting a newsletter erases every recipient row -- the open/click/bounce delivery record AND
    // the UnsubscribeToken for mail already sitting in inboxes, which breaks those unsubscribe links
    // (CASL/CAN-SPAM exposure, same already-delivered-mail-breaks class as an expired image cert).
    // If newsletter deletion is ever wanted, build it as a soft delete.

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

    // ONE conditional UPDATE, not read-then-write, and the change is not cosmetic.
    //
    // The old shape read the row, checked Status == Scheduled, and saved a moment later. If a
    // dispatcher claimed the send in that gap, the save stamped Cancelled over Sending -- and
    // because Repository.Update marks every column modified, it also wrote ClaimedAt = NULL and
    // ClaimAttempts = 0 back from its own pre-claim snapshot, erasing the claim entirely. The
    // in-flight dispatcher carried on mailing (nothing re-checks Status inside the loop), its
    // heartbeat silently no-opped, and its terminal write stamped Sent over Cancelled. Net effect:
    // the agent is told the send was cancelled, the whole list is mailed anyway, and the history
    // says Sent.
    //
    // That race existed before the claim, but the dispatcher's own Status != Scheduled guard used to
    // catch it. The claim removed that guard, so this has to become atomic in the same change.
    // Reporting false when zero rows matched is what tells the agent the truth: too late.
    public async Task<bool> CancelSendAsync(int sendId, int agentId)
    {
        var cancelled = await _db.NewsLetterSends
            .Where(s => s.Id == sendId
                     && s.AgentUserId == agentId
                     && s.Status == NewsLetterSendStatus.Scheduled)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, NewsLetterSendStatus.Cancelled));

        if (cancelled == 1)
        {
            // The tracked copy, if any caller had already loaded one, now disagrees with the row.
            SendClaims.ForgetTracked<NewsLetterSend>(_db, sendId);
        }

        return cancelled == 1;
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
                        // Was IsNewsletterSubscribed = false and nothing else, so someone who marked
                        // a newsletter as spam went on receiving that agent's e-cards, e-letters,
                        // polls, Did You Know mail and drip campaigns. A complaint is not a
                        // channel-scoped preference (JOBS-4).
                        await _consent.SuppressAllAsync(client, $"sendgrid:{normalizedEvent}:newsletter");
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
                send.TotalClicked = recipients.Count(r => r.ClickedAt.HasValue);
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
                newsletter.TotalClicked = recipients.Count(r => r.ClickedAt.HasValue);
                _uow.NewsLetters.Update(newsletter);
            }
        }

        await _uow.SaveChangesAsync();
    }

    public async Task RecordDripStepEventAsync(int stepSendId, string eventName, string? providerMessageId, string? reason, DateTime occurredAt)
    {
        var stepSend = await _uow.DripCampaignStepSends.GetByIdAsync(stepSendId);
        if (stepSend == null) return;

        var normalizedEvent = (eventName ?? string.Empty).Trim().ToLowerInvariant();
        stepSend.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(providerMessageId))
        {
            stepSend.SendGridMessageId = providerMessageId;
        }

        switch (normalizedEvent)
        {
            case "processed":
            case "sent":
                if (!IsTerminalFailure(stepSend.Status) && stepSend.Status != NewsLetterRecipientStatus.Delivered && stepSend.Status != NewsLetterRecipientStatus.Opened && stepSend.Status != NewsLetterRecipientStatus.Clicked)
                {
                    stepSend.Status = NewsLetterRecipientStatus.Sent;
                }
                stepSend.SentAt ??= occurredAt;
                break;
            case "delivered":
                if (!IsTerminalFailure(stepSend.Status))
                {
                    stepSend.Status = NewsLetterRecipientStatus.Delivered;
                    stepSend.FailureReason = string.Empty;
                }
                stepSend.DeliveredAt ??= occurredAt;
                break;
            case "open":
            case "opened":
                if (!IsTerminalFailure(stepSend.Status))
                {
                    stepSend.Status = NewsLetterRecipientStatus.Opened;
                    stepSend.FailureReason = string.Empty;
                }
                stepSend.OpenedAt ??= occurredAt;
                break;
            case "click":
            case "clicked":
                if (!IsTerminalFailure(stepSend.Status))
                {
                    stepSend.Status = NewsLetterRecipientStatus.Clicked;
                    stepSend.FailureReason = string.Empty;
                }
                stepSend.ClickedAt ??= occurredAt;
                stepSend.OpenedAt ??= occurredAt;
                break;
            case "bounce":
            case "bounced":
                stepSend.Status = NewsLetterRecipientStatus.Bounced;
                stepSend.BouncedAt ??= occurredAt;
                stepSend.FailureReason = reason ?? stepSend.FailureReason;
                break;
            case "dropped":
                stepSend.Status = NewsLetterRecipientStatus.Dropped;
                stepSend.FailedAt ??= occurredAt;
                stepSend.FailureReason = reason ?? stepSend.FailureReason;
                break;
            case "deferred":
                stepSend.Status = NewsLetterRecipientStatus.Deferred;
                stepSend.FailureReason = reason ?? stepSend.FailureReason;
                break;
            // This case did not exist. A spam complaint or an unsubscribe on a drip-campaign email
            // fell out of the switch entirely, so the campaign kept mailing that person its
            // remaining steps on schedule -- the worst version of the JOBS-4 gap, because a drip is
            // a standing instruction rather than a single send.
            case "spamreport":
            case "unsubscribe":
            case "group_unsubscribe":
                stepSend.Status = NewsLetterRecipientStatus.Unsubscribed;
                stepSend.FailureReason = reason ?? stepSend.FailureReason;
                await SuppressDripRecipientAsync(stepSend.DripCampaignEnrollmentId, normalizedEvent);
                break;
        }

        _uow.DripCampaignStepSends.Update(stepSend);
        await _uow.SaveChangesAsync();
    }

    // A step send names an enrollment, not a client, so the client is one hop away. SuppressAllAsync
    // cancels the enrollment itself, which is what actually stops the rest of the campaign.
    private async Task SuppressDripRecipientAsync(int enrollmentId, string normalizedEvent)
    {
        var enrollment = await _uow.DripCampaignEnrollments.GetByIdAsync(enrollmentId);
        if (enrollment == null) return;

        var client = await _uow.Clients.GetByIdAsync(enrollment.ClientId);
        if (client == null) return;

        await _consent.SuppressAllAsync(client, $"sendgrid:{normalizedEvent}:dripcampaign");
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
