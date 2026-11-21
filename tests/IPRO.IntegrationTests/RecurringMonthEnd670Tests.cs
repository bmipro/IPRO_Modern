using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Scheduler;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 670 (2026-11-21): a recurring invoice schedule on the 29th, 30th or 31st drifted for good after its first short
// month. The job moved NextRunDate on with AddMonths from the last run, and .NET clamps to the month's end, so
// Jan 31 became Feb 28, then Mar 28, Apr 28... The schedule now keeps the day the adviser chose (AnchorDay) and
// each next run is counted from it, clamped within that one month only.
public class RecurringMonthEnd670Tests
{
    private static DateTime D(int year, int month, int day) => new(year, month, day);

    // The run dates a schedule makes from its first date on, as the job counts them.
    private static DateTime[] Runs(DateTime first, RecurringInvoiceFrequency frequency, int count)
    {
        var runs = new List<DateTime> { first };
        while (runs.Count < count) runs.Add(RecurringClientInvoiceJob.NextRunAfter(runs[^1], frequency, first.Day));
        return runs.ToArray();
    }

    // ---- the arithmetic ---------------------------------------------------------------------------------------

    [Fact]
    public void Monthly_on_the_31st_takes_the_short_month_and_goes_back_to_the_31st()
    {
        Assert.Equal(new[] { D(2027, 1, 31), D(2027, 2, 28), D(2027, 3, 31), D(2027, 4, 30), D(2027, 5, 31) },
            Runs(D(2027, 1, 31), RecurringInvoiceFrequency.Monthly, 5));
        // A leap year's February has the 29th.
        Assert.Equal(new[] { D(2028, 1, 31), D(2028, 2, 29), D(2028, 3, 31) }, Runs(D(2028, 1, 31), RecurringInvoiceFrequency.Monthly, 3));
        // The 29th and the 30th the same way; December rolls into the next year.
        Assert.Equal(new[] { D(2027, 1, 30), D(2027, 2, 28), D(2027, 3, 30) }, Runs(D(2027, 1, 30), RecurringInvoiceFrequency.Monthly, 3));
        Assert.Equal(new[] { D(2027, 1, 29), D(2027, 2, 28), D(2027, 3, 29) }, Runs(D(2027, 1, 29), RecurringInvoiceFrequency.Monthly, 3));
        Assert.Equal(new[] { D(2026, 12, 31), D(2027, 1, 31), D(2027, 2, 28) }, Runs(D(2026, 12, 31), RecurringInvoiceFrequency.Monthly, 3));
        // A day every month has is not touched.
        Assert.Equal(new[] { D(2026, 11, 15), D(2026, 12, 15), D(2027, 1, 15) }, Runs(D(2026, 11, 15), RecurringInvoiceFrequency.Monthly, 3));
    }

    [Fact]
    public void Quarterly_from_November_30th_takes_February_and_goes_back_to_the_30th()
    {
        Assert.Equal(new[] { D(2026, 11, 30), D(2027, 2, 28), D(2027, 5, 30), D(2027, 8, 30), D(2027, 11, 30) },
            Runs(D(2026, 11, 30), RecurringInvoiceFrequency.Quarterly, 5));
        Assert.Equal(new[] { D(2027, 11, 30), D(2028, 2, 29), D(2028, 5, 30) }, Runs(D(2027, 11, 30), RecurringInvoiceFrequency.Quarterly, 3));
    }

    [Fact]
    public void Annually_on_February_29th_runs_on_the_28th_until_the_next_leap_year()
    {
        Assert.Equal(new[] { D(2028, 2, 29), D(2029, 2, 28), D(2030, 2, 28), D(2031, 2, 28), D(2032, 2, 29) },
            Runs(D(2028, 2, 29), RecurringInvoiceFrequency.Annually, 5));
    }

    [Fact]
    public void A_time_on_the_run_date_and_its_kind_are_kept()
    {
        var next = RecurringClientInvoiceJob.NextRunAfter(new DateTime(2027, 1, 31, 9, 30, 0, DateTimeKind.Utc), RecurringInvoiceFrequency.Monthly, 31);
        Assert.Equal(new DateTime(2027, 2, 28, 9, 30, 0, DateTimeKind.Utc), next);
        Assert.Equal(DateTimeKind.Utc, next.Kind);
    }

    // ---- the job ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_job_keeps_the_31st_and_gives_an_older_schedule_its_day()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AdviserAsync(db);
        var client = new Client { AgentUserId = agentId, FirstName = "Month", LastName = "End", Email = $"m-{Guid.NewGuid():N}"[..12] + "@example.test", Province = "Ontario" };
        db.Add(client);
        await db.SaveChangesAsync();
        RecurringInvoiceSchedule Schedule(DateTime runDate, int? anchorDay, string what) => new()
        {
            AgentUserId = agentId, ClientId = client.Id, Frequency = RecurringInvoiceFrequency.Monthly, NextRunDate = runDate, AnchorDay = anchorDay,
            DueInDays = 15, IsActive = true, Notes = what,
            LineItems = { new RecurringInvoiceLineItem { Description = what, Quantity = 1, UnitPrice = 100m, IsTaxable = false, SortOrder = 1 } }
        };
        // Past dates, so each is due on any adviser's calendar and every run of the job moves each one on once.
        var kept = Schedule(D(2026, 1, 31), 31, "kept");
        var older = Schedule(D(2026, 1, 30), null, "older");            // saved before 670
        var drifted = Schedule(D(2026, 3, 28), null, "drifted");        // moved to the 28th by the old arithmetic
        db.AddRange(kept, older, drifted);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var expected = new Dictionary<int, (int?, DateTime)[]>
        {
            [kept.Id] = new (int?, DateTime)[] { (31, D(2026, 2, 28)), (31, D(2026, 3, 31)), (31, D(2026, 4, 30)) },
            [older.Id] = new (int?, DateTime)[] { (30, D(2026, 2, 28)), (30, D(2026, 3, 30)), (30, D(2026, 4, 30)) },
            // Its first day was never recorded, so it stays on the 28th: 670 stops the drift, it cannot undo it.
            [drifted.Id] = new (int?, DateTime)[] { (28, D(2026, 4, 28)), (28, D(2026, 5, 28)), (28, D(2026, 6, 28)) },
        };
        for (var run = 0; run < 3; run++)
        {
            await using var jobDb = testDb.CreateContext();
            var service = new ClientInvoiceService(new IPRO.DataAccess.Repositories.UnitOfWork(jobDb));
            await new RecurringClientInvoiceJob(jobDb, service, NullLogger<RecurringClientInvoiceJob>.Instance).RunAsync();

            await using var fresh = testDb.CreateContext();
            foreach (var (id, steps) in expected)
            {
                var schedule = await fresh.RecurringInvoiceSchedules.AsNoTracking().SingleAsync(s => s.Id == id);
                Assert.Equal(steps[run], (schedule.AnchorDay, schedule.NextRunDate));
            }
        }

        await using var check = testDb.CreateContext();
        Assert.Equal(9, await check.ClientInvoices.CountAsync(i => i.AgentUserId == agentId));
    }

    // ---- the adviser's own date -------------------------------------------------------------------------------

    [Fact]
    public async Task The_date_the_adviser_sets_is_the_day_kept_and_saving_unchanged_keeps_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AdviserAsync(db);
        var client = new Client { AgentUserId = agentId, FirstName = "Ava", LastName = "Chen", Email = $"a-{Guid.NewGuid():N}"[..12] + "@example.test", Province = "Ontario" };
        db.Add(client);
        await db.SaveChangesAsync();
        var controller = RecurringController(db, agentId);
        RecurringInvoiceEditViewModel Form(DateTime runDate) => new()
        {
            ClientId = client.Id, Frequency = RecurringInvoiceFrequency.Monthly, NextRunDate = runDate, DueInDays = 15,
            LineItems = { new ClientInvoiceLineItemInputModel { Description = "Bookkeeping", Quantity = 1, UnitPrice = 250m, IsTaxable = false } }
        };

        Assert.IsType<RedirectToActionResult>(await controller.Create(Form(D(2027, 1, 31))));
        var schedule = await db.RecurringInvoiceSchedules.AsNoTracking().SingleAsync(s => s.AgentUserId == agentId);
        Assert.Equal(((int?)31, D(2027, 1, 31)), (schedule.AnchorDay, schedule.NextRunDate));

        // The job has run once: the schedule is on Feb 28 and still kept for the 31st. The adviser opens it and
        // saves without changing the date; it must not settle on the 28th.
        await db.RecurringInvoiceSchedules.Where(s => s.Id == schedule.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.NextRunDate, D(2027, 2, 28)));
        db.ChangeTracker.Clear();
        Assert.IsType<RedirectToActionResult>(await controller.Edit(schedule.Id, Form(D(2027, 2, 28))));
        Assert.Equal((int?)31, (await db.RecurringInvoiceSchedules.AsNoTracking().SingleAsync(s => s.Id == schedule.Id)).AnchorDay);

        // A new date is a new day of the month.
        db.ChangeTracker.Clear();
        Assert.IsType<RedirectToActionResult>(await controller.Edit(schedule.Id, Form(D(2027, 3, 15))));
        var moved = await db.RecurringInvoiceSchedules.AsNoTracking().SingleAsync(s => s.Id == schedule.Id);
        Assert.Equal(((int?)15, D(2027, 3, 15)), (moved.AnchorDay, moved.NextRunDate));

        // A schedule from before 670 that is edited gets the day it is saved on.
        await db.RecurringInvoiceSchedules.Where(s => s.Id == schedule.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.AnchorDay, (int?)null));
        db.ChangeTracker.Clear();
        Assert.IsType<RedirectToActionResult>(await controller.Edit(schedule.Id, Form(D(2027, 3, 15))));
        Assert.Equal((int?)15, (await db.RecurringInvoiceSchedules.AsNoTracking().SingleAsync(s => s.Id == schedule.Id)).AnchorDay);
    }

    // ---- production's database --------------------------------------------------------------------------------

    [Fact]
    public async Task A_database_from_before_gains_the_column_empty_on_the_schedules_already_there()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AdviserAsync(db);
        var client = new Client { AgentUserId = agentId, FirstName = "Old", LastName = "Row", Email = $"o-{Guid.NewGuid():N}"[..12] + "@example.test", Province = "Ontario" };
        db.Add(client);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE `RecurringInvoiceSchedules` DROP COLUMN `AnchorDay`");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO `RecurringInvoiceSchedules` (`AgentUserId`, `ClientId`, `Frequency`, `NextRunDate`, `DueInDays`, `IsActive`, `Notes`, `DiscountType`, `DiscountValue`, `CreatedAt`, `UpdatedAt`) " +
            "VALUES ({0}, {1}, 0, '2026-12-31', 15, 1, '', 0, 0, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))", agentId, client.Id);

        await StartupSchemaRepair.EnsureClientInvoiceSchemaAsync(db);
        await StartupSchemaRepair.EnsureClientInvoiceSchemaAsync(db);           // and again: nothing to do, nothing thrown

        await using var fresh = testDb.CreateContext();
        var schedule = await fresh.RecurringInvoiceSchedules.AsNoTracking().SingleAsync(s => s.AgentUserId == agentId);
        Assert.Null(schedule.AnchorDay);
        Assert.Equal(D(2026, 12, 31), schedule.NextRunDate);
    }

    [Fact]
    public void The_job_counts_from_the_kept_day_and_both_apps_add_the_column()
    {
        var job = Read(@"src\IPRO.Scheduler\RecurringClientInvoiceJob.cs");
        Assert.Contains("var anchorDay = schedule.AnchorDay ??= schedule.NextRunDate.Day;", job);
        Assert.Contains("schedule.NextRunDate = NextRunAfter(schedule.NextRunDate, schedule.Frequency, anchorDay);", job);
        Assert.DoesNotContain("schedule.NextRunDate.AddMonths(", job);
        Assert.DoesNotContain("schedule.NextRunDate.AddYears(", job);
        Assert.Contains("\"AnchorDay\", \"ALTER TABLE `RecurringInvoiceSchedules` ADD COLUMN `AnchorDay` int NULL\"", Read(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        foreach (var app in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
            Assert.Contains("StartupSchemaRepair.EnsureClientInvoiceSchemaAsync(db)", Read(app));
        Assert.Contains("runs on the last day of a shorter month and goes back to its own day", Read(@"DOCS\10_CLIENT_INVOICING.md"));
    }

    // ---- harness -----------------------------------------------------------------------------------------------

    // An adviser who is paying, as SweepFixes641Tests makes one.
    private static async Task<int> AdviserAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"R670-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"r670-{Guid.NewGuid():N}"[..20], Email = $"r670-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Month", LastName = "End", DomainName = $"r670-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = rule.Id, IsActive = true, TimeZone = string.Empty
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-40), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return agent.Id;
    }

    private static IPRO.Web.Controllers.RecurringInvoicesController RecurringController(IPRODbContext db, int agentId)
    {
        var controller = new IPRO.Web.Controllers.RecurringInvoicesController(db, new GrantAll());
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test")) };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
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

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
