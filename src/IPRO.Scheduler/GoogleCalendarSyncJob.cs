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

    public GoogleCalendarSyncJob(IPRODbContext db, IGoogleCalendarService googleCalendar, IDataProtectionProvider dataProtectionProvider, ILogger<GoogleCalendarSyncJob> logger)
    {
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
        var accessToken = await EnsureFreshAccessTokenAsync(connection);

        await PushIproFollowUpsToGoogleAsync(connection, accessToken);
        await PullGoogleChangesIntoIproAsync(connection, accessToken);

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

    private async Task PushIproFollowUpsToGoogleAsync(GoogleCalendarConnection connection, string accessToken)
    {
        var unsyncedFollowUps = await _db.ClientFollowUps
            .Include(f => f.Client)
            .Where(f => f.Client.AgentUserId == connection.AgentUserId && f.GoogleEventId == null && !f.IsCompleted)
            .Take(100)
            .ToListAsync();

        foreach (var followUp in unsyncedFollowUps)
        {
            var googleEventId = await _googleCalendar.CreateEventAsync(accessToken, connection.GoogleCalendarId, followUp.Title, followUp.DueAt, null);
            followUp.GoogleEventId = googleEventId;
        }

        if (unsyncedFollowUps.Count > 0)
        {
            await _db.SaveChangesAsync();
        }
    }

    private async Task PullGoogleChangesIntoIproAsync(GoogleCalendarConnection connection, string accessToken)
    {
        var result = await _googleCalendar.ListEventsAsync(accessToken, connection.GoogleCalendarId, connection.SyncToken);

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
                }
                else
                {
                    linkedFollowUp.Title = googleEvent.Title;
                    linkedFollowUp.DueAt = googleEvent.StartAt;
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

            if (externalEvent == null)
            {
                await _db.ExternalCalendarEvents.AddAsync(new ExternalCalendarEvent
                {
                    AgentUserId = connection.AgentUserId,
                    GoogleEventId = googleEvent.GoogleEventId,
                    Title = googleEvent.Title,
                    StartAt = googleEvent.StartAt,
                    EndAt = googleEvent.EndAt,
                    LastSyncedAt = DateTime.UtcNow
                });
            }
            else
            {
                externalEvent.Title = googleEvent.Title;
                externalEvent.StartAt = googleEvent.StartAt;
                externalEvent.EndAt = googleEvent.EndAt;
                externalEvent.LastSyncedAt = DateTime.UtcNow;
            }
        }

        if (!string.IsNullOrWhiteSpace(result.NextSyncToken))
        {
            connection.SyncToken = result.NextSyncToken;
        }

        await _db.SaveChangesAsync();
    }
}
