using IPRO.DataAccess;
using IPRO.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class ELetterDispatchJob
{
    private readonly IPRODbContext _db;
    private readonly ELetterDispatcher _dispatcher;
    private readonly ILogger<ELetterDispatchJob> _logger;

    public ELetterDispatchJob(IPRODbContext db, ELetterDispatcher dispatcher, ILogger<ELetterDispatchJob> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var now = DateTime.UtcNow;

        // Retire first, so a send that has exhausted its retries is not selected again on this pass.
        // This is the half of the stuck-send problem that is about TELLING somebody: before it, a
        // card left Sending by a crashed process sat there indefinitely and no log line ever
        // mentioned it.
        var retired = await SendClaims.RetireExhaustedAsync(_db, now, _logger);
        if (retired > 0)
        {
            _logger.LogWarning("{Count} abandoned send(s) were retired this pass.", retired);
        }

        // IDs ONLY, and untracked. Materialising the card entities here would put pre-claim copies in
        // the change tracker that the dispatcher shares, and the first SaveChangesAsync inside the
        // send loop would write Status=Scheduled straight back over the claim.
        //
        // The predicate is SendClaims.DueELetters rather than a local Where, so what the job picks up
        // and what the claim will accept cannot drift apart. It also now includes stale Sending rows
        // -- that IS the sweep; there is no separate recovery job.
        var due = await SendClaims.DueELetters(_db, now)
            .AsNoTracking()
            .OrderBy(l => l.ScheduledAt)
            .Select(l => l.Id)
            .ToListAsync();

        foreach (var letterId in due)
        {
            try
            {
                _logger.LogInformation("Dispatching e-letter {ELetterId}", letterId);
                await _dispatcher.DispatchAsync(letterId);
            }
            catch (Exception ex)
            {
                // Per-item isolation: one bad letter must not stop the rest of the pass. The claim it
                // holds is deliberately NOT released here -- leaving it set is what lets the sweep
                // pick the letter up again in 15 minutes rather than losing it.
                _logger.LogError(ex, "E-letter {ELetterId} dispatch failed", letterId);
            }
        }
    }
}
