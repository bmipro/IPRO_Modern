using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class ECardDispatchJob
{
    private readonly IPRODbContext _db;
    private readonly ECardDispatcher _dispatcher;
    private readonly ILogger<ECardDispatchJob> _logger;

    public ECardDispatchJob(IPRODbContext db, ECardDispatcher dispatcher, ILogger<ECardDispatchJob> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var due = await _db.ECards
            .Where(c => c.Status == ECardStatuses.Scheduled && c.ScheduledAt <= DateTime.UtcNow)
            .ToListAsync();

        foreach (var card in due)
        {
            try
            {
                _logger.LogInformation("Dispatching e-card {ECardId}", card.Id);
                await _dispatcher.DispatchAsync(card.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "E-card {ECardId} dispatch failed", card.Id);
            }
        }
    }
}
