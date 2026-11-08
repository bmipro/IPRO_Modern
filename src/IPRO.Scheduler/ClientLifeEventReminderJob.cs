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

    // How many rows are read at a time while every row is looked at, and how many due rows are changed per save.
    public const int BatchSize = 1000;
    public const int BirthdayReminderDaysBefore = 7;

    public ClientLifeEventReminderJob(IPRODbContext db, IPackageEntitlementService entitlements, ILogger<ClientLifeEventReminderJob> logger)
    {
        _db = db;
        _entitlements = entitlements;
        _logger = logger;
    }

    public Task RunAsync() => RunForDayAsync(DateTime.UtcNow.Date);

    // 650 (634's first, built 2026-11-08 on the owner's word). This job used to read the 500 life events and the
    // 500 clients with a birth date that it had checked longest ago -- across the whole platform -- and check
    // only those. A reminder's window is a week, so once there were more than about 4,000 clients with a birth
    // date (all advisers together) a client's turn came round less often than once a window and reminders were
    // silently missed: about 60% of them at 10,000. Now every active row is looked at every day: a light read
    // of the few columns the decision needs, in batches by Id, and only the rows due today are loaded and
    // changed. The decision itself is unchanged (NextOccurrence, catch-up safe, LastReminderYear stops a
    // repeat), and the rows that are not due are no longer written at all; the LastCheckedAt columns that
    // ordered the rotation are kept but no longer written. A life event whose client was deleted (the table has
    // no foreign key) is still left out in the query, so it can never stop the run.
    public async Task RunForDayAsync(DateTime today)
    {
        today = today.Date;

        var dueEvents = new List<int>();
        for (var lastId = 0; ;)
        {
            var batch = await _db.ClientLifeEvents.AsNoTracking()
                .Where(e => e.Id > lastId && e.IsActive && e.Client != null)
                .OrderBy(e => e.Id)
                .Select(e => new { e.Id, e.EventDate, e.ReminderDaysBefore, e.LastReminderYear })
                .Take(BatchSize)
                .ToListAsync();
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;
            dueEvents.AddRange(batch.Where(e => IsDue(e.EventDate, e.ReminderDaysBefore, e.LastReminderYear, today)).Select(e => e.Id));
        }

        var dueBirthdays = new List<int>();
        for (var lastId = 0; ;)
        {
            var batch = await _db.Clients.AsNoTracking()
                .Where(c => c.Id > lastId && c.DateOfBirth != null)
                .OrderBy(c => c.Id)
                .Select(c => new { c.Id, c.DateOfBirth, c.LastBirthdayReminderYear })
                .Take(BatchSize)
                .ToListAsync();
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;
            dueBirthdays.AddRange(batch.Where(c => IsDue(c.DateOfBirth!.Value, BirthdayReminderDaysBefore, c.LastBirthdayReminderYear, today)).Select(c => c.Id));
        }

        // A List, not the array Chunk makes: C# 14 binds an array's Contains to a span overload that EF Core 8
        // cannot translate (596, ArrayContainsQueries596Tests).
        foreach (var ids in dueEvents.Chunk(BatchSize).Select(chunk => chunk.ToList()))
        {
            var events = await _db.ClientLifeEvents
                .Include(e => e.Client)
                .Where(e => ids.Contains(e.Id) && e.IsActive && e.Client != null)
                .ToListAsync();
            var access = await _entitlements.HasAccessBulkAsync(events.Select(e => e.Client.AgentUserId), PackageFeatureCodes.LifeEventReminders);
            foreach (var lifeEvent in events)
            {
                try
                {
                    var nextOccurrence = NextOccurrence(lifeEvent.EventDate, today);
                    if (!IsDue(lifeEvent.EventDate, lifeEvent.ReminderDaysBefore, lifeEvent.LastReminderYear, today)) continue;
                    // Not entitled: nothing is written, so the reminder is still due tomorrow, as before.
                    if (!access.GetValueOrDefault(lifeEvent.Client.AgentUserId)) continue;

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
            await _db.SaveChangesAsync();
            _db.ChangeTracker.Clear();
        }

        foreach (var ids in dueBirthdays.Chunk(BatchSize).Select(chunk => chunk.ToList()))
        {
            var clients = await _db.Clients
                .Where(c => ids.Contains(c.Id) && c.DateOfBirth != null)
                .ToListAsync();
            var access = await _entitlements.HasAccessBulkAsync(clients.Select(c => c.AgentUserId), PackageFeatureCodes.LifeEventReminders);
            foreach (var client in clients)
            {
                try
                {
                    var nextBirthday = NextOccurrence(client.DateOfBirth!.Value, today);
                    if (!IsDue(client.DateOfBirth!.Value, BirthdayReminderDaysBefore, client.LastBirthdayReminderYear, today)) continue;
                    if (!access.GetValueOrDefault(client.AgentUserId)) continue;

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
            _db.ChangeTracker.Clear();
        }
    }

    // Due: this year's (or next year's) occurrence has not been reminded yet, and today is inside its window.
    // Catch-up safe: once the window opens it stays due until a reminder is made, so a missed run never skips
    // a year's reminder; LastReminderYear is what prevents a repeat.
    public static bool IsDue(DateTime date, int reminderDaysBefore, int? lastReminderYear, DateTime today)
    {
        var next = NextOccurrence(date, today);
        if (lastReminderYear == next.Year) return false;
        return today >= next.AddDays(-reminderDaysBefore);
    }

    public static DateTime NextOccurrence(DateTime sourceDate, DateTime today)
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
