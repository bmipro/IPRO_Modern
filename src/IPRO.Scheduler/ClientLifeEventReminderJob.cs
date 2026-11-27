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
            dueEvents.AddRange(batch.Where(e => DueOrSkipped("life event", e.Id, e.EventDate, e.ReminderDaysBefore, e.LastReminderYear, today)).Select(e => e.Id));
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
            dueBirthdays.AddRange(batch.Where(c => DueOrSkipped("birthday of client", c.Id, c.DateOfBirth!.Value, BirthdayReminderDaysBefore, c.LastBirthdayReminderYear, today)).Select(c => c.Id));
        }

        // A List, not the array Chunk makes: C# 14 binds an array's Contains to a span overload that EF Core 8
        // cannot translate (596, ArrayContainsQueries596Tests).
        //
        // 683 (audit 2026-11-26, W02-02 and S01-01): one adviser's row must never stop everyone's reminders. A chunk
        // is saved together; if that save fails (one row the database refuses), the chunk is done again one row at a
        // time, so the bad row is logged and left for tomorrow and every other reminder is still made.
        foreach (var ids in dueEvents.Chunk(BatchSize).Select(chunk => chunk.ToList()))
        {
            try
            {
                await RemindOfEventsAsync(ids, today);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A batch of {Count} life-event reminders could not be saved together; making them one at a time", ids.Count);
                _db.ChangeTracker.Clear();
                foreach (var id in ids)
                {
                    try
                    {
                        await RemindOfEventsAsync(new List<int> { id }, today);
                    }
                    catch (Exception one)
                    {
                        _logger.LogError(one, "Life event reminder {LifeEventId} could not be made; it is tried again tomorrow", id);
                        _db.ChangeTracker.Clear();
                    }
                }
            }
        }

        foreach (var ids in dueBirthdays.Chunk(BatchSize).Select(chunk => chunk.ToList()))
        {
            try
            {
                await RemindOfBirthdaysAsync(ids, today);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A batch of {Count} birthday reminders could not be saved together; making them one at a time", ids.Count);
                _db.ChangeTracker.Clear();
                foreach (var id in ids)
                {
                    try
                    {
                        await RemindOfBirthdaysAsync(new List<int> { id }, today);
                    }
                    catch (Exception one)
                    {
                        _logger.LogError(one, "Birthday reminder for client {ClientId} could not be made; it is tried again tomorrow", id);
                        _db.ChangeTracker.Clear();
                    }
                }
            }
        }
    }

    private async Task RemindOfEventsAsync(List<int> ids, DateTime today)
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
                    Title = FollowUpTitle($"{lifeEvent.Label}: {lifeEvent.Client.FirstName} {lifeEvent.Client.LastName}"),
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

    private async Task RemindOfBirthdaysAsync(List<int> ids, DateTime today)
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
                    Title = FollowUpTitle($"\U0001F382 Birthday: {client.FirstName} {client.LastName}"),
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

    // A follow-up's title holds 160 characters (ClientFollowUps.Title). A label of up to 200 plus two names of up to
    // 80 each did not fit: the database refused the row and, with it, the whole run (S01-01). Cut at a whole character.
    public const int FollowUpTitleLength = 160;

    public static string FollowUpTitle(string title)
    {
        if (title.Length <= FollowUpTitleLength) return title;
        var cut = FollowUpTitleLength - 3;
        if (char.IsHighSurrogate(title[cut - 1])) cut--;
        return title[..cut].TrimEnd() + "...";
    }

    // The scan's question for one row, never an error: a row whose dates cannot be worked out is logged and skipped.
    private bool DueOrSkipped(string what, int id, DateTime date, int reminderDaysBefore, int? lastReminderYear, DateTime today)
    {
        try
        {
            return IsDue(date, reminderDaysBefore, lastReminderYear, today);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The reminder date of {What} {Id} could not be worked out; the row is skipped", what, id);
            return false;
        }
    }

    // Due: this year's (or next year's) occurrence has not been reminded yet, and today is inside its window.
    // Catch-up safe: once the window opens it stays due until a reminder is made, so a missed run never skips
    // a year's reminder; LastReminderYear is what prevents a repeat.
    public static bool IsDue(DateTime date, int reminderDaysBefore, int? lastReminderYear, DateTime today)
    {
        var next = NextOccurrence(date, today);
        if (lastReminderYear == next.Year) return false;
        // W02-02: never more than a year ahead, whatever the row holds (a stored two billion made AddDays throw).
        return today >= next.AddDays(-Math.Clamp(reminderDaysBefore, 0, 366));
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
