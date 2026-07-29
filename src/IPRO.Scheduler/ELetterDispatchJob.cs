using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
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
        var due = await _db.ELetters
            .Where(l => l.Status == ELetterStatuses.Scheduled && l.ScheduledAt <= DateTime.UtcNow)
            .ToListAsync();

        foreach (var letter in due)
        {
            try
            {
                _logger.LogInformation("Dispatching e-letter {ELetterId}", letter.Id);
                await _dispatcher.DispatchAsync(letter.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "E-letter {ELetterId} dispatch failed", letter.Id);
            }
        }
    }
}
