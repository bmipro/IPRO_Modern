using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Utility;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 632's second part (2026-11-24, the owner: "yes go ahead with 632"): the three Google Calendar findings the
// 2026-09-17 narrow audit left for after launch.
//  E-2  a Disconnect pressed while a sync was running could leave the copies of the adviser's Google events
//       behind -- the run wrote them after Disconnect had deleted them.
//  E-5  connecting a different Google account sent it nothing: the follow-ups kept the old account's event ids.
//  L-4  the sync ran for every connection, whatever the adviser's account and package.
public class GoogleCalendarSync632Tests
{
    // ---- E-2 --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_disconnect_while_the_run_reads_google_keeps_none_of_the_runs_copies()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var agent = await SeedAgentAsync(db, "e2-a");
        await SeedConnectionAsync(db, provider, agent, "adviser@example.test");

        var google = new StubGoogle { Events = { new("g-ext-new", "Dentist", DateTime.UtcNow.AddDays(2), null, IsCancelled: false) } };
        // The adviser presses Disconnect on another request while the run is reading their calendar.
        google.DuringList = async () =>
        {
            await using var page = testDb.CreateContext();
            Assert.IsType<RedirectToActionResult>(await NewGoogleController(page, google, provider, agent).Disconnect());
        };

        var log = new List<string>();
        await NewJob(db, google, provider, new GrantAll(), log).RunAsync();
        db.ChangeTracker.Clear();

        Assert.False(await db.GoogleCalendarConnections.AnyAsync(c => c.AgentUserId == agent));
        Assert.Equal(0, await db.ExternalCalendarEvents.CountAsync(e => e.AgentUserId == agent));
        Assert.DoesNotContain(log, l => l.StartsWith("Error"));
    }

    // The other order: the run is in the middle of writing, holding the connection's lock, when Disconnect
    // arrives. Disconnect waits for the write and then removes what it wrote. Before, Disconnect read the copies
    // first and removed that list, so a copy committed while it waited stayed behind.
    [Fact]
    public async Task A_disconnect_waits_for_a_write_in_progress_and_then_removes_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var agent = await SeedAgentAsync(db, "e2-b");
        var connectionId = await SeedConnectionAsync(db, provider, agent, "adviser@example.test");
        db.ExternalCalendarEvents.Add(new ExternalCalendarEvent { AgentUserId = agent, GoogleEventId = "g-old", Title = "Old copy", StartAt = DateTime.Today });
        await db.SaveChangesAsync();

        await using var run = testDb.CreateContext();
        await using var write = await run.Database.BeginTransactionAsync();
        await run.Database.ExecuteSqlInterpolatedAsync($"SELECT `Id` FROM `GoogleCalendarConnections` WHERE `Id` = {connectionId} FOR UPDATE");
        run.ExternalCalendarEvents.Add(new ExternalCalendarEvent { AgentUserId = agent, GoogleEventId = "g-late", Title = "Written by the run", StartAt = DateTime.Today.AddDays(1) });
        await run.SaveChangesAsync();

        await using var page = testDb.CreateContext();
        var disconnect = NewGoogleController(page, new StubGoogle(), provider, agent).Disconnect();
        await Task.Delay(700);
        Assert.False(disconnect.IsCompleted);   // waiting for the run's write

        await write.CommitAsync();
        Assert.IsType<RedirectToActionResult>(await disconnect);

        db.ChangeTracker.Clear();
        Assert.False(await db.GoogleCalendarConnections.AnyAsync(c => c.AgentUserId == agent));
        Assert.Equal(0, await db.ExternalCalendarEvents.CountAsync(e => e.AgentUserId == agent));
    }

    [Fact]
    public async Task A_run_that_saves_after_another_account_was_connected_keeps_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var agent = await SeedAgentAsync(db, "e2-c");
        var connectionId = await SeedConnectionAsync(db, provider, agent, "old@example.test");

        var google = new StubGoogle { Events = { new("g-old-account", "Old account's event", DateTime.UtcNow.AddDays(2), null, IsCancelled: false) } };
        // The connect callback for another account, while the run reads the old one.
        google.DuringList = async () =>
        {
            await using var page = testDb.CreateContext();
            await page.GoogleCalendarConnections.Where(c => c.Id == connectionId).ExecuteUpdateAsync(u => u
                .SetProperty(c => c.GoogleAccountEmail, "new@example.test")
                .SetProperty(c => c.SyncToken, (string?)null));
        };

        await NewJob(db, google, provider, new GrantAll(), new List<string>()).RunAsync();
        db.ChangeTracker.Clear();

        var connection = await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(c => c.Id == connectionId);
        Assert.Equal("new@example.test", connection.GoogleAccountEmail);
        Assert.Null(connection.SyncToken);           // not the old account's token
        Assert.Null(connection.LastSyncedAt);
        Assert.Equal(0, connection.FailedRuns);      // not a failure either
        Assert.Equal(0, await db.ExternalCalendarEvents.CountAsync(e => e.AgentUserId == agent));
    }

    // ---- E-5 --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Connecting_a_different_account_sends_it_the_open_follow_ups()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var agent = await SeedAgentAsync(db, "e5-a");
        var f = await SeedFollowUpsAsync(db, agent);
        db.AgentGoogleCalendarAccounts.Add(new AgentGoogleCalendarAccount { AgentUserId = agent, AccountHash = GoogleCalendarAccountMemory.Hash(agent, "old@example.test") });
        db.ExternalCalendarEvents.Add(new ExternalCalendarEvent { AgentUserId = agent, GoogleEventId = "g-old-copy", Title = "Old account's event", StartAt = DateTime.Today });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Disconnected earlier; now connects another account.
        var google = new StubGoogle { Account = "new@example.test" };
        var controller = NewGoogleController(db, google, provider, agent);
        Assert.IsType<RedirectToActionResult>(await controller.Callback("code", State(provider, agent), null));
        Assert.Contains("different Google account", (string)controller.TempData["Success"]!);

        db.ChangeTracker.Clear();
        Assert.Null((await Reload(db, f.Open)).GoogleEventId);
        Assert.Null((await Reload(db, f.Completed)).GoogleEventId);
        Assert.NotNull((await Reload(db, f.CancelledInGoogle)).GoogleUnlinkedAt);   // the adviser's choice stands
        Assert.Equal(0, await db.ExternalCalendarEvents.CountAsync(e => e.AgentUserId == agent));
        Assert.Equal(GoogleCalendarAccountMemory.Hash(agent, "new@example.test"),
            (await db.AgentGoogleCalendarAccounts.AsNoTracking().SingleAsync(a => a.AgentUserId == agent)).AccountHash);

        // The next run writes the open follow-up to the new calendar, and only that one.
        await NewJob(db, google, provider, new GrantAll(), new List<string>()).RunAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { "Open review" }, google.Created);
        Assert.Equal("g-new-1", (await Reload(db, f.Open)).GoogleEventId);
    }

    [Fact]
    public async Task Connecting_the_same_account_again_keeps_the_links_and_sends_nothing_twice()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var agent = await SeedAgentAsync(db, "e5-b");
        var f = await SeedFollowUpsAsync(db, agent);
        db.AgentGoogleCalendarAccounts.Add(new AgentGoogleCalendarAccount { AgentUserId = agent, AccountHash = GoogleCalendarAccountMemory.Hash(agent, "Same@Example.test") });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var google = new StubGoogle { Account = "same@example.test" };
        var controller = NewGoogleController(db, google, provider, agent);
        Assert.IsType<RedirectToActionResult>(await controller.Callback("code", State(provider, agent), null));
        Assert.DoesNotContain("different", (string)controller.TempData["Success"]!);

        db.ChangeTracker.Clear();
        Assert.Equal("g-open", (await Reload(db, f.Open)).GoogleEventId);
        await NewJob(db, google, provider, new GrantAll(), new List<string>()).RunAsync();
        Assert.Empty(google.Created);
    }

    // Connected and disconnected before this release: nothing was remembered, so a reconnect behaves as before.
    [Fact]
    public async Task An_account_never_remembered_counts_as_the_same_one()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var agent = await SeedAgentAsync(db, "e5-c");
        var f = await SeedFollowUpsAsync(db, agent);

        var controller = NewGoogleController(db, new StubGoogle { Account = "whoever@example.test" }, provider, agent);
        Assert.IsType<RedirectToActionResult>(await controller.Callback("code", State(provider, agent), null));

        db.ChangeTracker.Clear();
        Assert.Equal("g-open", (await Reload(db, f.Open)).GoogleEventId);
        Assert.True(await db.AgentGoogleCalendarAccounts.AnyAsync(a => a.AgentUserId == agent));   // remembered from now on
    }

    // Reconnect while still connected (the "stopped syncing" link): the account on the row is the previous one.
    [Fact]
    public async Task Reconnecting_another_account_over_a_live_connection_is_a_different_account_too()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var agent = await SeedAgentAsync(db, "e5-d");
        var f = await SeedFollowUpsAsync(db, agent);
        await SeedConnectionAsync(db, provider, agent, "old@example.test");

        var controller = NewGoogleController(db, new StubGoogle { Account = "new@example.test" }, provider, agent);
        Assert.IsType<RedirectToActionResult>(await controller.Callback("code", State(provider, agent), null));

        db.ChangeTracker.Clear();
        Assert.Null((await Reload(db, f.Open)).GoogleEventId);
        var connection = await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(c => c.AgentUserId == agent);
        Assert.Equal("new@example.test", connection.GoogleAccountEmail);
        Assert.Null(connection.SyncToken);
    }

    [Fact]
    public async Task A_connection_from_before_gets_its_account_remembered_by_the_next_run()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var agent = await SeedAgentAsync(db, "e5-e");
        await SeedConnectionAsync(db, provider, agent, "adviser@example.test");

        await NewJob(db, new StubGoogle(), provider, new GrantAll(), new List<string>()).RunAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(GoogleCalendarAccountMemory.Hash(agent, "adviser@example.test"),
            (await db.AgentGoogleCalendarAccounts.AsNoTracking().SingleAsync(a => a.AgentUserId == agent)).AccountHash);
    }

    [Fact]
    public void The_remembered_account_is_a_hash_that_ignores_case_and_differs_per_adviser()
    {
        var hash = GoogleCalendarAccountMemory.Hash(7, " Adviser@Example.TEST ");
        Assert.Equal(GoogleCalendarAccountMemory.Hash(7, "adviser@example.test"), hash);
        Assert.NotEqual(GoogleCalendarAccountMemory.Hash(8, "adviser@example.test"), hash);
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain("adviser", hash);
    }

    // ---- L-4 --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_open_accounts_whose_package_has_the_sync_are_synced()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var platinum = await SeedAgentAsync(db, "l4-p");
        var gold = await SeedAgentAsync(db, "l4-g");
        var suspended = await SeedAgentAsync(db, "l4-s");
        await SeedConnectionAsync(db, provider, platinum, "p@example.test", accessToken: "token-platinum");
        await SeedConnectionAsync(db, provider, gold, "g@example.test", accessToken: "token-gold");
        await SeedConnectionAsync(db, provider, suspended, "s@example.test", accessToken: "token-suspended");
        await db.AgentUsers.Where(a => a.Id == suspended).ExecuteUpdateAsync(u => u.SetProperty(a => a.IsActive, false));

        var google = new StubGoogle();
        var entitlements = new GrantOnly(platinum, suspended);
        await NewJob(db, google, provider, entitlements, new List<string>()).RunAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(new[] { "token-platinum" }, google.ListedWith);
        Assert.NotNull((await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(c => c.AgentUserId == platinum)).LastSyncedAt);
        var paused = await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(c => c.AgentUserId == gold);
        Assert.Null(paused.LastSyncedAt);
        Assert.True(paused.IsActive);   // paused, not removed
        Assert.DoesNotContain(suspended, entitlements.Asked);   // a closed account is not even looked up
    }

    [Fact]
    public void Profile_keeps_disconnect_within_reach_while_the_sync_is_paused_and_the_calendar_hides_its_copies()
    {
        var profile = Read(@"src\IPRO.Web\Views\Account\Profile.cshtml");
        Assert.Contains("var googlePaused = !googleIncluded;", profile);
        Assert.Contains("is paused until your package includes Google Calendar again.", profile);
        var connectionBlock = profile.IndexOf("@if (googleConnection != null)", StringComparison.Ordinal);
        Assert.True(connectionBlock > profile.IndexOf("@if (!googleIncluded)", StringComparison.Ordinal));
        Assert.True(profile.IndexOf("asp-action=\"Disconnect\"", StringComparison.Ordinal) > connectionBlock);
        Assert.Contains("else if (googleIncluded)", profile);

        var clients = Read(@"src\IPRO.Web\Controllers\ClientsController.cs");
        Assert.Contains("&& await _entitlements.HasAccessAsync(AgentId, PackageFeatureCodes.GoogleCalendarSync);", clients);

        var guide = Read(@"DOCS\28_CALENDAR_AND_GOOGLE_CALENDAR.md");
        Assert.Contains("### Switch to a Different Google Account", guide);
        Assert.Contains("### If Your Package Changes", guide);
    }

    // ---- the new table and index --------------------------------------------------------------------------

    [Fact]
    public async Task Start_up_makes_the_new_table_and_index_on_a_database_without_them()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = await SeedAgentAsync(db, "schema");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE `AgentGoogleCalendarAccounts`");   // production before this deploy
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE `ExternalCalendarEvents` DROP INDEX `IX_ExternalCalendarEvents_AgentUserId_GoogleEventId`");

        await StartupSchemaRepair.EnsureClientPortalSchemaAsync(db);
        await StartupSchemaRepair.EnsureClientPortalSchemaAsync(db);

        await GoogleCalendarAccountMemory.RememberAsync(db, agent, "adviser@example.test");
        await db.SaveChangesAsync();
        Assert.Equal(GoogleCalendarAccountMemory.Hash(agent, "adviser@example.test"), await GoogleCalendarAccountMemory.RememberedAsync(db, agent));

        var indexes = await db.Database.SqlQueryRaw<string>(
            "SELECT DISTINCT INDEX_NAME AS `Value` FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'ExternalCalendarEvents'").ToListAsync();
        Assert.Contains("IX_ExternalCalendarEvents_AgentUserId_GoogleEventId", indexes);

        Assert.Contains("(\"AgentGoogleCalendarAccounts\", \"AgentUserId = @agentId\")", Read(@"src\IPRO.DataAccess\AgentDataEraser.cs"));
    }

    // ---- harness ----------------------------------------------------------------------------------------

    private static GoogleCalendarSyncJob NewJob(IPRODbContext db, IGoogleCalendarService google, IDataProtectionProvider provider,
        IPRO.Business.Interfaces.IPackageEntitlementService entitlements, List<string> log) =>
        new(db, google, provider, entitlements, new ListLogger<GoogleCalendarSyncJob>(log));

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string prefix)
    {
        var agent = new AgentUser
        {
            UserName = $"{prefix}-{Guid.NewGuid():N}"[..20],
            Email = $"{prefix}-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Calendar", LastName = "Adviser",
            DomainName = $"{prefix}-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario"
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> SeedConnectionAsync(IPRODbContext db, IDataProtectionProvider provider, int agentId, string account, string accessToken = "access-token")
    {
        var tokens = provider.CreateProtector("IPRO.Web.GoogleCalendar.Tokens.v1");
        var connection = new GoogleCalendarConnection
        {
            AgentUserId = agentId,
            GoogleAccountEmail = account,
            EncryptedAccessToken = tokens.Protect(accessToken),
            EncryptedRefreshToken = tokens.Protect("refresh-token"),
            AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1),
            SyncToken = "sync-0",
            LocalTimesResyncedAt = DateTime.UtcNow
        };
        db.Add(connection);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return connection.Id;
    }

    private sealed record FollowUps(int Open, int Completed, int CancelledInGoogle);

    private static async Task<FollowUps> SeedFollowUpsAsync(IPRODbContext db, int agentId)
    {
        var client = new Client { AgentUserId = agentId, FirstName = "Cal", LastName = "Client", Email = $"cal-{Guid.NewGuid():N}"[..12] + "@example.test" };
        db.Add(client);
        await db.SaveChangesAsync();
        var open = new ClientFollowUp { ClientId = client.Id, Title = "Open review", DueAt = DateTime.Today.AddDays(3), GoogleEventId = "g-open" };
        var completed = new ClientFollowUp { ClientId = client.Id, Title = "Done review", DueAt = DateTime.Today.AddDays(-3), GoogleEventId = "g-done", IsCompleted = true, CompletedAt = DateTime.UtcNow };
        var cancelled = new ClientFollowUp { ClientId = client.Id, Title = "Removed in Google", DueAt = DateTime.Today.AddDays(4), GoogleUnlinkedAt = DateTime.UtcNow.AddDays(-1) };
        db.AddRange(open, completed, cancelled);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new FollowUps(open.Id, completed.Id, cancelled.Id);
    }

    private static Task<ClientFollowUp> Reload(IPRODbContext db, int id) => db.ClientFollowUps.AsNoTracking().SingleAsync(f => f.Id == id);

    private static string State(IDataProtectionProvider provider, int agentId) =>
        provider.CreateProtector("IPRO.Web.GoogleCalendar.State.v1").Protect($"{agentId}|{DateTime.UtcNow.Ticks}");

    private static IPRO.Web.Controllers.GoogleCalendarController NewGoogleController(IPRODbContext db, IGoogleCalendarService google, IDataProtectionProvider provider, int agentId)
    {
        var controller = new IPRO.Web.Controllers.GoogleCalendarController(db, google, new GrantAll(), new ConfigurationBuilder().Build(), provider);
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }

    private sealed class StubGoogle : IGoogleCalendarService
    {
        public string Account { get; init; } = "adviser@example.test";
        public List<GoogleCalendarEventData> Events { get; } = new();
        public Func<Task>? DuringList { get; set; }
        public List<string> Created { get; } = new();
        public List<string> ListedWith { get; } = new();
        public string BuildAuthorizationUrl(string redirectUri, string state) => throw new NotSupportedException();
        public Task<GoogleTokenResult> ExchangeCodeAsync(string code, string redirectUri) =>
            Task.FromResult(new GoogleTokenResult("access-new", "refresh-new", DateTime.UtcNow.AddHours(1), Account));
        public Task<(string AccessToken, DateTime ExpiresAt)> RefreshAccessTokenAsync(string refreshToken) =>
            Task.FromResult(("access-refreshed", DateTime.UtcNow.AddHours(1)));
        public async Task<GoogleEventListResult> ListEventsAsync(string accessToken, string calendarId, string? syncToken)
        {
            ListedWith.Add(accessToken);
            if (DuringList != null) await DuringList();
            return new GoogleEventListResult(Events.ToList(), "sync-next", false);
        }
        public Task<string> CreateEventAsync(string accessToken, string calendarId, string title, DateTime localStart, DateTime? localEnd, TimeZoneInfo zone)
        {
            Created.Add(title);
            return Task.FromResult($"g-new-{Created.Count}");
        }
        public Task UpdateEventAsync(string accessToken, string calendarId, string googleEventId, string title, DateTime localStart, DateTime? localEnd, TimeZoneInfo zone) => Task.CompletedTask;
        public Task DeleteEventAsync(string accessToken, string calendarId, string googleEventId) => Task.CompletedTask;
        public Task RevokeTokenAsync(string token) => Task.CompletedTask;
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

    // The sync for some advisers only; records who was asked about.
    private sealed class GrantOnly : IPRO.Business.Interfaces.IPackageEntitlementService
    {
        private readonly HashSet<int> _granted;
        public List<int> Asked { get; } = new();
        public GrantOnly(params int[] granted) => _granted = granted.ToHashSet();
        public Task<IPRO.Business.Interfaces.PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new IPRO.Business.Interfaces.PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = _granted.Contains(agentId) });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(_granted.Contains(agentId));
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode)
        {
            Assert.Equal(PackageFeatureCodes.GoogleCalendarSync, featureCode);
            var ids = agentIds.Distinct().ToList();
            Asked.AddRange(ids);
            return Task.FromResult(ids.ToDictionary(a => a, a => _granted.Contains(a)));
        }
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        private readonly List<string> _lines;
        public ListLogger(List<string> lines) => _lines = lines;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Add($"{logLevel}: {formatter(state, exception)}");
    }
}
