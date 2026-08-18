using IPRO.DataAccess;
using IPRO.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class PollDispatchJob
{
    private readonly IPRODbContext _db;
    private readonly PollDispatcher _dispatcher;
    private readonly ILogger<PollDispatchJob> _logger;

    public PollDispatchJob(IPRODbContext db, PollDispatcher dispatcher, ILogger<PollDispatchJob> logger)
    {
        _db = db; _dispatcher = dispatcher; _logger = logger;
    }

    public async Task RunAsync()
    {
        var now = DateTime.UtcNow;

        // Retire first, so an exhausted send is not re-selected on this pass. For polls this also
        // unwinds the parent survey, which would otherwise stay Sending forever and lock the agent
        // out of editing their own poll.
        var retired = await SendClaims.RetireExhaustedAsync(_db, now, _logger);
        if (retired > 0)
        {
            _logger.LogWarning("{Count} abandoned send(s) were retired this pass.", retired);
        }

        // IDs only, untracked, through the shared due predicate -- see the note in ECardDispatchJob.
        var due = await SendClaims.DuePollSends(_db, now)
            .AsNoTracking()
            .OrderBy(s => s.ScheduledAt)
            .Select(s => s.Id)
            .ToListAsync();

        foreach (var sendId in due)
        {
            try
            {
                _logger.LogInformation("Dispatching poll send {SendId}", sendId);
                await _dispatcher.DispatchSendAsync(sendId);
            }
            catch (Exception ex)
            {
                // The claim is deliberately left set: that is what lets the sweep retry this send in
                // 15 minutes instead of losing it.
                _logger.LogError(ex, "Poll send {SendId} failed", sendId);
            }
        }
    }
}
