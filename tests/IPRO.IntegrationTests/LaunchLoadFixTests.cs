using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Communication.Email;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Scheduler;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using EmailSendResult = IPRO.Email.EmailSendResult;

namespace IPRO.IntegrationTests;

// 493 (2026-09-17), the narrow pre-launch audit's one serious finding. 491 put an unbounded wait
// INSIDE the claimed send loops: once the hour's bulk allowance was spent, the next send waited most
// of an hour in EmailSendGate while its 15-minute claim went stale, so the sweep re-claimed the send,
// mailed the recipient in progress a second time, and after three thefts retired the send as Failed
// with hundreds still queued. Did You Know and drip steps, with no heartbeat at all, would have
// re-sent every 15 minutes; five parked workers stopped every other job; and a password reset, an
// invoice or a lead notification waited inside the web request until Azure's 230-second cut-off.
//
// The rule now: the gate never waits longer than the bound (Email__MaxSlotWaitSeconds, 90 by
// default). A longer wait comes back as a DEFERRED result -- transient, so the four blast loops take
// the pause path they already had (Scheduled, claim released, no attempt spent, resumed by the next
// minute's pass) -- and Did You Know, drip and the request-path callers each get a "not counted"
// answer instead of a hang. A fake clock drives the gate; nothing here sleeps.
public class LaunchLoadFixTests
{
    // ---- the bound --------------------------------------------------------------------------

    [Fact]
    public async Task A_wait_longer_than_the_bound_is_refused_and_no_slot_is_taken()
    {
        var clock = new FakeClock();
        var gate = new EmailSendGate(1000, 100, 0, 10, () => clock.Now, clock.Delay);
        for (var i = 0; i < 90; i++) Assert.Null(await gate.TryWaitForSlotAsync(bulk: true, TimeSpan.FromSeconds(90)));

        // The 91st bulk send would wait about an hour: refused, nothing slept, nothing recorded.
        var refused = await gate.TryWaitForSlotAsync(bulk: true, TimeSpan.FromSeconds(90));
        Assert.NotNull(refused);
        Assert.InRange(refused!.Value.TotalMinutes, 59.9, 61);
        Assert.Empty(clock.Waits);

        // The reserve is untouched by the refusal: a transactional send still goes at once.
        Assert.Null(await gate.TryWaitForSlotAsync(bulk: false, TimeSpan.FromSeconds(90)));
        Assert.Empty(clock.Waits);
    }

    [Fact]
    public async Task A_wait_inside_the_bound_is_made_and_the_slot_taken()
    {
        var clock = new FakeClock();
        var gate = new EmailSendGate(3, 100, 0, 0, () => clock.Now, clock.Delay);
        for (var i = 0; i < 3; i++) await gate.WaitForSlotAsync(bulk: true);

        Assert.Null(await gate.TryWaitForSlotAsync(bulk: true, TimeSpan.FromSeconds(90)));
        Assert.InRange(Assert.Single(clock.Waits).TotalSeconds, 59.5, 61);
    }

    [Fact]
    public async Task A_provider_hold_longer_than_the_bound_is_refused_too()
    {
        var clock = new FakeClock();
        var gate = new EmailSendGate(30, 100, 5, 10, () => clock.Now, clock.Delay);
        gate.ReportThrottled(TimeSpan.FromMinutes(5));

        var refused = await gate.TryWaitForSlotAsync(bulk: false, TimeSpan.FromSeconds(90));
        Assert.NotNull(refused);
        Assert.InRange(refused!.Value.TotalMinutes, 4.9, 5.1);
        Assert.Empty(clock.Waits);
    }

    [Fact]
    public async Task The_unbounded_wait_is_still_there_for_a_caller_that_wants_it()
    {
        var clock = new FakeClock();
        var gate = new EmailSendGate(1000, 100, 0, 10, () => clock.Now, clock.Delay);
        for (var i = 0; i < 90; i++) await gate.WaitForSlotAsync(bulk: true);
        await gate.WaitForSlotAsync(bulk: true);
        Assert.InRange(Assert.Single(clock.Waits).TotalMinutes, 59.9, 61);
    }

    // ---- the Azure sender -------------------------------------------------------------------

    [Fact]
    public async Task A_send_with_no_slot_inside_the_bound_comes_back_deferred_and_never_reaches_the_provider()
    {
        var clock = new FakeClock();
        var gate = new EmailSendGate(1000, 100, 0, 20, () => clock.Now, clock.Delay);
        for (var i = 0; i < 80; i++) await gate.WaitForSlotAsync(bulk: true);

        var calls = 0;
        var service = NewAzureService(gate, _ => { calls++; return Task.FromResult("op-1"); });
        var result = await service.SendDetailedAsync("a@example.test", "A", "s", "<p>b</p>",
            customArgs: new Dictionary<string, string> { ["ipro_entity"] = "newsletter" });

        Assert.False(result.Success);
        Assert.True(result.IsTransient, "a deferral must take the pause path in every dispatcher");
        Assert.True(result.IsDeferred);
        Assert.NotNull(result.RetryAfter);
        Assert.InRange(result.RetryAfter!.Value.TotalMinutes, 59.9, 61);
        Assert.Contains("limit", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, calls);
        Assert.Empty(clock.Waits);

        // The reserve still carries a transactional send straight through.
        var invoice = await service.SendDetailedAsync("b@example.test", "B", "s", "<p>b</p>");
        Assert.True(invoice.Success);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task The_bulk_fan_out_goes_through_the_gate_as_well()
    {
        // SendBulkAsync (SuperAdmin retiring a template mails every affected agent) was the one send
        // path 491 did not pace.
        var clock = new FakeClock();
        var gate = new EmailSendGate(1000, 100, 0, 0, () => clock.Now, clock.Delay);
        for (var i = 0; i < 100; i++) await gate.WaitForSlotAsync(bulk: false);

        var calls = 0;
        var service = NewAzureService(gate, _ => { calls++; return Task.FromResult("op"); });
        var allSent = await service.SendBulkAsync(
            new[] { new EmailRecipient("a@example.test", "A"), new EmailRecipient("b@example.test", "B") }, "s", "<p>b</p>");

        Assert.False(allSent);
        Assert.Equal(0, calls);
        Assert.Empty(clock.Waits);
    }

    [Fact]
    public void The_defaults_carry_the_bound_and_a_bigger_hourly_reserve()
    {
        // The hourly reserve for transactional mail goes from 10 to 20 (bulk keeps 80 of the 100):
        // on launch day a sign-up, a password reset and an invoice must not be refused for want of
        // a slot, and the reserve is what stands between them and a deferral.
        var defaults = new EmailSettings();
        Assert.Equal(20, defaults.TransactionalReservePerHour);
        Assert.Equal(90, defaults.MaxSlotWaitSeconds);

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:MaxSlotWaitSeconds"] = "15"
        }).Build();
        var bound = new EmailSettings();
        cfg.GetSection("Email").Bind(bound);
        Assert.Equal(15, bound.MaxSlotWaitSeconds);
    }

    // ---- Did You Know -----------------------------------------------------------------------

    [Fact]
    public async Task A_deferred_did_you_know_item_releases_its_claim_counts_no_attempt_and_ends_the_pass()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (first, second) = await SeedTwoDidYouKnowItemsAsync(db);

        var email = new DeferringEmailService();
        var job = new DidYouKnowEmailDispatchJob(db, email, new StubConsent(),
            new ConfigurationBuilder().Build(), NullLogger<DidYouKnowEmailDispatchJob>.Instance);
        await job.RunAsync();
        db.ChangeTracker.Clear();

        var one = await db.DidYouKnowEmailQueueItems.AsNoTracking().SingleAsync(q => q.Id == first);
        Assert.Null(one.SentAtUtc);
        Assert.Null(one.ClaimedAtUtc);         // released at once, not left to go stale in 15 minutes
        Assert.Equal(0, one.SendAttempts);      // a deferral is not a failed attempt
        // The pass ended on the first deferral: the second item was not even claimed.
        var two = await db.DidYouKnowEmailQueueItems.AsNoTracking().SingleAsync(q => q.Id == second);
        Assert.Null(two.ClaimedAtUtc);
        Assert.Equal(1, email.Calls);

        // Any number of passes later it is still due, still unsent, still at zero attempts.
        for (var i = 0; i < 6; i++) await job.RunAsync();
        db.ChangeTracker.Clear();
        one = await db.DidYouKnowEmailQueueItems.AsNoTracking().SingleAsync(q => q.Id == first);
        Assert.Null(one.SentAtUtc);
        Assert.Equal(0, one.SendAttempts);
        Assert.NotEqual(DidYouKnowQueueStatuses.Failed, one.Status);
    }

    // ---- drip -------------------------------------------------------------------------------

    [Fact]
    public async Task A_deferred_drip_step_stays_due_with_no_attempt_and_leaves_no_step_send_row()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (enrollmentId, secondEnrollmentId) = await SeedDripAsync(db);

        var email = new DeferringEmailService();
        var job = new DripCampaignJob(new UnitOfWork(db), db,
            new NewsLetterDispatcher(new UnitOfWork(db), db, email, new ConfigurationBuilder().Build(), NullLogger<NewsLetterDispatcher>.Instance),
            new StubConsent(), NullLogger<DripCampaignJob>.Instance);
        await job.RunAsync();
        db.ChangeTracker.Clear();

        var enrollment = await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == enrollmentId);
        Assert.Equal(DripCampaignEnrollmentStatus.Active, enrollment.Status);
        Assert.Equal(0, enrollment.SendAttempts);
        Assert.Equal(0, enrollment.NextStepIndex);
        Assert.True(enrollment.NextSendAt <= DateTime.UtcNow, "no back-off: the step is still due");
        Assert.Null(enrollment.ClaimedAt);
        // No Failed step-send row for a step that was never attempted.
        Assert.Equal(0, await db.DripCampaignStepSends.CountAsync(s => s.DripCampaignEnrollmentId == enrollmentId));
        // The batch stopped at the first deferral: the second enrollment was not tried.
        Assert.Equal(1, email.Calls);
        Assert.Null((await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.Id == secondEnrollmentId)).ClaimedAt);
    }

    // ---- the newsletter roll-up writer ------------------------------------------------------

    [Fact]
    public void The_tracking_recorders_never_mark_a_whole_send_row_modified()
    {
        // Repository.Update marks EVERY column modified. On the newsletter roll-up that rewrote Status
        // and ClaimedAt from a snapshot taken moments earlier, over a terminal or pause write the
        // dispatcher had just made -- resurrecting a finished send into "Sending with a stale claim",
        // which the sweep then re-claims at the cost of an attempt. 488 made the path live on every
        // pixel hit. The entities are tracked; assignment is enough.
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Business\Services\NewsLetterService.cs"));
        var start = src.IndexOf("public async Task RecordRecipientEventAsync", StringComparison.Ordinal);
        var end = src.IndexOf("private async Task SuppressDripRecipientAsync", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        Assert.DoesNotContain(".Update(", src[start..end]);
    }

    // ---- what a paused send looks like ------------------------------------------------------

    [Fact]
    public void A_paused_send_reads_as_in_progress_on_the_activity_screen()
    {
        // On launch day every blast pauses at least once an hour. "Scheduled, Sent 0" for a send
        // half-way through reads as "it never went out", and an adviser who believes that presses
        // Send again.
        static IPRO.Web.Controllers.EmailActivityRow Row(string status, int sent) =>
            new("Newsletter", "newsletter", 1, "s", "", status, null, 300, sent, 0, 0, 0);
        Assert.Equal("In progress", Row("Scheduled", 150).DisplayStatus);
        Assert.Equal("Scheduled", Row("Scheduled", 0).DisplayStatus);
        Assert.Equal("Sent", Row("Sent", 300).DisplayStatus);
        Assert.Equal("Sending", Row("Sending", 10).DisplayStatus);

        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\EmailActivity\Index.cshtml"));
        Assert.Contains("row.DisplayStatus", view);
        // And the list is built once per request, not twice.
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\EmailActivityController.cs"));
        var index = controller[controller.IndexOf("public async Task<IActionResult> Index(", StringComparison.Ordinal)..];
        index = index[..index.IndexOf("return View(rows);", StringComparison.Ordinal)];
        Assert.Equal(1, index.Split("LoadSendsAsync()").Length - 1);
    }

    // ---- harness ----------------------------------------------------------------------------

    private static AzureEmailService NewAzureService(EmailSendGate gate, Func<EmailMessage, Task<string>> sendCore)
    {
        var settings = Options.Create(new EmailSettings
        {
            Provider = "Azure",
            AzureCommunicationConnectionString = "endpoint=https://x.canada.communication.azure.com/;accesskey=abc",
            FromEmail = "no-reply@example.test"
        });
        return new AzureEmailService(settings, NullLogger<AzureEmailService>.Instance, gate)
        {
            ClientFactory = _ => new StubEmailClient(sendCore)
        };
    }

    private sealed class StubEmailClient : EmailClient
    {
        private readonly Func<EmailMessage, Task<string>> _sendCore;
        public StubEmailClient(Func<EmailMessage, Task<string>> sendCore) => _sendCore = sendCore;

        public override async Task<EmailSendOperation> SendAsync(WaitUntil wait, EmailMessage message, CancellationToken cancellationToken = default)
        {
            var id = await _sendCore(message);
            return new StubOperation(id);
        }

        private sealed class StubOperation : EmailSendOperation
        {
            private readonly string _id;
            public StubOperation(string id) => _id = id;
            public override string Id => _id;
        }
    }

    private sealed class DeferringEmailService : IEmailService
    {
        public int Calls { get; private set; }
        public Task<EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null)
        {
            Calls++;
            return Task.FromResult(EmailSendResult.Deferred(TimeSpan.FromMinutes(55)));
        }
        public async Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) =>
            (await SendDetailedAsync(a, b, c, d, e, f, g, h, i)).Success;
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> r, string s, string h, string? t = null) => throw new NotSupportedException();
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => throw new NotSupportedException();
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

    private static AgentUser NewAgent(string prefix) => new()
    {
        UserName = $"{prefix}-{Guid.NewGuid():N}"[..20],
        Email = $"{prefix}-{Guid.NewGuid():N}"[..12] + "@example.test",
        FirstName = "Launch", LastName = "Load",
        DomainName = $"{prefix}-{Guid.NewGuid():N}"[..24],
        Country = "Canada", Province = "Ontario"
    };

    private static Client NewClient(int agentId, string prefix) => new()
    {
        AgentUserId = agentId,
        FirstName = prefix, LastName = "Client",
        Email = $"{prefix}-{Guid.NewGuid():N}"[..12] + "@example.test"
    };

    private static async Task<(int First, int Second)> SeedTwoDidYouKnowItemsAsync(IPRODbContext db)
    {
        var agent = NewAgent("ll-dyk");
        db.Add(agent);
        await db.SaveChangesAsync();
        var article = new Article { AgentUserId = agent.Id, Title = "Deferred article", Content = "<p>content</p>", IsPublished = true };
        var clientA = NewClient(agent.Id, "dyk-a");
        var clientB = NewClient(agent.Id, "dyk-b");
        db.AddRange(article, clientA, clientB);
        await db.SaveChangesAsync();
        var first = new DidYouKnowEmailQueueItem { ArticleId = article.Id, ClientId = clientA.Id, ScheduledForUtc = DateTime.UtcNow.AddMinutes(-10) };
        var second = new DidYouKnowEmailQueueItem { ArticleId = article.Id, ClientId = clientB.Id, ScheduledForUtc = DateTime.UtcNow.AddMinutes(-5) };
        db.AddRange(first, second);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (first.Id, second.Id);
    }

    private static async Task<(int Enrollment, int SecondEnrollment)> SeedDripAsync(IPRODbContext db)
    {
        var agent = NewAgent("ll-drip");
        db.Add(agent);
        await db.SaveChangesAsync();
        var campaign = new DripCampaign { AgentUserId = agent.Id, Name = "Deferred drip", IsActive = true };
        var clientA = NewClient(agent.Id, "drip-a");
        var clientB = NewClient(agent.Id, "drip-b");
        db.AddRange(campaign, clientA, clientB);
        await db.SaveChangesAsync();
        db.AddRange(
            new DripCampaignStep { DripCampaignId = campaign.Id, Subject = "Step one", HtmlBody = "<p>one</p>", DelayDays = 0, SortOrder = 0 },
            new DripCampaignStep { DripCampaignId = campaign.Id, Subject = "Step two", HtmlBody = "<p>two</p>", DelayDays = 1, SortOrder = 1 });
        var first = new DripCampaignEnrollment { AgentUserId = agent.Id, DripCampaignId = campaign.Id, ClientId = clientA.Id, NextStepIndex = 0, NextSendAt = DateTime.UtcNow.AddMinutes(-10) };
        var second = new DripCampaignEnrollment { AgentUserId = agent.Id, DripCampaignId = campaign.Id, ClientId = clientB.Id, NextStepIndex = 0, NextSendAt = DateTime.UtcNow.AddMinutes(-5) };
        db.AddRange(first, second);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (first.Id, second.Id);
    }

    private sealed class FakeClock
    {
        public DateTime Now = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);
        public List<TimeSpan> Waits { get; } = new();

        public Task Delay(TimeSpan wait, CancellationToken ct)
        {
            Waits.Add(wait);
            Now = Now.Add(wait);
            return Task.CompletedTask;
        }
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IPRO.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
