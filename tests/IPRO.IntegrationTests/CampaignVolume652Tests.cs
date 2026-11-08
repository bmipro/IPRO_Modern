using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 652 (634's third). The campaign list joined every step to every enrollment for two numbers, a campaign's
// page loaded every send of every step and the whole client book into a dropdown, and Email Activity loaded
// every drip send ever made, ignoring the period. Now they count in the database, the client is found as the
// adviser types, and the drip rows keep to the period.
public class CampaignVolume652Tests
{
    [Fact]
    public async Task The_campaign_list_counts_steps_and_active_enrollments_in_the_database()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var mine = await SeedAgentAsync(db, "mine");
        var theirs = await SeedAgentAsync(db, "theirs");
        var busy = await SeedCampaignAsync(db, mine, "Busy", steps: 3);
        var quiet = await SeedCampaignAsync(db, mine, "Quiet", steps: 1);
        await SeedCampaignAsync(db, theirs, "Theirs", steps: 2);
        var client = await SeedClientAsync(db, mine, "Avery", "Stone", "avery@example.test");
        for (var i = 0; i < 5; i++) db.Add(Enrollment(mine, busy, client, DripCampaignEnrollmentStatus.Active));
        db.Add(Enrollment(mine, busy, client, DripCampaignEnrollmentStatus.Completed));
        db.Add(Enrollment(mine, busy, client, DripCampaignEnrollmentStatus.Cancelled));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var model = Assert.IsType<CampaignIndexViewModel>(Assert.IsType<ViewResult>(await Controller(db, mine).Index()).Model);

        Assert.Equal(new[] { "Quiet", "Busy" }.OrderBy(n => n), model.Campaigns.Select(c => c.Name).OrderBy(n => n));
        Assert.Equal(3, model.StepCounts[busy]);
        Assert.Equal(1, model.StepCounts[quiet]);
        Assert.Equal(5, model.ActiveEnrollmentCounts[busy]);
        Assert.Equal(0, model.ActiveEnrollmentCounts.GetValueOrDefault(quiet));
        Assert.All(model.Campaigns, c => Assert.Empty(c.Steps));   // nothing joined any more
    }

    [Fact]
    public async Task A_campaigns_page_counts_each_steps_sends_in_the_database_and_loads_no_client_book()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var mine = await SeedAgentAsync(db, "mine");
        var campaign = await SeedCampaignAsync(db, mine, "Busy", steps: 2);
        var client = await SeedClientAsync(db, mine, "Avery", "Stone", "avery@example.test");
        var enrollment = Enrollment(mine, campaign, client, DripCampaignEnrollmentStatus.Active);
        db.Add(enrollment);
        await db.SaveChangesAsync();
        var steps = await db.DripCampaignSteps.Where(s => s.DripCampaignId == campaign).OrderBy(s => s.SortOrder).Select(s => s.Id).ToListAsync();
        var now = DateTime.UtcNow;
        db.AddRange(
            Send(enrollment.Id, steps[0], sent: now, delivered: now, opened: now, clicked: now),
            Send(enrollment.Id, steps[0], sent: now, delivered: now),
            Send(enrollment.Id, steps[0], sent: null, delivered: null, opened: now),   // an open without a sent time still counts as sent
            Send(enrollment.Id, steps[0], sent: null, delivered: null),               // queued: not sent
            Send(enrollment.Id, steps[1], sent: now));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var model = Assert.IsType<CampaignDetailsViewModel>(Assert.IsType<ViewResult>(await Controller(db, mine).Details(campaign)).Model);

        var first = model.StepPerformance[steps[0]];
        Assert.Equal((3, 2, 2, 1), (first.Sent, first.Delivered, first.Opened, first.Clicked));
        var second = model.StepPerformance[steps[1]];
        Assert.Equal((1, 0, 0, 0), (second.Sent, second.Delivered, second.Opened, second.Clicked));
        Assert.Empty(model.Clients);
    }

    [Fact]
    public async Task The_client_box_finds_the_advisers_own_clients_with_an_email_as_they_type()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var mine = await SeedAgentAsync(db, "mine");
        var theirs = await SeedAgentAsync(db, "theirs");
        var campaign = await SeedCampaignAsync(db, mine, "Busy", steps: 1);
        var theirCampaign = await SeedCampaignAsync(db, theirs, "Theirs", steps: 1);
        await SeedClientAsync(db, mine, "Blake", "Stone", "blake@example.test");
        await SeedClientAsync(db, mine, "Avery", "Stone", "avery@example.test");
        await SeedClientAsync(db, mine, "Casey", "Stone", "");                        // no email: cannot be enrolled
        await SeedClientAsync(db, mine, "Dana", "Moss", "dana.stonehouse@example.test");
        await SeedClientAsync(db, theirs, "Erin", "Stone", "erin@example.test");     // another adviser's

        var found = await OptionsAsync(Controller(db, mine), campaign, "sto");
        Assert.Equal(new[] { "Moss, Dana - dana.stonehouse@example.test", "Stone, Avery - avery@example.test", "Stone, Blake - blake@example.test" },
            found.Select(f => f.Label));
        Assert.All(found, f => Assert.True(f.Id > 0));
        Assert.Equal(new[] { "Stone, Avery - avery@example.test" }, (await OptionsAsync(Controller(db, mine), campaign, "avery st")).Select(f => f.Label));
        Assert.Empty(await OptionsAsync(Controller(db, mine), campaign, "s"));        // two letters at least
        Assert.Empty(await OptionsAsync(Controller(db, mine), campaign, null));
        Assert.IsType<NotFoundResult>(await Controller(db, mine).ClientOptions(theirCampaign, "sto"));

        for (var i = 0; i < 25; i++) await SeedClientAsync(db, mine, $"Many{i:00}", "Stonefield", $"many{i}@example.test");
        Assert.Equal(20, (await OptionsAsync(Controller(db, mine), campaign, "stonefield")).Count);
    }

    [Fact]
    public async Task Email_Activity_keeps_the_drip_rows_to_the_period_shown()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var mine = await SeedAgentAsync(db, "mine");
        var campaign = await SeedCampaignAsync(db, mine, "Busy", steps: 1);
        var client = await SeedClientAsync(db, mine, "Avery", "Stone", "avery@example.test");
        var enrollment = Enrollment(mine, campaign, client, DripCampaignEnrollmentStatus.Active);
        db.Add(enrollment);
        await db.SaveChangesAsync();
        var step = await db.DripCampaignSteps.Where(s => s.DripCampaignId == campaign).Select(s => s.Id).SingleAsync();
        var now = DateTime.UtcNow;
        db.AddRange(Send(enrollment.Id, step, sent: now.AddDays(-40), delivered: now.AddDays(-40)),
                    Send(enrollment.Id, step, sent: now.AddDays(-2), delivered: null));
        await db.SaveChangesAsync();

        Assert.Equal(2, Assert.Single(await EmailActivityQueries.DripStepRowsAsync(db, mine)).Recipients);
        var recent = Assert.Single(await EmailActivityQueries.DripStepRowsAsync(db, mine, now.AddDays(-30)));
        Assert.Equal((1, 1, 0), (recent.Recipients, recent.Sent, recent.Delivered));
        Assert.Empty(await EmailActivityQueries.DripStepRowsAsync(db, mine, now.AddDays(-1)));
    }

    [Fact]
    public void The_pages_and_the_start_up_indexes_say_so()
    {
        var index = Read(@"src\IPRO.Web\Views\Campaigns\Index.cshtml");
        Assert.Contains("<td>@Model.StepCounts.GetValueOrDefault(campaign.Id)</td>", index);
        Assert.Contains("<td>@Model.ActiveEnrollmentCounts.GetValueOrDefault(campaign.Id)</td>", index);
        var details = Read(@"src\IPRO.Web\Views\Campaigns\Details.cshtml");
        Assert.DoesNotContain("Model.Clients", details);
        Assert.Contains("data-options-url=\"/portal/Campaigns/ClientOptions/@Model.Campaign.Id\"", details);
        Assert.Contains("<script nonce=\"@Context.GetCspNonce()\">", details);

        var repair = Read(@"src\IPRO.DataAccess\StartupSchemaRepair.cs");
        foreach (var (table, column) in new[] { ("DripCampaignStepSends", "DripCampaignStepId"), ("ClientLifeEvents", "ClientId"), ("PortalDocuments", "ClientId") })
            Assert.Contains($"\"ALTER TABLE `{table}` ADD INDEX `IX_{table}_{column}` (`{column}`)\"", repair);
    }

    // ---- harness ------------------------------------------------------------------------------

    private sealed record Option(int Id, string Label);

    private static async Task<List<Option>> OptionsAsync(CampaignsController controller, int campaignId, string? q)
    {
        var json = JsonSerializer.Serialize(Assert.IsType<JsonResult>(await controller.ClientOptions(campaignId, q)).Value);
        return JsonSerializer.Deserialize<List<Option>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static CampaignsController Controller(IPRODbContext db, int agentId)
    {
        var controller = new CampaignsController(db, new GrantAll(), new NoConsent(), new RecordingJobClient());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NoTempData());
        return controller;
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string tag)
    {
        var rule = new BillingRule { PackageName = $"C652-{Guid.NewGuid():N}"[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"c652{tag}-{Guid.NewGuid():N}"[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Morgan", LastName = "Reid",
            DomainName = $"c652-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> SeedCampaignAsync(IPRODbContext db, int agentId, string name, int steps)
    {
        var campaign = new DripCampaign { AgentUserId = agentId, Name = name, IsActive = true };
        db.Add(campaign);
        await db.SaveChangesAsync();
        for (var i = 0; i < steps; i++)
            db.Add(new DripCampaignStep { DripCampaignId = campaign.Id, SortOrder = i, Subject = $"S{i + 1}", HtmlBody = "<p>x</p>", DelayDays = i });
        await db.SaveChangesAsync();
        return campaign.Id;
    }

    private static async Task<int> SeedClientAsync(IPRODbContext db, int agentId, string first, string last, string email)
    {
        var client = new Client { AgentUserId = agentId, FirstName = first, LastName = last, Email = email, IsNewsletterSubscribed = true };
        db.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static DripCampaignEnrollment Enrollment(int agentId, int campaignId, int clientId, DripCampaignEnrollmentStatus status) => new()
    {
        AgentUserId = agentId,
        DripCampaignId = campaignId,
        ClientId = clientId,
        Status = status,
        NextStepIndex = 0,
        StartedAt = DateTime.UtcNow,
        NextSendAt = DateTime.UtcNow.AddDays(1),
        UnsubscribeToken = Guid.NewGuid().ToString("N")
    };

    private static DripCampaignStepSend Send(int enrollmentId, int stepId, DateTime? sent, DateTime? delivered = null, DateTime? opened = null, DateTime? clicked = null) => new()
    {
        DripCampaignEnrollmentId = enrollmentId,
        DripCampaignStepId = stepId,
        StepIndex = 0,
        Email = "x@example.test",
        RecipientName = "X",
        Status = sent == null && opened == null ? NewsLetterRecipientStatus.Queued : NewsLetterRecipientStatus.Sent,
        SentAt = sent,
        DeliveredAt = delivered,
        OpenedAt = opened,
        ClickedAt = clicked
    };

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }

    private sealed class GrantAll : IPackageEntitlementService
    {
        public Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) =>
            Task.FromResult(agentIds.Distinct().ToDictionary(id => id, _ => true));
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class NoConsent : IPRO.Business.Services.IEmailConsentService
    {
        public bool IsSuppressed(Client client, IPRO.Business.Services.EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<IPRO.Business.Services.SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public bool LiftBounceSuppression(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
