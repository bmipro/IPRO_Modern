using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
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
        var due = await _db.PollSends
            .Where(s => s.Status == PollSendStatus.Scheduled && s.ScheduledAt <= DateTime.UtcNow)
            .ToListAsync();

        foreach (var send in due)
        {
            _logger.LogInformation("Dispatching poll send {SendId} for poll {PollId}", send.Id, send.PollSurveyId);
            await _dispatcher.DispatchSendAsync(send.Id);
        }
    }
}
