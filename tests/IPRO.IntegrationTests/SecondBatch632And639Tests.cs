using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Utility;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 2026-11-01, built to go out with the first batch:
//  632  the Google Calendar sync runs one at a time, and an adviser whose connection stops working is told.
//  639  the dates that printed Azure's clock (UTC) show the adviser's own day, in the portal and the client portal.
// 644 is in EmailConsentChannelTests.
public class SecondBatch632And639Tests
{
    [Fact]
    public void The_sync_runs_one_at_a_time()
    {
        var run = typeof(GoogleCalendarSyncJob).GetMethod(nameof(GoogleCalendarSyncJob.RunAsync))!;
        Assert.NotNull(run.GetCustomAttribute<Hangfire.DisableConcurrentExecutionAttribute>());
        Assert.Equal(0, run.GetCustomAttribute<Hangfire.AutomaticRetryAttribute>()!.Attempts);
    }

    [Fact]
    public async Task An_adviser_is_told_once_after_two_hours_of_failed_runs_and_a_working_run_clears_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var agent = await SeedConnectionAsync(db, provider, expiredToken: true);
        var google = new StubGoogle { Refresh = () => throw new InvalidOperationException("Google Calendar connection has expired or was revoked. Please reconnect.") };
        var email = new RecordingEmail();
        var job = NewJob(db, google, provider, email);

        // Seven runs is 1 hour 45: a passing outage, nobody is bothered.
        for (var run = 0; run < 7; run++)
        {
            await job.RunAsync();
            db.ChangeTracker.Clear();   // each real run has its own context
        }
        var connection = await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(c => c.AgentUserId == agent.Id);
        Assert.Equal(7, connection.FailedRuns);
        Assert.Null(connection.StoppedSyncingAt);
        Assert.Empty(email.Sent);

        // The eighth is two hours: told once, and the job keeps trying without telling again.
        job = NewJob(db, google, provider, email);
        await job.RunAsync();
        db.ChangeTracker.Clear();
        connection = await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(c => c.AgentUserId == agent.Id);
        var stoppedAt = connection.StoppedSyncingAt;
        Assert.NotNull(stoppedAt);
        var (to, subject, html, text) = Assert.Single(email.Sent);
        Assert.Equal(agent.Email, to);
        Assert.Contains("stopped syncing", subject);
        Assert.Contains("/Account/Profile", html);
        Assert.Contains("Reconnect Google Calendar", html);
        Assert.Contains("Reconnect Google Calendar", text!);

        job = NewJob(db, google, provider, email);
        await job.RunAsync();
        db.ChangeTracker.Clear();
        connection = await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(c => c.AgentUserId == agent.Id);
        Assert.Equal(9, connection.FailedRuns);
        Assert.Equal(stoppedAt, connection.StoppedSyncingAt);
        Assert.Single(email.Sent);

        // Google answers again: nothing for the adviser to do, the notice goes by itself.
        google.Refresh = () => ("fresh-token", DateTime.UtcNow.AddHours(1));
        job = NewJob(db, google, provider, email);
        await job.RunAsync();
        db.ChangeTracker.Clear();
        connection = await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(c => c.AgentUserId == agent.Id);
        Assert.Equal(0, connection.FailedRuns);
        Assert.Null(connection.StoppedSyncingAt);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task A_failed_run_is_counted_and_is_not_recorded_as_a_sync()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var agent = await SeedConnectionAsync(db, provider, expiredToken: false);
        var google = new StubGoogle { FailList = true };

        await NewJob(db, google, provider, new RecordingEmail()).RunAsync();
        db.ChangeTracker.Clear();

        var connection = await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(c => c.AgentUserId == agent.Id);
        Assert.Equal(1, connection.FailedRuns);
        Assert.Null(connection.LastSyncedAt);
    }

    [Fact]
    public async Task A_suspended_adviser_is_not_emailed_about_their_calendar()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var agent = await SeedConnectionAsync(db, provider, expiredToken: true, failedRuns: 7);
        await db.AgentUsers.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false));
        var email = new RecordingEmail();
        var google = new StubGoogle { Refresh = () => throw new InvalidOperationException("revoked") };

        await NewJob(db, google, provider, email).RunAsync();
        db.ChangeTracker.Clear();

        Assert.NotNull((await db.GoogleCalendarConnections.AsNoTracking().SingleAsync(c => c.AgentUserId == agent.Id)).StoppedSyncingAt);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public void Profile_shows_the_notice_and_reconnecting_clears_it()
    {
        var profile = Read("src/IPRO.Web/Views/Account/Profile.cshtml");
        Assert.Contains("Your Google Calendar has stopped syncing.", profile);
        Assert.Contains("googleConnection.StoppedSyncingAt != null", profile);

        var callback = Read("src/IPRO.Web/Controllers/GoogleCalendarController.cs");
        Assert.Contains("existing.FailedRuns = 0;", callback);
        Assert.Contains("existing.StoppedSyncingAt = null;", callback);

        var schema = Read("src/IPRO.DataAccess/StartupSchemaRepair.cs");
        Assert.Contains("\"GoogleCalendarConnections\", \"FailedRuns\"", schema);
        Assert.Contains("\"GoogleCalendarConnections\", \"StoppedSyncingAt\"", schema);
    }

    // The evening is the case that matters: 9:30 PM in Toronto on the 1st is already the 2nd on Azure's clock.
    [Fact]
    public void A_late_evening_stays_on_the_advisers_own_day()
    {
        var utc = new DateTime(2026, 11, 2, 2, 30, 0, DateTimeKind.Unspecified);
        Assert.Equal(new DateTime(2026, 11, 1, 21, 30, 0), AgentTimeZoneHelper.FromUtc(utc, AgentTimeZoneHelper.DefaultTimeZone));
    }

    [Fact]
    public async Task The_client_portal_uses_the_zone_of_the_clients_adviser()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = NewAgent("tz");
        agent.TimeZone = "(GMT-08:00) Pacific Time (US & Canada)";
        db.Add(agent);
        await db.SaveChangesAsync();
        var client = new Client { AgentUserId = agent.Id, FirstName = "Zone", LastName = "Client", Email = "zone@example.test" };
        db.Add(client);
        await db.SaveChangesAsync();

        Assert.Equal("(GMT-08:00) Pacific Time (US & Canada)", await AgentTimeZoneHelper.ResolveForClientAsync(db, client.Id));
        Assert.Equal(AgentTimeZoneHelper.DefaultTimeZone, await AgentTimeZoneHelper.ResolveForClientAsync(db, -1));
    }

    [Theory]
    [InlineData("Articles/Index", "Local(article.UpdatedAt)", "ArticlesController")]
    [InlineData("Forms/Index", "Local(form.UpdatedAt)", "FormsController")]
    [InlineData("Newsletter/Subscribers", "Local(c.CreatedAt)", "NewsletterController")]
    [InlineData("Newsletter/Edit", "Local(Model.SentAt.Value)", "NewsletterController")]
    [InlineData("Polls/Index", "Local(poll.UpdatedAt)", "PollsController")]
    [InlineData("Polls/Preview", "Local(send.ScheduledAt)", "PollsController")]
    [InlineData("SocialPosts/Index", "Local(post.UpdatedAt)", "SocialPostsController")]
    [InlineData("SocialPosts/Edit", "Local(Model.PostedAt.Value)", "SocialPostsController")]
    [InlineData("Testimonials/Index", "Local(t.SubmittedAt)", "TestimonialsController")]
    [InlineData("Testimonials/Edit", "Local(Model.SubmittedAt)", "TestimonialsController")]
    [InlineData("Documents/Index", "Local(doc.UploadedAt)", "DocumentsController")]
    [InlineData("ReferAFriend/Index", "Local(r.SignedUpAt)", "ReferAFriendController")]
    [InlineData("Payments/Index", "Local(stripe.ConnectedAt)", "PaymentsController")]
    [InlineData("ClientPortalMessages/Index", "Local(message.CreatedAt)", "ClientPortalMessagesController")]
    [InlineData("ClientPortalDocuments/Index", "Local(doc.UploadedAt)", "ClientPortalDocumentsController")]
    [InlineData("ClientPortalAppointments/Index", "Local(r.CreatedAt)", "ClientPortalAppointmentsController")]
    public void A_screen_prints_its_date_in_the_advisers_zone(string view, string call, string controller)
    {
        var page = Read($"src/IPRO.Web/Views/{view}.cshtml");
        Assert.Contains("AgentTimeZoneHelper.FromUtc(utc, ViewBag.AgentTimeZone as string)", page);
        Assert.Contains(call, page);

        var source = Read($"src/IPRO.Web/Controllers/{controller}.cs");
        Assert.True(source.Contains("ViewBag.AgentTimeZone =") || source.Contains("LoadAgentTimeZoneAsync()"), $"{controller} sets no zone for {view}");
    }

    // ToLocalTime on a Linux host with its clock on UTC changes nothing, which is how this went unseen.
    [Fact]
    public void No_view_trusts_the_servers_own_clock()
    {
        var views = Path.Combine(RepoRoot(), "src", "IPRO.Web", "Views");
        var offenders = Directory.EnumerateFiles(views, "*.cshtml", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains(".ToLocalTime()"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    private static GoogleCalendarSyncJob NewJob(IPRODbContext db, IGoogleCalendarService google, IDataProtectionProvider provider, IEmailService email) =>
        new(db, google, provider, NullLogger<GoogleCalendarSyncJob>.Instance, null, email,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.example.test" }).Build());

    private static AgentUser NewAgent(string prefix) => new()
    {
        UserName = $"{prefix}-{Guid.NewGuid():N}"[..20],
        Email = $"{prefix}-{Guid.NewGuid():N}"[..12] + "@example.test",
        FirstName = "Second", LastName = "Batch",
        DomainName = $"{prefix}-{Guid.NewGuid():N}"[..24],
        Country = "Canada", Province = "Ontario"
    };

    private static async Task<AgentUser> SeedConnectionAsync(IPRODbContext db, IDataProtectionProvider provider, bool expiredToken, int failedRuns = 0)
    {
        var agent = NewAgent("gcal");
        db.Add(agent);
        await db.SaveChangesAsync();
        var tokens = provider.CreateProtector("IPRO.Web.GoogleCalendar.Tokens.v1");
        db.Add(new GoogleCalendarConnection
        {
            AgentUserId = agent.Id,
            GoogleAccountEmail = "adviser@example.test",
            EncryptedAccessToken = tokens.Protect("access-token"),
            EncryptedRefreshToken = tokens.Protect("refresh-token"),
            AccessTokenExpiresAt = expiredToken ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddHours(1),
            SyncToken = "sync-0",
            LocalTimesResyncedAt = DateTime.UtcNow,
            FailedRuns = failedRuns
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return agent;
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "IPRO.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class StubGoogle : IGoogleCalendarService
    {
        public Func<(string AccessToken, DateTime ExpiresAt)> Refresh { get; set; } = () => ("fresh-token", DateTime.UtcNow.AddHours(1));
        public bool FailList { get; set; }
        public string BuildAuthorizationUrl(string redirectUri, string state) => throw new NotSupportedException();
        public Task<GoogleTokenResult> ExchangeCodeAsync(string code, string redirectUri) => throw new NotSupportedException();
        public Task<(string AccessToken, DateTime ExpiresAt)> RefreshAccessTokenAsync(string refreshToken) => Task.FromResult(Refresh());
        public Task<GoogleEventListResult> ListEventsAsync(string accessToken, string calendarId, string? syncToken) =>
            FailList ? throw new InvalidOperationException("Google is down") : Task.FromResult(new GoogleEventListResult(new List<GoogleCalendarEventData>(), "sync-1", false));
        public Task<string> CreateEventAsync(string accessToken, string calendarId, string title, DateTime localStart, DateTime? localEnd, TimeZoneInfo zone) => Task.FromResult("g-new");
        public Task UpdateEventAsync(string accessToken, string calendarId, string googleEventId, string title, DateTime localStart, DateTime? localEnd, TimeZoneInfo zone) => Task.CompletedTask;
        public Task DeleteEventAsync(string accessToken, string calendarId, string googleEventId) => Task.CompletedTask;
        public Task RevokeTokenAsync(string token) => Task.CompletedTask;
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, string Subject, string Html, string? Text)> Sent { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            Sent.Add((toEmail, subject, htmlBody, textBody));
            return Task.FromResult(EmailSendResult.Sent($"acs-{Sent.Count}"));
        }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }
}
