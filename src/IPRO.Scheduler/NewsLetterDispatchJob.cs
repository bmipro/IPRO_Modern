using IPRO.DataAccess;
using IPRO.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class NewsLetterDispatchJob
{
    private readonly IPRODbContext _db;
    private readonly NewsLetterDispatcher _dispatcher;
    private readonly ILogger<NewsLetterDispatchJob> _logger;

    public NewsLetterDispatchJob(IPRODbContext db, NewsLetterDispatcher dispatcher, ILogger<NewsLetterDispatchJob> logger)
    {
        _db = db; _dispatcher = dispatcher; _logger = logger;
    }

    public async Task RunAsync()
    {
        var now = DateTime.UtcNow;

        var retired = await SendClaims.RetireExhaustedAsync(_db, now, _logger);
        if (retired > 0)
        {
            _logger.LogWarning("{Count} abandoned send(s) were retired this pass.", retired);
        }

        // IDs only, untracked. This job used to load whole NewsLetterSend entities through the
        // repository, which tracks -- and the dispatcher shares this scoped context, so those
        // pre-claim copies would have been written back over the claim by the first per-recipient
        // save. Selecting ids is not an optimisation here, it is required for correctness.
        var due = await SendClaims.DueNewsletterSends(_db, now)
            .AsNoTracking()
            .OrderBy(s => s.ScheduledAt)
            .Select(s => s.Id)
            .ToListAsync();

        foreach (var sendId in due)
        {
            try
            {
                _logger.LogInformation("Dispatching newsletter send {SendId}", sendId);
                await _dispatcher.DispatchSendAsync(sendId);
            }
            catch (Exception ex)
            {
                // The claim stays set on purpose so the sweep can resume this send.
                _logger.LogError(ex, "Newsletter send {SendId} failed", sendId);
            }
        }
    }
}
