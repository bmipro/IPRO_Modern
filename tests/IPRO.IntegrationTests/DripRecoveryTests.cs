using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;
using Xunit;

namespace IPRO.IntegrationTests;

// H7 (launch runway Phase 1, 2026-08-27). A recoverable SendGrid rejection killed every drip
// enrollment permanently: 401/403 (rotated key, exhausted credits, unverified sender) and the
// not-configured result all classified as PERMANENT, so DripCampaignJob marked the enrollment
// Failed on the first attempt -- SendAttempts not even incremented -- and no resume path existed:
// Active was only ever assigned at enrollment creation. Recovery meant re-enrolling, which
// re-sends every prior step into the client's inbox.
//
// Two halves, matching the fix:
//   1. Classification -- account-level rejections are transient. Proven against the REAL
//      SendGridEmailService via its ClientFactory seam (the PublicHostGuard.ResolveHook pattern),
//      so the classification exercised is the production line, not a copy in the test. The
//      flagship test then chains REAL service -> REAL dispatcher -> REAL job, the C1 lesson.
//   2. Resume -- the new CampaignsController.ResumeFailedEnrollments action, with the invariant
//      that matters: NextStepIndex is untouched, so nothing already delivered is ever repeated.
public class DripRecoveryTests
{
    // ------------------------------------------------------------ 1. classification, service --

    [Fact]
    public async Task H7_a_rotated_key_401_is_transient_not_permanent()
    {
        var service = NewService(new StubSendGridClient(HttpStatusCode.Unauthorized, "{\"errors\":[{\"message\":\"The provided authorization grant is invalid, expired, or revoked\"}]}"));

        var result = await service.SendDetailedAsync("client@example.test", "Client", "Subject", "<p>Body</p>");

        Assert.False(result.Success);
        // Pre-fix: IsTransient false -> the drip job retired the enrollment permanently on the
        // FIRST attempt of an outage that a fixed API key would have ended.
        Assert.True(result.IsTransient, "401 means the ACCOUNT is broken, not this recipient — the send must survive to retry");
    }

    [Fact]
    public async Task H7_a_forbidden_403_is_transient_too()
    {
        var service = NewService(new StubSendGridClient(HttpStatusCode.Forbidden, "{\"errors\":[{\"message\":\"maximum credits exceeded\"}]}"));
        var result = await service.SendDetailedAsync("client@example.test", "Client", "Subject", "<p>Body</p>");
        Assert.False(result.Success);
        Assert.True(result.IsTransient, "403 (exhausted credits, unverified sender) is account-level and recoverable");
    }

    [Fact]
    public async Task H7_the_not_configured_result_is_transient()
    {
        // A placeholder key fails IsConfigured(). Pre-fix this returned a permanent failure, so
        // one bad deploy of app settings retired every due enrollment forever.
        var service = NewService(new StubSendGridClient(HttpStatusCode.OK, "never reached"), apiKey: "YOUR_SENDGRID_KEY");
        var result = await service.SendDetailedAsync("client@example.test", "Client", "Subject", "<p>Body</p>");
        Assert.False(result.Success);
        Assert.True(result.IsTransient, "missing configuration is fixed in config; the queued work must survive until it is");
    }

    [Fact]
    public async Task H7_a_real_rejection_400_stays_permanent()
    {
        // The other direction, pinned so the fix cannot err permissive: a payload/recipient
        // rejection is SendGrid answering "never" — retrying it on a schedule is spam.
        var service = NewService(new StubSendGridClient(HttpStatusCode.BadRequest, "{\"errors\":[{\"message\":\"Does not contain a valid address\"}]}"));
        var result = await service.SendDetailedAsync("client@example.test", "Client", "Subject", "<p>Body</p>");
        Assert.False(result.Success);
        Assert.False(result.IsTransient, "400 is a verdict on the payload — it must still retire the work");
    }

    // -------------------------------------------- 1b. the chain: service -> dispatcher -> job --

    [Fact]
    public async Task H7_a_key_rotation_does_not_permanently_fail_the_enrollment()
    {
        // The C1-proof test: REAL SendGridEmailService (answering 401), REAL NewsLetterDispatcher,
        // REAL DripCampaignJob. Pre-fix: after one tick the enrollment is Failed with
        // SendAttempts == 0 — dead forever, exactly the register's words. Post-fix: still Active,
        // one attempt recorded, backed off — it outlives the outage.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedCampaignAsync(db, stepCount: 3, nextStepIndex: 0);

        var job = NewJob(db, NewService(new StubSendGridClient(HttpStatusCode.Unauthorized, "{\"errors\":[]}")));
        await job.RunAsync();
        db.ChangeTracker.Clear();

        var enrollment = await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == seed.EnrollmentId);
        Assert.Equal(DripCampaignEnrollmentStatus.Active, enrollment.Status);
        Assert.Equal(1, enrollment.SendAttempts);
        Assert.True(enrollment.NextSendAt > DateTime.UtcNow.AddMinutes(30), "the H13 backoff must push the row behind healthy ones");
        Assert.Equal(0, enrollment.NextStepIndex);   // JOBS-7: a failed send never advances
    }

    [Fact]
    public async Task H7_the_transient_cap_still_bounds_a_long_outage()
    {
        // Regression pin for the other side of the same coin: transient does NOT mean forever.
        // An outage outlasting MaxSendAttempts still retires the row — that is what the resume
        // button is for.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedCampaignAsync(db, stepCount: 3, nextStepIndex: 0, sendAttempts: 4);

        var job = NewJob(db, NewService(new StubSendGridClient(HttpStatusCode.Unauthorized, "{\"errors\":[]}")));
        await job.RunAsync();
        db.ChangeTracker.Clear();

        var enrollment = await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == seed.EnrollmentId);
        Assert.Equal(DripCampaignEnrollmentStatus.Failed, enrollment.Status);
        Assert.Contains("Gave up after", enrollment.LastError);
    }

    // ------------------------------------------------------------------------ 2. resume path --

    [Fact]
    public async Task H7_resume_reactivates_failed_rows_and_never_replays_delivered_steps()
    {
        // The heart of the resume design. An enrollment died at step 3 (index 2) — steps 1 and 2
        // are already in the client's inbox. Resume, then run the job with SendGrid healthy
        // again: EXACTLY ONE email goes out, and it is step 3. Re-enrolling — the only recovery
        // that existed pre-fix — would have re-sent all three.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedCampaignAsync(db, stepCount: 3, nextStepIndex: 2,
            status: DripCampaignEnrollmentStatus.Failed, sendAttempts: 5,
            lastError: "Gave up after 5 attempts. Last error: SendGrid rejected the email. Status: 401 Unauthorized.");

        var controller = NewController(db, seed.AgentId);
        var result = await controller.ResumeFailedEnrollments(seed.CampaignId);
        db.ChangeTracker.Clear();

        Assert.IsType<RedirectToActionResult>(result);
        var resumed = await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == seed.EnrollmentId);
        Assert.Equal(DripCampaignEnrollmentStatus.Active, resumed.Status);
        Assert.Equal(0, resumed.SendAttempts);
        Assert.True(resumed.NextSendAt <= DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(2, resumed.NextStepIndex);      // untouched — still pointing at the unsent step

        // The job's due predicate translates DateTime.UtcNow to MySQL UTC_TIMESTAMP(), which
        // truncates to WHOLE SECONDS -- a row resumed at hh:mm:ss.4 is not "due" until the next
        // second ticks over. Irrelevant at the job's hourly cadence; in a test that runs the job
        // milliseconds after resume it is a coin-flip. Cross the boundary deterministically.
        await Task.Delay(1100);

        // SendGrid healthy again: the campaign finishes from where it stopped.
        var recorder = new StubSendGridClient(HttpStatusCode.Accepted, string.Empty);
        var job = NewJob(db, NewService(recorder));
        await job.RunAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(1, recorder.SendCount);
        Assert.Equal(new[] { "Step 3" }, recorder.SentSubjects);
        var after = await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == seed.EnrollmentId);
        Assert.Equal(DripCampaignEnrollmentStatus.Completed, after.Status);
    }

    [Fact]
    public async Task H7_resume_touches_only_the_owners_failed_rows_in_that_campaign()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var mine = await SeedCampaignAsync(db, stepCount: 2, nextStepIndex: 1,
            status: DripCampaignEnrollmentStatus.Failed);
        // Same campaign, other outcomes — a person's Cancel and a finished run must not reopen.
        var cancelled = await AddEnrollmentAsync(db, mine.AgentId, mine.CampaignId, DripCampaignEnrollmentStatus.Cancelled);
        var completed = await AddEnrollmentAsync(db, mine.AgentId, mine.CampaignId, DripCampaignEnrollmentStatus.Completed);
        // Another agent's failed enrollment in their own campaign.
        var theirs = await SeedCampaignAsync(db, stepCount: 2, nextStepIndex: 0,
            status: DripCampaignEnrollmentStatus.Failed);

        var controller = NewController(db, mine.AgentId);
        await controller.ResumeFailedEnrollments(mine.CampaignId);
        db.ChangeTracker.Clear();

        Assert.Equal(DripCampaignEnrollmentStatus.Active,
            (await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == mine.EnrollmentId)).Status);
        Assert.Equal(DripCampaignEnrollmentStatus.Cancelled,
            (await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == cancelled)).Status);
        Assert.Equal(DripCampaignEnrollmentStatus.Completed,
            (await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == completed)).Status);
        Assert.Equal(DripCampaignEnrollmentStatus.Failed,
            (await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == theirs.EnrollmentId)).Status);
    }

    [Fact]
    public async Task H7_resuming_someone_elses_campaign_is_not_found()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var theirs = await SeedCampaignAsync(db, stepCount: 1, nextStepIndex: 0,
            status: DripCampaignEnrollmentStatus.Failed);
        var me = await SeedCampaignAsync(db, stepCount: 1, nextStepIndex: 0);   // gives me an entitled agent

        var controller = NewController(db, me.AgentId);
        var result = await controller.ResumeFailedEnrollments(theirs.CampaignId);

        Assert.IsType<NotFoundResult>(result);
        db.ChangeTracker.Clear();
        Assert.Equal(DripCampaignEnrollmentStatus.Failed,
            (await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == theirs.EnrollmentId)).Status);
    }

    [Fact]
    public async Task H7_a_resumed_client_who_unsubscribed_meanwhile_is_cancelled_not_mailed()
    {
        // Consent outranks recovery. The client opted out while the enrollment sat Failed;
        // resume must not become a way around the suppression the whole 2026-08-17 consent work
        // built. The job's pre-send IsSuppressed check (the REAL EmailConsentService) cancels
        // the row before anything is dispatched.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedCampaignAsync(db, stepCount: 3, nextStepIndex: 1,
            status: DripCampaignEnrollmentStatus.Failed);
        var client = await db.Clients.SingleAsync(c => c.Id == seed.ClientId);
        client.EmailOptOutAt = DateTime.UtcNow.AddDays(-1);
        client.IsNewsletterSubscribed = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = NewController(db, seed.AgentId);
        await controller.ResumeFailedEnrollments(seed.CampaignId);
        db.ChangeTracker.Clear();

        await Task.Delay(1100);   // same UTC_TIMESTAMP() second-truncation as above

        var recorder = new StubSendGridClient(HttpStatusCode.Accepted, string.Empty);
        var job = NewJob(db, NewService(recorder), realConsent: true);
        await job.RunAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(0, recorder.SendCount);
        var after = await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == seed.EnrollmentId);
        Assert.Equal(DripCampaignEnrollmentStatus.Cancelled, after.Status);
    }

    // ------------------------------------------------------------------------------ plumbing --

    private static SendGridEmailService NewService(StubSendGridClient client, string apiKey = "SG.test-key")
    {
        var service = new SendGridEmailService(
            Options.Create(new EmailSettings
            {
                SendGridApiKey = apiKey,
                FromEmail = "no-reply@iproadvisers.test",
                FromName = "IPRO Test"
            }),
            NullLogger<SendGridEmailService>.Instance);
        service.ClientFactory = _ => client;
        return service;
    }

    private static DripCampaignJob NewJob(IPRODbContext db, IEmailService email, bool realConsent = false)
    {
        IEmailConsentService consent = realConsent
            ? new EmailConsentService(db, new ConfigurationBuilder().Build(),
                NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>())
            : new NoSweepConsent();
        return new DripCampaignJob(
            new UnitOfWork(db), db,
            new NewsLetterDispatcher(new UnitOfWork(db), db, email, new ConfigurationBuilder().Build(),
                NullLogger<NewsLetterDispatcher>.Instance),
            consent, NullLogger<DripCampaignJob>.Instance);
    }

    private static CampaignsController NewController(IPRODbContext db, int agentId)
    {
        var controller = new CampaignsController(
            db,
            new PackageEntitlementService(new UnitOfWork(db), db),
            new NoSweepConsent());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            context, new NoopTempData());
        return controller;
    }

    private sealed record Seed(int AgentId, int ClientId, int CampaignId, int EnrollmentId);

    private static async Task<Seed> SeedCampaignAsync(
        IPRODbContext db, int stepCount, int nextStepIndex,
        DripCampaignEnrollmentStatus status = DripCampaignEnrollmentStatus.Active,
        int sendAttempts = 0, string lastError = "")
    {
        // An entitled agent: Active billing on a rule that includes the campaigns feature, so the
        // REAL PackageEntitlementService lets the controller through (no faked gate).
        var rule = new BillingRule { PackageName = $"DR-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.MarketingCampaign, FeatureName = "Campaigns", IsIncluded = true });

        var agent = new AgentUser
        {
            UserName = $"dr-{Guid.NewGuid():N}"[..20],
            Email = $"dr-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Drip", LastName = "Recovery",
            DomainName = $"dr-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario",
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m,
            Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-10), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });

        var client = new Client
        {
            AgentUserId = agent.Id, FirstName = "Cli", LastName = "Ent",
            Email = $"cl-{Guid.NewGuid():N}"[..12] + "@example.test",
            IsNewsletterSubscribed = true
        };
        db.Add(client);

        var campaign = new DripCampaign
        {
            AgentUserId = agent.Id, Name = "Recovery drip", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Add(campaign);
        await db.SaveChangesAsync();

        for (var i = 0; i < stepCount; i++)
        {
            db.Add(new DripCampaignStep
            {
                DripCampaignId = campaign.Id, SortOrder = i,
                Subject = $"Step {i + 1}", HtmlBody = $"<p>Step {i + 1} body</p>", DelayDays = 1
            });
        }

        var enrollment = new DripCampaignEnrollment
        {
            AgentUserId = agent.Id, DripCampaignId = campaign.Id, ClientId = client.Id,
            Status = status, NextStepIndex = nextStepIndex, SendAttempts = sendAttempts,
            LastError = lastError, StartedAt = DateTime.UtcNow.AddDays(-7),
            NextSendAt = DateTime.UtcNow.AddHours(-1),
            UnsubscribeToken = Guid.NewGuid().ToString("N")
        };
        db.Add(enrollment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(agent.Id, client.Id, campaign.Id, enrollment.Id);
    }

    private static async Task<int> AddEnrollmentAsync(
        IPRODbContext db, int agentId, int campaignId, DripCampaignEnrollmentStatus status)
    {
        var client = new Client
        {
            AgentUserId = agentId, FirstName = "Extra", LastName = "Client",
            Email = $"ex-{Guid.NewGuid():N}"[..12] + "@example.test"
        };
        db.Add(client);
        await db.SaveChangesAsync();
        var enrollment = new DripCampaignEnrollment
        {
            AgentUserId = agentId, DripCampaignId = campaignId, ClientId = client.Id,
            Status = status, NextStepIndex = 1, StartedAt = DateTime.UtcNow.AddDays(-3),
            NextSendAt = DateTime.UtcNow.AddDays(-1),
            UnsubscribeToken = Guid.NewGuid().ToString("N")
        };
        db.Add(enrollment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return enrollment.Id;
    }

    // A real ISendGridClient whose HTTP conversation is scripted: the REAL SendDetailedAsync
    // builds the message, "sends" it here, and classifies the scripted answer.
    private sealed class StubSendGridClient : ISendGridClient
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public int SendCount;
        public List<string> SentSubjects { get; } = new();

        public StubSendGridClient(HttpStatusCode status, string body)
        {
            _status = status; _body = body;
        }

        public string UrlPath { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;

        public System.Net.Http.Headers.AuthenticationHeaderValue AddAuthorization(KeyValuePair<string, string> header) =>
            new("Bearer", "test");

        public Task<Response> MakeRequest(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildResponse());

        public Task<Response> RequestAsync(BaseClient.Method method, string? requestBody = null,
            string? queryParams = null, string? urlPath = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildResponse());

        public Task<Response> SendEmailAsync(SendGridMessage msg, CancellationToken cancellationToken = default)
        {
            SendCount++;
            SentSubjects.Add(msg.Personalizations?.FirstOrDefault()?.Subject ?? msg.Subject ?? string.Empty);
            return Task.FromResult(BuildResponse());
        }

        private Response BuildResponse()
        {
            // Real headers, not null: the service's SUCCESS path reads
            // response.Headers.TryGetValues("X-Message-Id", ...) — a null there turned a healthy
            // 202 into an NRE that the catch-all classified transient, which this suite would
            // have shipped as a green-looking stub bug.
            var carrier = new HttpResponseMessage(_status);
            carrier.Headers.TryAddWithoutValidation("X-Message-Id", "stub-message-id");
            return new Response(_status, new StringContent(_body), carrier.Headers);
        }
    }

    private sealed class NoSweepConsent : IEmailConsentService
    {
        public bool IsSuppressed(Client client, EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }

    private sealed class NoopTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
