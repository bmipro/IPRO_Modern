using IPRO.Entities;

namespace IPRO.Business.Interfaces;

public interface INewsLetterService
{
    Task<IEnumerable<NewsLetter>> GetByAgentAsync(int agentId);
    Task<NewsLetter?> GetByIdAsync(int id);
    Task<NewsLetter> CreateAsync(NewsLetter newsletter);
    Task UpdateAsync(NewsLetter newsletter);
    Task DeleteAsync(int id);
    Task ScheduleAsync(int id, DateTime scheduledAt);
    Task MarkAsSentAsync(int id, int totalSent);
    Task<IEnumerable<NewsLetterRecipient>> GetRecipientsAsync(int newsletterId);
    Task RecordRecipientEventAsync(int recipientId, string eventName, string? providerMessageId, string? reason, DateTime occurredAt);
    Task<IEnumerable<NewsLetterArticle>> GetArticlesAsync(int newsletterId);
    Task AddArticleAsync(NewsLetterArticle article);
    Task RemoveArticleAsync(int articleId);
}
