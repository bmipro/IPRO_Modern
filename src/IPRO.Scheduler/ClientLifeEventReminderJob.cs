using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class ClientLifeEventReminderJob
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly ILogger<ClientLifeEventReminderJob> _logger;

    public ClientLifeEventReminderJob(IPRODbContext db, IPackageEntitlementService entitlements, ILogger<ClientLifeEventReminderJob> logger)
    {
        _db = db;
        _entitlements = entitlements;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var today = DateTime.UtcNow.Date;

        var events = await _db.ClientLifeEvents
            .Include(e => e.Client)
            .Where(e => e.IsActive)
            .Take(500)
            .ToListAsync();

        foreach (var lifeEvent in events)
        {
            try
            {
                var nextOccurrence = NextOccurrence(lifeEvent.EventDate, today);
                if (lifeEvent.LastReminderYear == nextOccurrence.Year) continue;
                if (nextOccurrence.AddDays(-lifeEvent.ReminderDaysBefore) != today) continue;

                if (!await _entitlements.HasAccessAsync(lifeEvent.Client.AgentUserId, PackageFeatureCodes.LifeEventReminders))
                {
                    continue;
                }

                _db.ClientFollowUps.Add(new ClientFollowUp
                {
                    ClientId = lifeEvent.ClientId,
                    Title = $"{lifeEvent.Label}: {lifeEvent.Client.FirstName} {lifeEvent.Client.LastName}",
                    DueAt = nextOccurrence
                });
                lifeEvent.LastReminderYear = nextOccurrence.Year;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Life event reminder {LifeEventId} failed to generate a follow-up", lifeEvent.Id);
            }
        }

        var birthdayClients = await _db.Clients
            .Where(c => c.DateOfBirth != null)
            .Take(500)
            .ToListAsync();

        const int birthdayReminderDaysBefore = 7;

        foreach (var client in birthdayClients)
        {
            try
            {
                var nextBirthday = NextOccurrence(client.DateOfBirth!.Value, today);
                if (client.LastBirthdayReminderYear == nextBirthday.Year) continue;
                if (nextBirthday.AddDays(-birthdayReminderDaysBefore) != today) continue;

                if (!await _entitlements.HasAccessAsync(client.AgentUserId, PackageFeatureCodes.LifeEventReminders))
                {
                    continue;
                }

                _db.ClientFollowUps.Add(new ClientFollowUp
                {
                    ClientId = client.Id,
                    Title = $"\U0001F382 Birthday: {client.FirstName} {client.LastName}",
                    DueAt = nextBirthday
                });
                client.LastBirthdayReminderYear = nextBirthday.Year;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Birthday reminder for client {ClientId} failed to generate a follow-up", client.Id);
            }
        }

        await _db.SaveChangesAsync();
    }

    private static DateTime NextOccurrence(DateTime sourceDate, DateTime today)
    {
        var occurrence = ProjectToYear(sourceDate, today.Year);
        if (occurrence < today)
        {
            occurrence = ProjectToYear(sourceDate, today.Year + 1);
        }
        return occurrence;
    }

    private static DateTime ProjectToYear(DateTime sourceDate, int year)
    {
        var day = Math.Min(sourceDate.Day, DateTime.DaysInMonth(year, sourceDate.Month));
        return new DateTime(year, sourceDate.Month, day);
    }
}
