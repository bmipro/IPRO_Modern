using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Utility;
using IPRO.Web.Infrastructure;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 493 (2026-09-17): the narrow pre-launch audit's small findings, one test each. The serious one
// (the bounded gate) is in LaunchLoadFixTests. Each test here failed against the code as it stood on
// the morning of 2026-09-17.
public class NarrowAuditFixTests
{
    private const string OldKey = "old-webhook-secret-used-as-signing-key";
    private const string NewKey = "a-separate-tracking-signing-key";

    // ---- telemetry --------------------------------------------------------------------------

    [Fact]
    public void The_webhook_secret_and_the_tracking_tokens_are_scrubbed_from_telemetry()
    {
        // Application Insights stores the full request URL. The Event Grid webhook secret arrives in
        // the query string on every delivery report, and every open and click carries a live,
        // never-expiring per-recipient token in the path plus a valid signature in the query. The
        // privacy policy says telemetry is scrubbed of tokens; now it is.
        var hook = Scrub("https://app.iproadvisers.com/AzureEmailEvents?secret=S3CRETVALUE");
        Assert.DoesNotContain("S3CRETVALUE", hook.Url.ToString());
        Assert.Contains("secret=REDACTED", hook.Url.ToString());

        var pixel = Scrub("https://app.iproadvisers.com/t/o/newsletter/TOKENVALUE123.gif", "GET /t/o/newsletter/TOKENVALUE123.gif");
        Assert.DoesNotContain("TOKENVALUE123", pixel.Url.ToString());
        Assert.Contains("/t/o/newsletter/REDACTED", pixel.Url.ToString());
        Assert.DoesNotContain("TOKENVALUE123", pixel.Name);

        var click = Scrub("https://app.iproadvisers.com/t/c/ecard/TOKENVALUE123?u=https%3A%2F%2Fexample.test%2Fpage&s=SIGVALUE");
        var url = click.Url.ToString();
        Assert.DoesNotContain("TOKENVALUE123", url);
        Assert.DoesNotContain("SIGVALUE", url);
        Assert.Contains("/t/c/ecard/REDACTED", url);
        Assert.Contains("example.test", url);   // the destination stays: it is the thing worth seeing
    }

    // ---- the signing key --------------------------------------------------------------------

    [Fact]
    public void The_verification_keys_are_the_signing_key_then_the_previous_one()
    {
        // Production signed its first three days of links with the webhook secret (the 488 fallback).
        // A dedicated key can now be set without breaking those links: the previous key still
        // verifies, and only the dedicated key signs.
        var both = Config(("Email:TrackingSigningKey", NewKey), ("Email:TrackingSigningKeyPrevious", OldKey));
        Assert.Equal(new[] { NewKey, OldKey }, EmailTrackingLinks.VerificationKeys(both));
        Assert.Equal(NewKey, EmailTrackingLinks.SigningKey(both));

        var hookOnly = Config(("Email:AzureEventWebhookSecret", OldKey));
        Assert.Equal(new[] { OldKey }, EmailTrackingLinks.VerificationKeys(hookOnly));

        var hookAndPrevious = Config(("Email:AzureEventWebhookSecret", OldKey), ("Email:TrackingSigningKeyPrevious", "older"));
        Assert.Equal(new[] { OldKey, "older" }, EmailTrackingLinks.VerificationKeys(hookAndPrevious));

        Assert.Empty(EmailTrackingLinks.VerificationKeys(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public async Task A_click_signed_with_the_previous_key_still_redirects_and_new_links_carry_the_new_one()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (_, token) = await SeedSentCardAsync(db);
        const string target = "https://www.example-adviser.test/news";
        var config = Config(
            ("Email:TrackingSigningKey", NewKey),
            ("Email:TrackingSigningKeyPrevious", OldKey),
            ("Email:AzureEventWebhookSecret", "the-hook"));

        var controller = NewTrackingController(db, config, out _);
        Assert.IsType<RedirectResult>(await controller.Click("ecard", token, target, EmailTrackingLinks.Sign("ecard", token, target, OldKey)));
        Assert.IsType<RedirectResult>(await controller.Click("ecard", token, target, EmailTrackingLinks.Sign("ecard", token, target, NewKey)));
        // The webhook secret is no longer a signing key once a dedicated one is set.
        Assert.IsType<BadRequestObjectResult>(await controller.Click("ecard", token, target, EmailTrackingLinks.Sign("ecard", token, target, "the-hook")));

        // New links are signed with the new key.
        var html = EmailTrackingLinks.Instrument($"<a href=\"{target}\">x</a>", "ecard", token, "https://app.example.test", EmailTrackingLinks.SigningKey(config));
        Assert.Contains("s=" + EmailTrackingLinks.Sign("ecard", token, target, NewKey), html);
    }

    // ---- the pixel as a database writer -----------------------------------------------------

    [Fact]
    public async Task A_repeated_open_or_click_does_not_run_the_recorder_again()
    {
        // The recorders are read-modify-write over the recipient row plus a full roll-up recount of
        // the send. Opens and clicks are write-once milestones, so a replayed pixel -- a mail client
        // re-fetching it, or anyone who has the URL -- must cost one indexed read and nothing else.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (recipientId, token) = await SeedSentCardAsync(db);
        var config = Config(("Email:AzureEventWebhookSecret", OldKey));
        var controller = NewTrackingController(db, config, out var tracker);

        await controller.Open("ecard", token);
        await controller.Open("ecard", token);
        await controller.Open("ecard", token);
        Assert.Equal(1, tracker.Calls);

        const string target = "https://www.example-adviser.test/news";
        var s = EmailTrackingLinks.Sign("ecard", token, target, OldKey);
        Assert.IsType<RedirectResult>(await controller.Click("ecard", token, target, s));
        Assert.IsType<RedirectResult>(await controller.Click("ecard", token, target, s));
        Assert.Equal(2, tracker.Calls);

        db.ChangeTracker.Clear();
        var row = await db.ECardRecipients.AsNoTracking().SingleAsync(r => r.Id == recipientId);
        Assert.NotNull(row.OpenedAt);
        Assert.NotNull(row.ClickedAt);
    }

    // ---- the alias redirect -----------------------------------------------------------------

    [Fact]
    public void The_alias_redirect_re_encodes_the_path_it_forwards()
    {
        // The redirect was built from the DECODED path, so a %0A or a space in a request on a brand
        // domain produced a Location header Kestrel refuses: a free 500 on a public host.
        var config = Config(("App:AliasHosts", "www.iproaccountants.com=/accountants"), ("App:BaseUrl", "https://app.iproadvisers.com"));
        Assert.Equal("https://app.iproadvisers.com/a%20b/c?x=1",
            PlatformAliasHosts.RedirectTarget(config, "www.iproaccountants.com", new PathString("/a b/c"), new QueryString("?x=1")));
        var withNewline = PlatformAliasHosts.RedirectTarget(config, "www.iproaccountants.com", new PathString("/x\ny"), QueryString.Empty);
        Assert.DoesNotContain("\n", withNewline);
        Assert.Contains("%0A", withNewline);
        // The plain cases are unchanged.
        Assert.Equal("https://app.iproadvisers.com/accountants", PlatformAliasHosts.RedirectTarget(config, "www.iproaccountants.com"));
        Assert.Equal("https://app.iproadvisers.com/Account/Register?type=accountant",
            PlatformAliasHosts.RedirectTarget(config, "www.iproaccountants.com", new PathString("/Account/Register"), new QueryString("?type=accountant")));
    }

    // ---- the lead-notification flood --------------------------------------------------------

    [Fact]
    public async Task The_eleventh_lead_notification_in_an_hour_for_one_website_is_held()
    {
        // A public lead form sends the adviser an email per submission, inline, transactional. With
        // the subscription capped at 100 an hour, one machine working one form could spend the whole
        // platform's allowance. Ten notifications an hour per website; the leads are still saved.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId, otherWebsiteId) = await SeedTwoWebsitesAsync(db);
        var now = DateTime.UtcNow;

        for (var i = 0; i < 9; i++) db.Add(NewLead(agentId, websiteId, notified: true, createdAt: now.AddMinutes(-i - 1)));
        db.Add(NewLead(agentId, websiteId, notified: false, createdAt: now.AddMinutes(-2)));      // not emailed: does not count
        db.Add(NewLead(agentId, websiteId, notified: true, createdAt: now.AddMinutes(-90)));      // older than an hour: does not count
        for (var i = 0; i < 10; i++) db.Add(NewLead(agentId, otherWebsiteId, notified: true, createdAt: now.AddMinutes(-1)));   // another site
        await db.SaveChangesAsync();
        Assert.False(await WebsiteLeadNotifications.IsHeldAsync(db, websiteId, now));

        db.Add(NewLead(agentId, websiteId, notified: true, createdAt: now.AddMinutes(-30)));
        await db.SaveChangesAsync();
        Assert.True(await WebsiteLeadNotifications.IsHeldAsync(db, websiteId, now));
        Assert.True(await WebsiteLeadNotifications.IsHeldAsync(db, otherWebsiteId, now));

        // And the notification path consults it before it sends.
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\PublicWebsiteController.cs"));
        var notify = src[src.IndexOf("private async Task NotifyAgentAsync(", StringComparison.Ordinal)..];
        notify = notify[..notify.IndexOf("private async Task RecordSpamAttemptAsync(", StringComparison.Ordinal)];
        Assert.True(notify.IndexOf("WebsiteLeadNotifications.IsHeldAsync(", StringComparison.Ordinal) > 0
                 && notify.IndexOf("WebsiteLeadNotifications.IsHeldAsync(", StringComparison.Ordinal) < notify.IndexOf("_email.SendDetailedAsync(", StringComparison.Ordinal),
            "the cap must be checked before the send");
    }

    [Fact]
    public void The_public_form_limits_sit_well_under_the_hourly_sending_cap()
    {
        // 10 per 5 minutes per IP on three submit endpoints was 120 an hour from one address against
        // a 100-an-hour subscription cap.
        using var doc = JsonDocument.Parse(File.ReadAllText(FindRepoFile(@"src\IPRO.Web\appsettings.json")));
        var rules = doc.RootElement.GetProperty("IpRateLimiting").GetProperty("GeneralRules").EnumerateArray().ToList();
        var submits = rules.Where(r =>
        {
            var endpoint = r.GetProperty("Endpoint").GetString() ?? string.Empty;
            return endpoint.EndsWith("/PublicWebsite/SubmitLead", StringComparison.Ordinal)
                || endpoint.EndsWith("/PublicWebsite/SubmitCustomForm", StringComparison.Ordinal)
                || endpoint.EndsWith("/PublicWebsite/SubmitTestimonial", StringComparison.Ordinal);
        }).ToList();
        Assert.Equal(6, submits.Count);
        foreach (var rule in submits)
        {
            Assert.Equal("5m", rule.GetProperty("Period").GetString());
            Assert.True(rule.GetProperty("Limit").GetInt32() <= 3, rule.GetProperty("Endpoint").GetString());
        }
    }

    // ---- the Google delete loop -------------------------------------------------------------

    [Fact]
    public async Task A_follow_up_deleted_in_google_stays_deleted_while_a_new_one_is_still_pushed()
    {
        // Deleting a synced event in Google unlinked the follow-up (GoogleEventId = null), and the
        // push query selects exactly GoogleEventId == null: the event came back fifteen minutes
        // later, was deleted again, came back again, forever.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var (followUpId, clientId) = await SeedGoogleAsync(db, provider);

        var google = new StubGoogle
        {
            Events = new List<GoogleCalendarEventData> { new("g-follow-1", "Client review", DateTime.UtcNow.AddDays(3), null, IsCancelled: true) }
        };
        var job = new GoogleCalendarSyncJob(db, google, provider, NullLogger<GoogleCalendarSyncJob>.Instance);
        await job.RunAsync();
        db.ChangeTracker.Clear();

        var followUp = await db.ClientFollowUps.AsNoTracking().SingleAsync(f => f.Id == followUpId);
        Assert.Null(followUp.GoogleEventId);          // still unlinked, still an IPRO record
        Assert.NotNull(followUp.GoogleUnlinkedAt);    // and remembered as the adviser's choice
        Assert.Empty(google.Created);

        // The next run: nothing new at Google, one brand-new follow-up on our side.
        google.Events = new List<GoogleCalendarEventData>();
        db.Add(new ClientFollowUp { ClientId = clientId, Title = "Fresh task", DueAt = DateTime.UtcNow.AddDays(5) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await job.RunAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(new[] { "Fresh task" }, google.Created);
        followUp = await db.ClientFollowUps.AsNoTracking().SingleAsync(f => f.Id == followUpId);
        Assert.Null(followUp.GoogleEventId);
    }

    [Fact]
    public void Production_gets_the_unlinked_column()
    {
        Assert.Contains("GoogleUnlinkedAt", File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\StartupSchemaRepair.cs")));
    }

    // ---- harness ----------------------------------------------------------------------------

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value)).Build();

    private static RequestTelemetry Scrub(string url, string? name = null)
    {
        var t = new RequestTelemetry { Url = new Uri(url) };
        if (name != null) t.Name = name;
        new SensitiveDataTelemetryInitializer().Initialize(t);
        return t;
    }

    private static IPRO.Web.Controllers.EmailTrackingController NewTrackingController(IPRODbContext db, IConfiguration config, out CountingTracker tracker)
    {
        var consent = new StubConsent();
        tracker = new CountingTracker(new EmailDeliveryTracker(db, NullLogger<EmailDeliveryTracker>.Instance, consent));
        return new IPRO.Web.Controllers.EmailTrackingController(
            db,
            new NewsLetterService(new UnitOfWork(db), consent, db),
            tracker,
            config,
            NullLogger<IPRO.Web.Controllers.EmailTrackingController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private sealed class CountingTracker : IEmailDeliveryTracker
    {
        private readonly IEmailDeliveryTracker _inner;
        public CountingTracker(IEmailDeliveryTracker inner) => _inner = inner;
        public int Calls { get; private set; }
        public Task RecordAsync(string entityKind, int recipientId, string eventName, string? providerMessageId, string? reason, DateTime occurredAt)
        {
            Calls++;
            return _inner.RecordAsync(entityKind, recipientId, eventName, providerMessageId, reason, occurredAt);
        }
    }

    private sealed class StubConsent : IEmailConsentService
    {
        public bool IsSuppressed(Client client, EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }

    private sealed class StubGoogle : IGoogleCalendarService
    {
        public List<GoogleCalendarEventData> Events { get; set; } = new();
        public List<string> Created { get; } = new();
        public string BuildAuthorizationUrl(string redirectUri, string state) => throw new NotSupportedException();
        public Task<GoogleTokenResult> ExchangeCodeAsync(string code, string redirectUri) => throw new NotSupportedException();
        public Task<(string AccessToken, DateTime ExpiresAt)> RefreshAccessTokenAsync(string refreshToken) => throw new NotSupportedException();
        public Task<GoogleEventListResult> ListEventsAsync(string accessToken, string calendarId, string? syncToken) =>
            Task.FromResult(new GoogleEventListResult(Events, "sync-1", false));
        public Task<string> CreateEventAsync(string accessToken, string calendarId, string title, DateTime startAtUtc, DateTime? endAtUtc)
        {
            Created.Add(title);
            return Task.FromResult($"g-new-{Created.Count}");
        }
        public Task UpdateEventAsync(string accessToken, string calendarId, string googleEventId, string title, DateTime startAtUtc, DateTime? endAtUtc) => Task.CompletedTask;
        public Task DeleteEventAsync(string accessToken, string calendarId, string googleEventId) => Task.CompletedTask;
        public Task RevokeTokenAsync(string token) => Task.CompletedTask;
    }

    private static AgentUser NewAgent(string prefix) => new()
    {
        UserName = $"{prefix}-{Guid.NewGuid():N}"[..20],
        Email = $"{prefix}-{Guid.NewGuid():N}"[..12] + "@example.test",
        FirstName = "Narrow", LastName = "Audit",
        DomainName = $"{prefix}-{Guid.NewGuid():N}"[..24],
        Country = "Canada", Province = "Ontario"
    };

    private static async Task<(int RecipientId, string Token)> SeedSentCardAsync(IPRODbContext db)
    {
        var agent = NewAgent("na-card");
        db.Add(agent);
        await db.SaveChangesAsync();
        var client = new Client { AgentUserId = agent.Id, FirstName = "Card", LastName = "Reader", Email = $"reader-{Guid.NewGuid():N}"[..14] + "@example.test" };
        var card = new ECard { AgentUserId = agent.Id, Occasion = "Birthday", Subject = "Happy birthday" };
        db.AddRange(client, card);
        await db.SaveChangesAsync();
        var recipient = new ECardRecipient
        {
            ECardId = card.Id, ClientId = client.Id, Email = client.Email, RecipientName = "Card Reader",
            Status = ECardRecipientStatuses.Sent, SentAt = DateTime.UtcNow, TrackingToken = Guid.NewGuid().ToString("N")
        };
        db.Add(recipient);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (recipient.Id, recipient.TrackingToken);
    }

    private static async Task<(int AgentId, int WebsiteId, int OtherWebsiteId)> SeedTwoWebsitesAsync(IPRODbContext db)
    {
        var agent = NewAgent("na-web");
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "Test", BusinessType = "All" };
        db.AddRange(agent, template);
        await db.SaveChangesAsync();
        var one = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "One", IsPublished = true, CustomDomain = $"one-{Guid.NewGuid():N}"[..14] + ".example.test" };
        var two = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, SiteTitle = "Two", IsPublished = true, CustomDomain = $"two-{Guid.NewGuid():N}"[..14] + ".example.test" };
        db.AddRange(one, two);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, one.Id, two.Id);
    }

    private static WebsiteLead NewLead(int agentId, int websiteId, bool notified, DateTime createdAt) => new()
    {
        AgentUserId = agentId, AgentWebsiteId = websiteId,
        FirstName = "Lead", LastName = "Form", Email = $"lead-{Guid.NewGuid():N}"[..12] + "@example.test",
        NotificationSent = notified, CreatedAt = createdAt, UpdatedAt = createdAt
    };

    private static async Task<(int FollowUpId, int ClientId)> SeedGoogleAsync(IPRODbContext db, IDataProtectionProvider provider)
    {
        var agent = NewAgent("na-gcal");
        db.Add(agent);
        await db.SaveChangesAsync();
        var client = new Client { AgentUserId = agent.Id, FirstName = "Cal", LastName = "Client", Email = $"cal-{Guid.NewGuid():N}"[..12] + "@example.test" };
        db.Add(client);
        await db.SaveChangesAsync();
        var followUp = new ClientFollowUp { ClientId = client.Id, Title = "Client review", DueAt = DateTime.UtcNow.AddDays(3), GoogleEventId = "g-follow-1" };
        var tokens = provider.CreateProtector("IPRO.Web.GoogleCalendar.Tokens.v1");
        db.AddRange(followUp, new GoogleCalendarConnection
        {
            AgentUserId = agent.Id,
            GoogleAccountEmail = "adviser@example.test",
            EncryptedAccessToken = tokens.Protect("access-token"),
            EncryptedRefreshToken = tokens.Protect("refresh-token"),
            AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (followUp.Id, client.Id);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IPRO.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
