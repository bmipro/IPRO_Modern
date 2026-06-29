using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class NewsLetterDispatchJob
{
    private readonly IUnitOfWork _uow;
    private readonly NewsLetterDispatcher _dispatcher;
    private readonly ILogger<NewsLetterDispatchJob> _logger;

    public NewsLetterDispatchJob(IUnitOfWork uow, NewsLetterDispatcher dispatcher, ILogger<NewsLetterDispatchJob> logger)
    {
        _uow = uow; _dispatcher = dispatcher; _logger = logger;
    }

    public async Task RunAsync()
    {
        var due = await _uow.NewsLetters.FindAsync(n =>
            n.Status == NewsLetterStatus.Scheduled &&
            n.ScheduledAt <= DateTime.UtcNow);

        foreach (var nl in due)
        {
            _logger.LogInformation("Dispatching newsletter {Id}: {Subject}", nl.Id, nl.Subject);
            await _dispatcher.DispatchAsync(nl.Id);
        }
    }
}
