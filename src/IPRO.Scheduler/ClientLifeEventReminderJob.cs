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

        // Ordered by LastCheckedAt (oldest-checked-first, nulls -- never checked -- come first via
        // CreatedAt) so that if there are ever more active rows than the cap, every row still gets
        // a turn on a rotating basis instead of the same 500 winning every single day forever.
        var events = await _db.ClientLifeEvents
            .Include(e => e.Client)
            .Where(e => e.IsActive)
            .OrderBy(e => e.LastCheckedAt ?? e.CreatedAt)
            .Take(500)
            .ToListAsync();

        var birthdayClients = await _db.Clients
            .Where(c => c.DateOfBirth != null)
            .OrderBy(c => c.BirthdayReminderLastCheckedAt ?? c.CreatedAt)
            .Take(500)
            .ToListAsync();

        // Batched instead of one HasAccessAsync call per row (up to 1000 rows here) - see
        // PackageEntitlementService.HasAccessBulkAsync.
        var accessByAgent = await _entitlements.HasAccessBulkAsync(
            events.Select(e => e.Client.AgentUserId).Concat(birthdayClients.Select(c => c.AgentUserId)),
            PackageFeatureCodes.LifeEventReminders);

        foreach (var lifeEvent in events)
        {
            try
            {
                lifeEvent.LastCheckedAt = DateTime.UtcNow;

                var nextOccurrence = NextOccurrence(lifeEvent.EventDate, today);
                if (lifeEvent.LastReminderYear == nextOccurrence.Year) continue;
                // Catch-up safe: fire once we've reached the reminder window rather than only on
                // the exact day, so a single missed run doesn't silently skip this year's reminder
                // entirely. LastReminderYear (checked above) is what actually prevents a repeat.
                if (today < nextOccurrence.AddDays(-lifeEvent.ReminderDaysBefore)) continue;

                if (!accessByAgent.GetValueOrDefault(lifeEvent.Client.AgentUserId))
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

        const int birthdayReminderDaysBefore = 7;

        foreach (var client in birthdayClients)
        {
            try
            {
                client.BirthdayReminderLastCheckedAt = DateTime.UtcNow;

                var nextBirthday = NextOccurrence(client.DateOfBirth!.Value, today);
                if (client.LastBirthdayReminderYear == nextBirthday.Year) continue;
                // Catch-up safe, same reasoning as the life-event check above.
                if (today < nextBirthday.AddDays(-birthdayReminderDaysBefore)) continue;

                if (!accessByAgent.GetValueOrDefault(client.AgentUserId))
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
