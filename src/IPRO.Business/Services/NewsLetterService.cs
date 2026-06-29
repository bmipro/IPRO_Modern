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
