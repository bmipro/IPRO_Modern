using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class GoogleCalendarController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IGoogleCalendarService _googleCalendar;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IDataProtector _stateProtector;
    private readonly IDataProtector _tokenProtector;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public GoogleCalendarController(IPRODbContext db, IGoogleCalendarService googleCalendar, IPackageEntitlementService entitlements, IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _googleCalendar = googleCalendar;
        _entitlements = entitlements;
        _stateProtector = dataProtectionProvider.CreateProtector("IPRO.Web.GoogleCalendar.State.v1");
        _tokenProtector = dataProtectionProvider.CreateProtector("IPRO.Web.GoogleCalendar.Tokens.v1");
    }

    public async Task<IActionResult> Connect()
    {
        var gate = await RequireGoogleCalendarAccessAsync();
        if (gate != null) return gate;

        var redirectUri = Url.ActionLink(nameof(Callback))!;
        var state = _stateProtector.Protect($"{AgentId}|{DateTime.UtcNow.Ticks}");
        return Redirect(_googleCalendar.BuildAuthorizationUrl(redirectUri, state));
    }

    public async Task<IActionResult> Callback(string? code, string? state, string? error)
    {
        var gate = await RequireGoogleCalendarAccessAsync();
        if (gate != null) return gate;

        if (!string.IsNullOrWhiteSpace(error))
        {
            TempData["Error"] = "Google Calendar connection was cancelled or denied.";
            return RedirectToAction("Profile", "Account");
        }

        int agentIdFromState;
        try
        {
            var unprotected = _stateProtector.Unprotect(state ?? string.Empty);
            var parts = unprotected.Split('|');
            agentIdFromState = int.Parse(parts[0]);
            var ticks = long.Parse(parts[1]);
            if (DateTime.UtcNow.Ticks - ticks > TimeSpan.FromMinutes(10).Ticks)
            {
                TempData["Error"] = "The Google Calendar connection request expired. Please try again.";
                return RedirectToAction("Profile", "Account");
            }
        }
        catch
        {
            TempData["Error"] = "The Google Calendar connection request was invalid. Please try again.";
            return RedirectToAction("Profile", "Account");
        }

        if (agentIdFromState != AgentId)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            TempData["Error"] = "Google did not return an authorization code.";
            return RedirectToAction("Profile", "Account");
        }

        try
        {
            var redirectUri = Url.ActionLink(nameof(Callback))!;
            var tokenResult = await _googleCalendar.ExchangeCodeAsync(code, redirectUri);

            var existing = await _db.GoogleCalendarConnections.FirstOrDefaultAsync(c => c.AgentUserId == AgentId);
            if (existing == null)
            {
                existing = new GoogleCalendarConnection { AgentUserId = AgentId, ConnectedAt = DateTime.UtcNow };
                await _db.GoogleCalendarConnections.AddAsync(existing);
            }

            existing.GoogleAccountEmail = tokenResult.AccountEmail;
            existing.EncryptedAccessToken = _tokenProtector.Protect(tokenResult.AccessToken);
            existing.EncryptedRefreshToken = _tokenProtector.Protect(tokenResult.RefreshToken);
            existing.AccessTokenExpiresAt = tokenResult.ExpiresAt;
            existing.IsActive = true;
            existing.SyncToken = null;
            await _db.SaveChangesAsync();

            TempData["Success"] = $"Connected to Google Calendar as {tokenResult.AccountEmail}.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction("Profile", "Account");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect()
    {
        var connection = await _db.GoogleCalendarConnections.FirstOrDefaultAsync(c => c.AgentUserId == AgentId);
        if (connection != null)
        {
            try
            {
                var refreshToken = _tokenProtector.Unprotect(connection.EncryptedRefreshToken);
                await _googleCalendar.RevokeTokenAsync(refreshToken);
            }
            catch
            {
                // Best-effort revoke; still remove the local connection either way.
            }

            _db.GoogleCalendarConnections.Remove(connection);
            await _db.SaveChangesAsync();
        }

        TempData["Success"] = "Google Calendar disconnected.";
        return RedirectToAction("Profile", "Account");
    }

    private async Task<IActionResult?> RequireGoogleCalendarAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.GoogleCalendarSync);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Profile", "Account");
    }
}
