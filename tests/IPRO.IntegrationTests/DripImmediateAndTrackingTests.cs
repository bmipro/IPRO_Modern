using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 448 (2026-09-02). The owner enrolled a client in a three-step drip, saw "send immediately",
// and got nothing for an hour: the drip job only wakes hourly. Then he looked for the send and
// could not find it: Email Activity never listed drip steps. And the job had no guard against two
// runs overlapping, which the "send now" fix would have turned from latent into live.
public class DripImmediateAndTrackingTests
{
    // ---- the claim --------------------------------------------------------------------------

    [Fact]
    public async Task A_claim_is_exclusive_until_released()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db);
        var now = DateTime.UtcNow;

        var held = await DripEnrollmentClaims.TryClaimAsync(db, seed.EnrollmentId, now);
        Assert.Equal(0, held);

        // A second run looking at the same row gets nothing.
        Assert.Null(await DripEnrollmentClaims.TryClaimAsync(db, seed.EnrollmentId, now));

        await DripEnrollmentClaims.ReleaseAsync(db, seed.EnrollmentId, held!.Value, resetAttempts: true);
        Assert.Equal(0, await DripEnrollmentClaims.TryClaimAsync(db, seed.EnrollmentId, now));
    }

    [Fact]
    public async Task A_stale_claim_is_taken_over_with_the_attempt_counter_bumped()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db);
        var now = DateTime.UtcNow;

        // Somebody claimed it 20 minutes ago and never came back.
        await db.DripCampaignEnrollments.Where(e => e.Id == seed.EnrollmentId)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.ClaimedAt, now.AddMinutes(-20)));

        Assert.Equal(1, await DripEnrollmentClaims.TryClaimAsync(db, seed.EnrollmentId, now));
    }

    [Fact]
    public async Task Not_due_or_not_active_is_refused()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db);
        var now = DateTime.UtcNow;

        await db.DripCampaignEnrollments.Where(e => e.Id == seed.EnrollmentId)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.NextSendAt, now.AddHours(2)));
        Assert.Null(await DripEnrollmentClaims.TryClaimAsync(db, seed.EnrollmentId, now));

        await db.DripCampaignEnrollments.Where(e => e.Id == seed.EnrollmentId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(e => e.NextSendAt, now.AddHours(-1))
                .SetProperty(e => e.Status, DripCampaignEnrollmentStatus.Completed));
        Assert.Null(await DripEnrollmentClaims.TryClaimAsync(db, seed.EnrollmentId, now));
    }

    [Fact]
    public async Task An_enrollment_whose_processing_kept_dying_is_named_failed_not_hidden()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db);
        var now = DateTime.UtcNow;

        await db.DripCampaignEnrollments.Where(e => e.Id == seed.EnrollmentId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(e => e.ClaimAttempts, DripEnrollmentClaims.MaxAttempts)
                .SetProperty(e => e.ClaimedAt, now.AddMinutes(-30)));

        Assert.Equal(1, await DripEnrollmentClaims.FailExhaustedAsync(db, now));
        var row = await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == seed.EnrollmentId);
        Assert.Equal(DripCampaignEnrollmentStatus.Failed, row.Status);
        Assert.Contains("interrupted", row.LastError);
        Assert.Null(row.ClaimedAt);
    }

    // ---- send now, and only once ------------------------------------------------------------

    [Fact]
    public async Task Run_enrollment_sends_step_1_now_and_the_hourly_run_does_not_send_it_again()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db);
        var dispatcher = new CountingDispatcher(db);
        var job = NewJob(db, dispatcher);

        await job.RunEnrollmentAsync(seed.EnrollmentId);
        Assert.Equal(1, dispatcher.Calls);

        db.ChangeTracker.Clear();
        var after = await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == seed.EnrollmentId);
        Assert.Equal(1, after.NextStepIndex);
        Assert.NotNull(after.LastSentAt);
        Assert.Null(after.ClaimedAt);                       // released
        Assert.True(after.NextSendAt > DateTime.UtcNow.AddHours(23), "step 2 waits its full day");

        // The hourly run that follows finds nothing due.
        await job.RunAsync();
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task Run_enrollment_before_the_step_is_due_does_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db);
        await db.DripCampaignEnrollments.Where(e => e.Id == seed.EnrollmentId)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.NextSendAt, DateTime.UtcNow.AddDays(3)));
        var dispatcher = new CountingDispatcher(db);

        await NewJob(db, dispatcher).RunEnrollmentAsync(seed.EnrollmentId);
        Assert.Equal(0, dispatcher.Calls);
    }

    [Fact]
    public async Task A_run_that_finds_the_enrollment_already_claimed_sends_nothing()
    {
        // The overlap the claim exists for: the hourly run took it a moment ago.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db);
        var other = await DripEnrollmentClaims.TryClaimAsync(db, seed.EnrollmentId, DateTime.UtcNow);
        Assert.NotNull(other);
        var dispatcher = new CountingDispatcher(db);

        await NewJob(db, dispatcher).RunEnrollmentAsync(seed.EnrollmentId);
        Assert.Equal(0, dispatcher.Calls);

        await DripEnrollmentClaims.ReleaseAsync(db, seed.EnrollmentId, other!.Value, resetAttempts: true);
        await NewJob(db, dispatcher).RunAsync();
        Assert.Equal(1, dispatcher.Calls);
    }

    // ---- Email Activity finally lists drip steps ---------------------------------------------

    [Fact]
    public async Task Email_activity_lists_each_drip_step_and_its_recipients_scoped_to_the_agent()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var mine = await SeedAsync(db);
        var theirs = await SeedAsync(db);
        var now = DateTime.UtcNow;

        db.AddRange(
            new DripCampaignStepSend { DripCampaignEnrollmentId = mine.EnrollmentId, DripCampaignStepId = mine.Step1Id, StepIndex = 0, Email = "a@example.test", RecipientName = "Ann", Status = NewsLetterRecipientStatus.Sent, SentAt = now.AddMinutes(-10), DeliveredAt = now.AddMinutes(-9) },
            new DripCampaignStepSend { DripCampaignEnrollmentId = mine.EnrollmentId, DripCampaignStepId = mine.Step1Id, StepIndex = 0, Email = "b@example.test", RecipientName = "Bob", Status = NewsLetterRecipientStatus.Failed, SentAt = now.AddMinutes(-8), FailureReason = "mailbox full" },
            new DripCampaignStepSend { DripCampaignEnrollmentId = theirs.EnrollmentId, DripCampaignStepId = theirs.Step1Id, StepIndex = 0, Email = "z@example.test", RecipientName = "Zed", Status = NewsLetterRecipientStatus.Sent, SentAt = now });
        await db.SaveChangesAsync();

        var rows = await EmailActivityQueries.DripStepRowsAsync(db, mine.AgentId);
        var row = Assert.Single(rows);
        Assert.Equal("drip", row.TypeKey);
        Assert.Equal(mine.Step1Id, row.Id);
        Assert.Equal("S1", row.Subject);
        Assert.Equal("SD · step 1", row.Detail);
        Assert.Equal(2, row.Recipients);
        Assert.Equal(2, row.Sent);
        Assert.Equal(1, row.Delivered);
        Assert.Equal(1, row.Failed);

        var recipients = await EmailActivityQueries.DripStepRecipientsAsync(db, mine.AgentId, mine.Step1Id);
        Assert.Equal(new[] { "Ann", "Bob" }, recipients.Select(r => r.Name).ToArray());
        Assert.Equal("mailbox full", recipients.Single(r => r.Name == "Bob").Issue);

        // Another agent's campaign is invisible, by id and by listing.
        Assert.Empty(await EmailActivityQueries.DripStepRecipientsAsync(db, mine.AgentId, theirs.Step1Id));
        Assert.Single(await EmailActivityQueries.DripStepRowsAsync(db, theirs.AgentId));
    }

    // ---- the wiring the controllers and views must carry ------------------------------------

    [Fact]
    public async Task Enrolling_enqueues_step_1_when_it_is_due_now_and_nothing_when_it_is_not()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db);   // step 1 has DelayDays = 0
        var client2 = new Client
        {
            AgentUserId = seed.AgentId, FirstName = "E", LastName = "F",
            Email = $"di-{Guid.NewGuid():N}"[..14] + "@example.test", IsNewsletterSubscribed = true
        };
        db.Add(client2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var jobs = new RecordingJobClient();

        await NewController(db, seed.AgentId, jobs).EnrollClient(seed.CampaignId, client2.Id);

        // The real action, through the real controller: exactly one one-off run, for this enrollment.
        var created = Assert.Single(jobs.Created);
        Assert.Equal("RunEnrollmentAsync", created.Method.Name);
        var enrollmentId = await db.DripCampaignEnrollments
            .Where(e => e.ClientId == client2.Id && e.DripCampaignId == seed.CampaignId)
            .Select(e => e.Id).SingleAsync();
        Assert.Equal(enrollmentId, (int)created.Args[0]!);

        // A campaign whose first step waits a day enqueues nothing -- the hourly run owns it.
        var later = new DripCampaign { AgentUserId = seed.AgentId, Name = "Later", IsActive = true };
        db.Add(later);
        await db.SaveChangesAsync();
        db.Add(new DripCampaignStep { DripCampaignId = later.Id, SortOrder = 0, Subject = "L1", HtmlBody = "<p>l</p>", DelayDays = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        jobs.Created.Clear();

        await NewController(db, seed.AgentId, jobs).EnrollClient(later.Id, seed.ClientId);
        Assert.Empty(jobs.Created);
    }

    [Fact]
    public void Resuming_a_stopped_enrollment_gives_it_a_fresh_claim_slate()
    {
        // Otherwise FailExhausted would re-stop it on the next hourly run.
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\CampaignsController.cs"));
        var resume = src[src.IndexOf("ResumeFailedEnrollments", StringComparison.Ordinal)..];
        Assert.Contains("ClaimAttempts = 0", resume);
        Assert.Contains("ClaimedAt = null", resume);
    }

    [Fact]
    public void The_job_exposes_the_one_off_run_on_the_drip_queue()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Scheduler\DripCampaignJob.cs"));
        Assert.Contains("[Hangfire.Queue(\"drip\")]", src);
        Assert.Contains("public async Task RunEnrollmentAsync(int enrollmentId)", src);
        Assert.Contains("DripEnrollmentClaims.TryClaimAsync(", src);
        Assert.Contains("DripEnrollmentClaims.ReleaseAsync(", src);
        Assert.Contains("DripEnrollmentClaims.FailExhaustedAsync(", src);
        // The hourly batch reads the claim-aware query, not a bare Status/NextSendAt filter.
        Assert.Contains("DripEnrollmentClaims.Due(_db", src);
    }

    [Fact]
    public void Email_activity_wires_the_drip_kind_in_the_list_the_detail_and_the_view()
    {
        var ctrl = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\EmailActivityController.cs"));
        Assert.Contains("EmailActivityQueries.DripStepRowsAsync(_db, AgentId)", ctrl);
        Assert.Contains("\"drip\" => await EmailActivityQueries.DripStepRecipientsAsync(_db, AgentId, id)", ctrl);

        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\EmailActivity\Index.cshtml"));
        Assert.Contains("(\"drip\", \"Campaigns\")", view);
        Assert.Contains("\"drip\" => \"fa-bullhorn\"", view);
    }

    [Fact]
    public void The_campaign_page_tells_the_truth_about_provider_and_empty_steps()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Campaigns\Details.cshtml"));
        Assert.DoesNotContain("SendGrid", view);
        Assert.Contains("nothing sent yet", view);
    }

    [Fact]
    public void The_schema_repair_adds_the_claim_columns()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        Assert.Contains("\"DripCampaignEnrollments\", \"ClaimedAt\"", src);
        Assert.Contains("\"DripCampaignEnrollments\", \"ClaimAttempts\"", src);
    }

    // ---- harness -----------------------------------------------------------------------------

    private sealed record Seed(int AgentId, int ClientId, int CampaignId, int Step1Id, int EnrollmentId);

    private static async Task<Seed> SeedAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"DI-{Guid.NewGuid():N}"[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"di-{Guid.NewGuid():N}"[..20],
            Email = $"di-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Drip", LastName = "Immediate",
            DomainName = $"di-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var campaign = new DripCampaign { AgentUserId = agent.Id, Name = "SD", IsActive = true };
        db.Add(campaign);
        await db.SaveChangesAsync();
        var step1 = new DripCampaignStep { DripCampaignId = campaign.Id, SortOrder = 0, Subject = "S1", HtmlBody = "<p>one</p>", DelayDays = 0 };
        var step2 = new DripCampaignStep { DripCampaignId = campaign.Id, SortOrder = 1, Subject = "S2", HtmlBody = "<p>two</p>", DelayDays = 1 };
        db.AddRange(step1, step2);
        var client = new Client
        {
            AgentUserId = agent.Id,
            FirstName = "C", LastName = "D",
            Email = $"di-{Guid.NewGuid():N}"[..14] + "@example.test",
            IsNewsletterSubscribed = true
        };
        db.Add(client);
        await db.SaveChangesAsync();
        var enrollment = new DripCampaignEnrollment
        {
            AgentUserId = agent.Id,
            DripCampaignId = campaign.Id,
            ClientId = client.Id,
            Status = DripCampaignEnrollmentStatus.Active,
            NextStepIndex = 0,
            StartedAt = DateTime.UtcNow,
            NextSendAt = DateTime.UtcNow.AddMinutes(-1),
            UnsubscribeToken = Guid.NewGuid().ToString("N")
        };
        db.Add(enrollment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(agent.Id, client.Id, campaign.Id, step1.Id, enrollment.Id);
    }

    private static IPRO.Scheduler.DripCampaignJob NewJob(IPRODbContext db, NewsLetterDispatcher dispatcher) =>
        new(new UnitOfWork(db), db, dispatcher, new PassthroughConsent(), NullLogger<IPRO.Scheduler.DripCampaignJob>.Instance);

    private sealed class CountingDispatcher : NewsLetterDispatcher
    {
        public int Calls;
        public CountingDispatcher(IPRODbContext db) : base(
            new UnitOfWork(db), db, new StubEmail(), new ConfigurationBuilder().Build(),
            NullLogger<NewsLetterDispatcher>.Instance)
        { }
        public override Task<EmailSendResult?> DispatchDripStepAsync(int campaignId, int stepIndex, string toEmail, string toName, string? unsubscribeToken = null, int enrollmentId = 0)
        {
            Calls++;
            return Task.FromResult<EmailSendResult?>(EmailSendResult.Sent());
        }
    }

    private sealed class PassthroughConsent : IPRO.Business.Services.IEmailConsentService
    {
        public bool IsSuppressed(Client client, IPRO.Business.Services.EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<IPRO.Business.Services.SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }

    private sealed class StubEmail : IEmailService
    {
        public Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(true);
        public Task<EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private static CampaignsController NewController(IPRODbContext db, int agentId, RecordingJobClient jobs)
    {
        var controller = new CampaignsController(db, new GrantAllEntitlements(), new PassthroughConsent(), jobs);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            context, new DiscardingTempDataProvider());
        return controller;
    }

    private sealed class GrantAllEntitlements : IPackageEntitlementService
    {
        public Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) =>
            throw new NotSupportedException();
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class DiscardingTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
