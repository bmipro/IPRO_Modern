using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 599 (the owner's decision of 2026-10-24): SuperAdmin's Deactivate was "block sign-in" only -- PayPal
// kept charging and the website stayed up. It is now a suspension: billing cancelled first (refused, with
// nothing changed, if PayPal won't cancel), sign-in blocked, site offline (PublicSiteGatingTests), every row
// kept. A year on, iPro's mailbox is emailed once to keep the data another year or delete the account;
// Activate clears the suspension.
public class Suspension599Tests
{
    [Fact]
    public async Task Suspending_an_adviser_with_no_subscription_blocks_them_and_starts_the_years_clock()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, activeBilling: false);
        var billing = Billing(cancelResult: true);

        var controller = NewController(db, billing);
        await controller.Deactivate(agentId);

        db.ChangeTracker.Clear();
        var agent = await db.AgentUsers.SingleAsync(a => a.Id == agentId);
        Assert.False(agent.IsActive);
        Assert.NotNull(agent.SuspendedAt);
        Assert.InRange(agent.DataReviewDueAt!.Value, DateTime.UtcNow.AddYears(1).AddMinutes(-5), DateTime.UtcNow.AddYears(1).AddMinutes(5));
        Assert.Null(agent.DataReviewNoticeSentAt);
        Assert.Empty(Calls(billing));   // nothing to cancel
        Assert.Contains("no active PayPal subscription", controller.TempData["Warning"] as string);
    }

    [Fact]
    public async Task Suspending_an_adviser_with_an_active_subscription_cancels_it_first()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, activeBilling: true);
        var billing = Billing(cancelResult: true);

        var controller = NewController(db, billing);
        await controller.Deactivate(agentId);

        Assert.Equal(new[] { agentId }, Calls(billing));
        db.ChangeTracker.Clear();
        var agent = await db.AgentUsers.SingleAsync(a => a.Id == agentId);
        Assert.False(agent.IsActive);
        Assert.NotNull(agent.SuspendedAt);
        Assert.Contains("PayPal billing cancelled", controller.TempData["Warning"] as string);
    }

    [Theory]
    [InlineData(false, "No subscription to cancel")]
    [InlineData(true, "PayPal billing cancelled")]
    public async Task The_suspended_note_says_what_happened_to_billing_607(bool activeBilling, string expected)
    {
        // 607 (the owner's 599 test, 2026-10-26): the note said "Billing cancelled" for an account with no
        // subscription. It now reads the suspension's own log entry; this runs the real Deactivate for it.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, activeBilling);
        await NewController(db, Billing(cancelResult: true)).Deactivate(agentId);

        var entry = await db.OperateLogs.AsNoTracking()
            .Where(l => l.AgentUserId == agentId && l.Module == "Agents" && l.Action == "Deactivate")
            .Select(l => l.Description).SingleAsync();
        Assert.Equal(expected, IPRO.Admin.Controllers.AgentsController.SuspensionBillingNote(entry));
        Assert.Null(IPRO.Admin.Controllers.AgentsController.SuspensionBillingNote(null));
    }

    [Fact]
    public void The_suspended_note_sits_under_the_buttons_not_inside_their_row_607()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "IPRO.sln"))) dir = System.IO.Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var view = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!, @"src\IPRO.Admin\Views\Agents\Details.cshtml"));

        // The buttons' row ends with Delete; the note comes after it, on its own line.
        var deleteButton = view.IndexOf("<i class=\"fas fa-trash me-1\"></i>Delete</button>", StringComparison.Ordinal);
        var note = view.IndexOf("Suspended since", StringComparison.Ordinal);
        Assert.True(deleteButton > 0 && note > deleteButton, "the suspended note is back inside the button row");
        Assert.DoesNotContain("alert alert-warning w-100 mb-2", view);
        Assert.DoesNotContain("Billing cancelled, website offline", view);
        Assert.Contains("ViewBag.SuspensionBilling as string", view);
    }

    [Fact]
    public async Task If_PayPal_will_not_cancel_nothing_changes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, activeBilling: true);
        var billing = Billing(cancelResult: false);

        var controller = NewController(db, billing);
        await controller.Deactivate(agentId);

        db.ChangeTracker.Clear();
        var agent = await db.AgentUsers.SingleAsync(a => a.Id == agentId);
        Assert.True(agent.IsActive);
        Assert.Null(agent.SuspendedAt);
        Assert.Contains("NOT suspended", controller.TempData["Error"] as string);
    }

    [Fact]
    public async Task Activate_ends_the_suspension_and_keep_another_year_moves_the_review()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, activeBilling: false);
        var controller = NewController(db, Billing(cancelResult: true));
        await controller.Deactivate(agentId);

        // Keep another year: the review moves a year from now and the reminder re-arms.
        var agent = await db.AgentUsers.SingleAsync(a => a.Id == agentId);
        agent.DataReviewDueAt = DateTime.UtcNow.AddDays(-1);
        agent.DataReviewNoticeSentAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        controller = NewController(db, Billing(cancelResult: true));
        await controller.KeepSuspendedData(agentId);
        db.ChangeTracker.Clear();
        agent = await db.AgentUsers.SingleAsync(a => a.Id == agentId);
        Assert.True(agent.DataReviewDueAt > DateTime.UtcNow.AddDays(360));
        Assert.Null(agent.DataReviewNoticeSentAt);

        controller = NewController(db, Billing(cancelResult: true));
        await controller.Activate(agentId);
        db.ChangeTracker.Clear();
        agent = await db.AgentUsers.SingleAsync(a => a.Id == agentId);
        Assert.True(agent.IsActive);
        Assert.Null(agent.SuspendedAt);
        Assert.Null(agent.DataReviewDueAt);
        Assert.Contains("subscribe again", controller.TempData["Success"] as string);
    }

    [Fact]
    public async Task The_review_job_emails_once_when_a_year_has_passed_and_never_deletes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var due = await SeedAgentAsync(db, activeBilling: false, company: "Due Co");
        var notYet = await SeedAgentAsync(db, activeBilling: false, company: "Not Yet Co");
        var active = await SeedAgentAsync(db, activeBilling: false, company: "Active Co");
        foreach (var (id, suspendedDaysAgo, isActive) in new[] { (due, 400, false), (notYet, 100, false), (active, 400, true) })
        {
            var a = await db.AgentUsers.SingleAsync(x => x.Id == id);
            a.IsActive = isActive;
            a.SuspendedAt = DateTime.UtcNow.AddDays(-suspendedDaysAgo);
            a.DataReviewDueAt = a.SuspendedAt.Value.AddYears(1);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var email = new PortalBatch598Tests.RecordingEmail();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Signups:NotificationEmail"] = "owner@example.test",
            ["Signups:AdminBaseUrl"] = "https://admin.example.test"
        }).Build();
        var job = new IPRO.Scheduler.SuspendedAccountReviewJob(db, email, config, NullLogger<IPRO.Scheduler.SuspendedAccountReviewJob>.Instance);

        Assert.Equal(1, await job.RunAsync());
        var sent = Assert.Single(email.Sent);
        Assert.Equal("owner@example.test", sent.To);
        Assert.Equal("Suspended account review: Due Co", sent.Subject);
        Assert.Contains($"https://admin.example.test/Agents/Details/{due}", sent.Body);
        Assert.Contains("keeps their invoices", sent.Body);

        Assert.Equal(0, await job.RunAsync());   // once, not every day
        Assert.Equal(3, await db.AgentUsers.CountAsync(a => a.Id == due || a.Id == notYet || a.Id == active));   // nothing deleted
    }

    // ---------------------------------------------------------------- harness

    private static async Task<int> SeedAgentAsync(IPRODbContext db, bool activeBilling, string company = "Suspend Co")
    {
        var rule = new BillingRule { PackageName = ($"T599-{Guid.NewGuid():N}")[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t599-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Sue", LastName = "Spend", CompanyName = company,
            DomainName = ($"t599-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        if (activeBilling)
        {
            db.Add(new IPRO.Entities.Billing
            {
                AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active,
                Period = BillingPeriod.Monthly, StartDate = DateTime.UtcNow.AddDays(-10), NextBillingDate = DateTime.UtcNow.AddDays(20)
            });
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        return agent.Id;
    }

    private static IPRO.Admin.Controllers.AgentsController NewController(IPRODbContext db, IPRO.Billing.IBillingService billing)
    {
        var uow = new UnitOfWork(db);
        var controller = new IPRO.Admin.Controllers.AgentsController(
            new IPRO.Business.Services.AgentService(uow, new PasswordHasher<AgentUser>()),
            new IPRO.Business.Services.WebsiteService(uow),
            uow,
            billing,
            new PasswordHasher<AgentUser>(),
            NullLogger<IPRO.Admin.Controllers.AgentsController>.Instance,
            new NullAudit(),
            db,
            new ServiceCollection().BuildServiceProvider());
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NullTempData());
        return controller;
    }

    // IBillingService has 22 members; only CancelSubscriptionAsync is in play here.
    private static IPRO.Billing.IBillingService Billing(bool cancelResult)
    {
        var proxy = DispatchProxy.Create<IPRO.Billing.IBillingService, BillingStub>();
        ((BillingStub)(object)proxy).CancelResult = cancelResult;
        return proxy;
    }

    private static List<int> Calls(IPRO.Billing.IBillingService billing) => ((BillingStub)(object)billing).Cancelled;

    public class BillingStub : DispatchProxy
    {
        public bool CancelResult { get; set; }
        public List<int> Cancelled { get; } = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IPRO.Billing.IBillingService.CancelSubscriptionAsync))
            {
                Cancelled.Add((int)args![0]!);
                return Task.FromResult(CancelResult);
            }
            throw new NotSupportedException($"{method.Name} is not used by these tests");
        }
    }

    private sealed class NullAudit : IAdminAuditLogService
    {
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details) => Task.CompletedTask;
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
