using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 490 (2026-09-15). The owner disconnected his Google Calendar after recording the verification video and
// found the Google events still on the IPRO Calendar page. Disconnect revoked and deleted the credentials
// but left the copies the sync had pulled in, and the confirmation text even said so ("Events already
// synced will stay as-is"). A person who disconnects expects their Google data to leave with them, the
// privacy policy reads that way, and Google's reviewer watches exactly this step. Now Disconnect removes
// the copies too; follow-ups are IPRO records and stay, GoogleEventId included, so a reconnect does not
// push them to Google a second time. The Calendar page shows copies only while a connection exists, which
// also hides anything left behind by a disconnect from before 490.
public class GoogleCalendarDisconnectTests
{
    [Fact]
    public async Task Disconnect_removes_the_connection_and_the_events_copied_from_google()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var (agentA, agentB, followUpId) = await SeedAsync(db, provider);

        var google = new StubGoogle();
        var controller = NewGoogleController(db, google, provider, agentA);
        var result = Assert.IsType<RedirectToActionResult>(await controller.Disconnect());
        Assert.Equal("Profile", result.ActionName);

        db.ChangeTracker.Clear();
        Assert.False(await db.GoogleCalendarConnections.AnyAsync(c => c.AgentUserId == agentA));
        Assert.Equal(0, await db.ExternalCalendarEvents.CountAsync(e => e.AgentUserId == agentA));
        // Another adviser's copies are not touched.
        Assert.Equal(1, await db.ExternalCalendarEvents.CountAsync(e => e.AgentUserId == agentB));
        // The follow-up is an IPRO record: it stays, and it keeps the link to the Google event it already has.
        var followUp = await db.ClientFollowUps.AsNoTracking().SingleAsync(f => f.Id == followUpId);
        Assert.Equal("g-follow-1", followUp.GoogleEventId);
        // Google was asked to revoke the refresh token, and the message says what happened.
        Assert.Equal("refresh-token", google.RevokedToken);
        Assert.Contains("copied from Google", (string)controller.TempData["Success"]!);
    }

    [Fact]
    public async Task Disconnect_still_removes_everything_when_google_refuses_the_revoke()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var (agentA, _, _) = await SeedAsync(db, provider);

        var controller = NewGoogleController(db, new StubGoogle { RevokeThrows = true }, provider, agentA);
        Assert.IsType<RedirectToActionResult>(await controller.Disconnect());

        db.ChangeTracker.Clear();
        Assert.False(await db.GoogleCalendarConnections.AnyAsync(c => c.AgentUserId == agentA));
        Assert.Equal(0, await db.ExternalCalendarEvents.CountAsync(e => e.AgentUserId == agentA));
    }

    [Fact]
    public async Task The_calendar_page_shows_copied_google_events_only_while_connected()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var (agentA, _, _) = await SeedAsync(db, provider);
        var today = DateTime.Today;

        // Connected: the two copies show.
        var connected = NewClientsController(db, provider, agentA);
        Assert.IsType<ViewResult>(await connected.Calendar(today.Year, today.Month));
        Assert.Equal(2, ((IEnumerable<ExternalCalendarEvent>)connected.ViewBag.ExternalEvents).Count());

        // Copies left behind with no connection (a disconnect from before 490): none show.
        db.GoogleCalendarConnections.RemoveRange(db.GoogleCalendarConnections.Where(c => c.AgentUserId == agentA));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var disconnected = NewClientsController(db, provider, agentA);
        Assert.IsType<ViewResult>(await disconnected.Calendar(today.Year, today.Month));
        Assert.Empty((IEnumerable<ExternalCalendarEvent>)disconnected.ViewBag.ExternalEvents);
    }

    [Fact]
    public void The_confirmation_says_what_disconnect_now_does()
    {
        var profile = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Account\Profile.cshtml"));
        Assert.Contains("removed from your IPRO calendar", profile);
        Assert.DoesNotContain("stay as-is", profile);
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\GoogleCalendarController.cs"));
        Assert.Contains("ExternalCalendarEvents.RemoveRange", controller);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static async Task<(int AgentA, int AgentB, int FollowUpId)> SeedAsync(IPRODbContext db, IDataProtectionProvider provider)
    {
        var agentA = NewAgent("gcd-a");
        var agentB = NewAgent("gcd-b");
        db.AddRange(agentA, agentB);
        await db.SaveChangesAsync();

        var client = new Client { AgentUserId = agentA.Id, FirstName = "Cal", LastName = "Client", Email = $"cal-{Guid.NewGuid():N}"[..12] + "@example.test" };
        db.Add(client);
        await db.SaveChangesAsync();

        var today = DateTime.Today;
        var mid = new DateTime(today.Year, today.Month, 15, 10, 0, 0, DateTimeKind.Utc);
        var followUp = new ClientFollowUp { ClientId = client.Id, Title = "Client review", DueAt = mid, GoogleEventId = "g-follow-1" };
        db.Add(followUp);

        var tokens = provider.CreateProtector("IPRO.Web.GoogleCalendar.Tokens.v1");
        db.Add(new GoogleCalendarConnection
        {
            AgentUserId = agentA.Id,
            GoogleAccountEmail = "adviser@example.test",
            EncryptedAccessToken = tokens.Protect("access-token"),
            EncryptedRefreshToken = tokens.Protect("refresh-token"),
            AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        db.AddRange(
            new ExternalCalendarEvent { AgentUserId = agentA.Id, GoogleEventId = "g-ext-1", Title = "Dentist", StartAt = mid },
            new ExternalCalendarEvent { AgentUserId = agentA.Id, GoogleEventId = "g-ext-2", Title = "Mom B12", StartAt = mid.AddDays(1) },
            new ExternalCalendarEvent { AgentUserId = agentB.Id, GoogleEventId = "g-ext-3", Title = "Someone else's", StartAt = mid });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agentA.Id, agentB.Id, followUp.Id);
    }

    private static AgentUser NewAgent(string prefix) => new()
    {
        UserName = $"{prefix}-{Guid.NewGuid():N}"[..20],
        Email = $"{prefix}-{Guid.NewGuid():N}"[..12] + "@example.test",
        FirstName = "Google", LastName = "Disconnect",
        DomainName = $"{prefix}-{Guid.NewGuid():N}"[..24],
        Country = "Canada", Province = "Ontario"
    };

    private static IPRO.Web.Controllers.GoogleCalendarController NewGoogleController(IPRODbContext db, IGoogleCalendarService google, IDataProtectionProvider provider, int agentId)
    {
        var controller = new IPRO.Web.Controllers.GoogleCalendarController(db, google, new GrantAll(), new ConfigurationBuilder().Build(), provider);
        Wire(controller, agentId);
        return controller;
    }

    private static IPRO.Web.Controllers.ClientsController NewClientsController(IPRODbContext db, IDataProtectionProvider provider, int agentId)
    {
        var controller = new IPRO.Web.Controllers.ClientsController(
            null!, null!, null!, db, new GrantAll(), null!, null!, new StubGoogle(), provider, new ConfigurationBuilder().Build());
        Wire(controller, agentId);
        return controller;
    }

    private static void Wire(Controller controller, int agentId)
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }

    private sealed class StubGoogle : IGoogleCalendarService
    {
        public bool RevokeThrows { get; init; }
        public string? RevokedToken { get; private set; }
        public string BuildAuthorizationUrl(string redirectUri, string state) => throw new NotSupportedException();
        public Task<GoogleTokenResult> ExchangeCodeAsync(string code, string redirectUri) => throw new NotSupportedException();
        public Task<(string AccessToken, DateTime ExpiresAt)> RefreshAccessTokenAsync(string refreshToken) => throw new NotSupportedException();
        public Task<GoogleEventListResult> ListEventsAsync(string accessToken, string calendarId, string? syncToken) => throw new NotSupportedException();
        public Task<string> CreateEventAsync(string accessToken, string calendarId, string title, DateTime startAtUtc, DateTime? endAtUtc) => throw new NotSupportedException();
        public Task UpdateEventAsync(string accessToken, string calendarId, string googleEventId, string title, DateTime startAtUtc, DateTime? endAtUtc) => throw new NotSupportedException();
        public Task DeleteEventAsync(string accessToken, string calendarId, string googleEventId) => throw new NotSupportedException();
        public Task RevokeTokenAsync(string token)
        {
            if (RevokeThrows) throw new InvalidOperationException("Google said no");
            RevokedToken = token;
            return Task.CompletedTask;
        }
    }

    private sealed class GrantAll : IPRO.Business.Interfaces.IPackageEntitlementService
    {
        public Task<IPRO.Business.Interfaces.PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new IPRO.Business.Interfaces.PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) =>
            Task.FromResult(agentIds.Distinct().ToDictionary(a => a, _ => true));
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
