using IPRO.Entities;

namespace IPRO.Business.Interfaces;

public interface INewsLetterService
{
    Task<IEnumerable<NewsLetter>> GetByAgentAsync(int agentId);
    Task<NewsLetter?> GetByIdAsync(int id);
    Task<NewsLetter> CreateAsync(NewsLetter newsletter);
    Task<NewsLetter?> DuplicateAsync(int id, int agentId);
    Task UpdateAsync(NewsLetter newsletter);
    // No DeleteAsync on purpose -- see the note in NewsLetterService (F13): a hard delete cascades
    // away delivery history and live unsubscribe tokens. Soft delete or nothing.
    Task ScheduleAsync(int id, DateTime scheduledAt);
    Task<NewsLetterSend?> ScheduleSendAsync(int newsletterId, int agentId, DateTime scheduledAt, NewsLetterAudienceType audienceType = NewsLetterAudienceType.AllSubscribers, int? clientCategoryId = null, int? clientId = null);
    Task<bool> CancelSendAsync(int sendId, int agentId);
    Task MarkAsSentAsync(int id, int totalSent);
    Task<IEnumerable<NewsLetterRecipient>> GetRecipientsAsync(int newsletterId);
    Task<IEnumerable<NewsLetterRecipient>> GetRecipientsForSendAsync(int sendId);
    Task<IEnumerable<NewsLetterSend>> GetSendsAsync(int newsletterId);
    Task RecordRecipientEventAsync(int recipientId, string eventName, string? providerMessageId, string? reason, DateTime occurredAt);
    Task RecordDripStepEventAsync(int stepSendId, string eventName, string? providerMessageId, string? reason, DateTime occurredAt);
    Task<IEnumerable<NewsLetterArticle>> GetArticlesAsync(int newsletterId);
    Task AddArticleAsync(NewsLetterArticle article);
    Task RemoveArticleAsync(int articleId);
}
