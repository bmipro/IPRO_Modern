using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class GoogleCalendarSyncJob
{
    private readonly IPRODbContext _db;
    private readonly IGoogleCalendarService _googleCalendar;
    private readonly IDataProtector _tokenProtector;
    private readonly ILogger<GoogleCalendarSyncJob> _logger;
    // 608: who is told when the adviser moves a client's appointment in Google (the web app; none in a bare run).
    private readonly IAppointmentMoveNotifier? _moveNotifier;
    private readonly List<(int FollowUpId, DateTime From, DateTime To)> _moved = new();

    public GoogleCalendarSyncJob(IPRODbContext db, IGoogleCalendarService googleCalendar, IDataProtectionProvider dataProtectionProvider, ILogger<GoogleCalendarSyncJob> logger, IAppointmentMoveNotifier? moveNotifier = null)
    {
        _moveNotifier = moveNotifier;
        _db = db;
        _googleCalendar = googleCalendar;
        _tokenProtector = dataProtectionProvider.CreateProtector("IPRO.Web.GoogleCalendar.Tokens.v1");
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var connections = await _db.GoogleCalendarConnections.Where(c => c.IsActive).ToListAsync();

        foreach (var connection in connections)
        {
            try
            {
                await SyncConnectionAsync(connection);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Google Calendar sync failed for agent {AgentUserId}", connection.AgentUserId);
            }
        }
    }

    private async Task SyncConnectionAsync(GoogleCalendarConnection connection)
    {
        _moved.Clear();
        var accessToken = await EnsureFreshAccessTokenAsync(connection);

        // 601 (2026-10-24): the adviser's own zone (Profile), the same one every portal page uses for "today".
        // A follow-up's DueAt is a wall clock in it and is never converted in the database (INVARIANTS rule 10);
        // only what goes to Google, and what comes back, is turned to and from Google's instants with it.
        var agentTimeZone = await _db.AgentUsers.AsNoTracking()
            .Where(a => a.Id == connection.AgentUserId)
            .Select(a => a.TimeZone)
            .FirstOrDefaultAsync();
        var zone = AgentLocalTime.Zone(agentTimeZone);

        await PushIproFollowUpsToGoogleAsync(connection, accessToken, zone);
        await PullGoogleChangesIntoIproAsync(connection, accessToken, zone);
        await NotifyMovesAsync(connection);

        connection.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task<string> EnsureFreshAccessTokenAsync(GoogleCalendarConnection connection)
    {
        if (connection.AccessTokenExpiresAt > DateTime.UtcNow.AddMinutes(5))
        {
            return _tokenProtector.Unprotect(connection.EncryptedAccessToken);
        }

        var refreshToken = _tokenProtector.Unprotect(connection.EncryptedRefreshToken);
        var (accessToken, expiresAt) = await _googleCalendar.RefreshAccessTokenAsync(refreshToken);
        connection.EncryptedAccessToken = _tokenProtector.Protect(accessToken);
        connection.AccessTokenExpiresAt = expiresAt;
        await _db.SaveChangesAsync();
        return accessToken;
    }

    private async Task PushIproFollowUpsToGoogleAsync(GoogleCalendarConnection connection, string accessToken, TimeZoneInfo zone)
    {
        var unsyncedFollowUps = await _db.ClientFollowUps
            .Include(f => f.Client)
            // 493: GoogleUnlinkedAt marks a follow-up the adviser deleted from Google (the pull below
            // unlinks it rather than deleting the IPRO record). Without it the unlink put the follow-up
            // straight back into this query, and the event came back fifteen minutes later, forever.
            .Where(f => f.Client.AgentUserId == connection.AgentUserId && f.GoogleEventId == null && f.GoogleUnlinkedAt == null && !f.IsCompleted)
            .OrderBy(f => f.DueAt)
            .Take(100)
            .ToListAsync();

        var anySynced = false;
        foreach (var followUp in unsyncedFollowUps)
        {
            try
            {
                // 601: DueAt as stored -- the adviser's wall clock -- with their zone; never ToUtc'd first.
                var googleEventId = await _googleCalendar.CreateEventAsync(accessToken, connection.GoogleCalendarId, followUp.Title, followUp.DueAt, null, zone);
                followUp.GoogleEventId = googleEventId;
                anySynced = true;
            }
            catch (Exception ex)
            {
                // Isolate per follow-up: if this one call throws, earlier follow-ups in this batch
                // already have a real Google event created and their GoogleEventId set in memory -
                // letting the exception propagate here would skip the SaveChangesAsync below and lose
                // that event ID, causing a duplicate Google event to be created for them next run.
                _logger.LogError(ex, "Google Calendar event creation failed for follow-up {FollowUpId}, agent {AgentUserId}", followUp.Id, connection.AgentUserId);
            }
        }

        if (anySynced)
        {
            await _db.SaveChangesAsync();
        }
    }

    // 601 (2026-10-24): how the pull knows what changed in Google. There is no per-event comparison (no 'updated'
    // timestamp, no etag): the sync token makes Google list only the events changed since the last run, and every
    // listed event linked to a follow-up has always overwritten its title and due time. Two consequences:
    // - an event the sync itself just wrote comes straight back in the next listing, so reading it back must give
    //   exactly the stored DueAt (ApplyGoogleTime returns early when it does); and
    // - "listed" is not "moved by the adviser". The events written before 601 sit in Google shifted by the
    //   adviser's UTC offset. They are listed again whenever the adviser touches one, whenever Google expires the
    //   token, on a reconnect -- and once, on purpose, right after the release (below). Read naively they would
    //   look like moves and drag every DueAt by four or five hours, the very thing TRUTH_SWEEP_2026-09-18 item 11
    //   warned about. ApplyGoogleTime recognises them instead and re-writes them from the stored DueAt.
    private async Task PullGoogleChangesIntoIproAsync(GoogleCalendarConnection connection, string accessToken, TimeZoneInfo zone)
    {
        // 601: one full read per connection after the release, whatever token it holds. With the token alone the
        // old events would only be put right when the adviser happened to touch them; a full read (the past 30
        // days to a year ahead, the same window as a first sync) finds them all now, and also re-stores the copies
        // of the adviser's own events at their own time. Marked done only once the read has been applied, so a
        // run that fails part-way does it again.
        var syncToken = connection.LocalTimesResyncedAt == null ? null : connection.SyncToken;
        var result = await _googleCalendar.ListEventsAsync(accessToken, connection.GoogleCalendarId, syncToken);

        if (result.RequiresFullResync)
        {
            // The stored sync token was rejected by Google (expired/invalid) - clear it so the
            // next run performs a fresh full sync instead of failing repeatedly.
            connection.SyncToken = null;
            _logger.LogWarning("Google Calendar sync token expired for agent {AgentUserId}; will re-sync fully next run", connection.AgentUserId);
            return;
        }

        foreach (var googleEvent in result.Events)
        {
            var linkedFollowUp = await _db.ClientFollowUps
                .Include(f => f.Client)
                .FirstOrDefaultAsync(f => f.Client.AgentUserId == connection.AgentUserId && f.GoogleEventId == googleEvent.GoogleEventId);

            if (linkedFollowUp != null)
            {
                if (googleEvent.IsCancelled)
                {
                    // Unlink rather than delete - a follow-up is CRM history, not just a calendar
                    // block, so vanishing from Google shouldn't silently destroy the IPRO record.
                    linkedFollowUp.GoogleEventId = null;
                    linkedFollowUp.GoogleUnlinkedAt = DateTime.UtcNow;   // 493: and it stays off Google
                }
                else
                {
                    linkedFollowUp.Title = googleEvent.Title;
                    await ApplyGoogleTimeAsync(linkedFollowUp, googleEvent, connection, accessToken, zone);
                }
                continue;
            }

            var externalEvent = await _db.ExternalCalendarEvents
                .FirstOrDefaultAsync(e => e.AgentUserId == connection.AgentUserId && e.GoogleEventId == googleEvent.GoogleEventId);

            if (googleEvent.IsCancelled)
            {
                if (externalEvent != null)
                {
                    _db.ExternalCalendarEvents.Remove(externalEvent);
                }
                continue;
            }

            // 601: copies of the adviser's own Google events are kept at their wall clock too -- the Calendar page
            // files them under StartAt.Date as the adviser's day, so a 9 p.m. dinner stored as UTC showed the next day.
            var startAt = AdviserTime(googleEvent.StartAt, googleEvent.IsAllDay, zone);
            DateTime? endAt = googleEvent.EndAt.HasValue ? AdviserTime(googleEvent.EndAt.Value, googleEvent.IsAllDay, zone) : null;

            if (externalEvent == null)
            {
                await _db.ExternalCalendarEvents.AddAsync(new ExternalCalendarEvent
                {
                    AgentUserId = connection.AgentUserId,
                    GoogleEventId = googleEvent.GoogleEventId,
                    Title = googleEvent.Title,
                    StartAt = startAt,
                    EndAt = endAt,
                    LastSyncedAt = DateTime.UtcNow
                });
            }
            else
            {
                externalEvent.Title = googleEvent.Title;
                externalEvent.StartAt = startAt;
                externalEvent.EndAt = endAt;
                externalEvent.LastSyncedAt = DateTime.UtcNow;
            }
        }

        if (!string.IsNullOrWhiteSpace(result.NextSyncToken))
        {
            connection.SyncToken = result.NextSyncToken;
        }

        connection.LocalTimesResyncedAt ??= DateTime.UtcNow;   // 601: the full read above is done
        await _db.SaveChangesAsync();
    }

    // 601 (2026-10-24): a linked event's time, from Google into the follow-up. Three cases, in this order:
    // 1. It reads back as the stored DueAt: the sync's own event (or an untouched one). Nothing to do.
    // 2. It is exactly where the code before 601 put it -- timeZone "UTC" (or none) and its instant equal to DueAt
    //    read as UTC. The stored DueAt is the truth: it is what every iPro page has shown the adviser all along.
    //    The follow-up keeps it, and the event is re-written in Google at the adviser's own time (all-day for a
    //    date with no time), keeping the length it had. If the re-write fails it is only logged: the DueAt is
    //    still right, and the next time Google lists the event it is tried again.
    //    Note: under the old code a move made in Google was itself stored as the event's instant read as UTC, so
    //    such an event also matches here and stays where iPro already shows it (the iPro time, not the drag).
    // 3. Anything else is the adviser's own move in Google, seen in their own calendar: it becomes the DueAt at
    //    their wall clock (an old-shaped event dragged elsewhere included). An all-day event is a bare date.
    private async Task ApplyGoogleTimeAsync(ClientFollowUp followUp, GoogleCalendarEventData googleEvent, GoogleCalendarConnection connection, string accessToken, TimeZoneInfo zone)
    {
        var adviserTime = AdviserTime(googleEvent.StartAt, googleEvent.IsAllDay, zone);
        if (SameSecond(adviserTime, followUp.DueAt)) return;

        if (WrittenBefore601(googleEvent, followUp.DueAt))
        {
            DateTime? localEnd = null;
            if (googleEvent.EndAt is { } endUtc && endUtc > googleEvent.StartAt)
            {
                localEnd = followUp.DueAt + (endUtc - googleEvent.StartAt);
            }

            try
            {
                await _googleCalendar.UpdateEventAsync(accessToken, connection.GoogleCalendarId, googleEvent.GoogleEventId, followUp.Title, followUp.DueAt, localEnd, zone);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Google Calendar event {GoogleEventId} written before 601 could not be re-written at the adviser's time (follow-up {FollowUpId}, agent {AgentUserId}); the follow-up keeps its time", googleEvent.GoogleEventId, followUp.Id, connection.AgentUserId);
            }
            return;
        }

        _moved.Add((followUp.Id, followUp.DueAt, adviserTime));   // 608: told after the pull has saved it
        followUp.DueAt = adviserTime;
    }

    // 608: each move the pull saved is offered to the web app, which writes to the client when the follow-up is
    // their appointment. A letter that fails is logged; it never undoes the move or stops the sync.
    private async Task NotifyMovesAsync(GoogleCalendarConnection connection)
    {
        if (_moveNotifier != null)
        {
            foreach (var (followUpId, from, to) in _moved)
            {
                try
                {
                    await _moveNotifier.NotifyAsync(followUpId, from, to);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Telling the client that follow-up {FollowUpId} moved failed (agent {AgentUserId}); the move stands", followUpId, connection.AgentUserId);
                }
            }
        }
        _moved.Clear();
    }

    // The shape the code before 601 wrote: DueAt sent as a UTC instant with timeZone "UTC". The sync since 601
    // always names the adviser's zone, so an event of its own never matches (case 1 above catches an adviser
    // whose zone IS UTC first).
    public static bool WrittenBefore601(GoogleCalendarEventData googleEvent, DateTime dueAt) =>
        !googleEvent.IsAllDay
        && (googleEvent.TimeZone is null or "UTC" or "Etc/UTC" or "GMT" or "Etc/GMT")
        && SameSecond(googleEvent.StartAt, dueAt);

    // Google's instant -> the adviser's wall clock; an all-day date is already the adviser's date.
    private static DateTime AdviserTime(DateTime googleTime, bool allDay, TimeZoneInfo zone) =>
        allDay
            ? DateTime.SpecifyKind(googleTime.Date, DateTimeKind.Unspecified)
            : DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(googleTime, DateTimeKind.Utc), zone), DateTimeKind.Unspecified);

    // Google keeps whole seconds; a stored DueAt can carry fractions (a value typed with seconds, a test's Now).
    private static bool SameSecond(DateTime a, DateTime b) =>
        a.Ticks / TimeSpan.TicksPerSecond == b.Ticks / TimeSpan.TicksPerSecond;
}
