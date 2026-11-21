using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 641 (2026-11-20): the eight lower-severity audit candidates of 2026-08-30 were dropped unchecked and never written
// down, so a fresh short sweep of email, jobs, guards and money was run instead. What it found, and the fixes.
public class SweepFixes641Tests
{
    // ---- 1. a suspended or lapsed adviser's scheduled mail is held ---------------------------------------------

    [Fact]
    public async Task Only_advisers_the_portal_lets_in_may_send_scheduled_mail()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var paying = await AdviserAsync(db, active: true, BillingStatus.Active, paidThrough: null, trialEnds: null);
        var suspended = await AdviserAsync(db, active: false, BillingStatus.Active, paidThrough: null, trialEnds: null);
        var cancelledPaidUp = await AdviserAsync(db, active: true, BillingStatus.Cancelled, paidThrough: now.AddDays(10), trialEnds: null);
        var cancelledOver = await AdviserAsync(db, active: true, BillingStatus.Cancelled, paidThrough: now.AddDays(-1), trialEnds: null);
        var trialOn = await AdviserAsync(db, active: true, billing: null, paidThrough: null, trialEnds: now.AddDays(5));
        var trialInGrace = await AdviserAsync(db, active: true, billing: null, paidThrough: null, trialEnds: now.AddHours(-12));
        var trialLapsed = await AdviserAsync(db, active: true, billing: null, paidThrough: null, trialEnds: now.AddDays(-10));
        var neverPaid = await AdviserAsync(db, active: true, billing: null, paidThrough: null, trialEnds: null);

        var may = (await (await SendingAdvisers.WhoMaySendAsync(db, now)).ToListAsync()).ToHashSet();
        Assert.True(may.SetEquals(new[] { paying, cancelledPaidUp, trialOn, trialInGrace }.Intersect(may).Concat(new[] { paying, cancelledPaidUp, trialOn, trialInGrace }).Distinct()));
        foreach (var id in new[] { paying, cancelledPaidUp, trialOn, trialInGrace }) Assert.Contains(id, may);
        foreach (var id in new[] { suspended, cancelledOver, trialLapsed, neverPaid }) Assert.DoesNotContain(id, may);

        // The same answer as the portal's own lock, for each of them.
        var entitlements = new PackageEntitlementService(new IPRO.DataAccess.Repositories.UnitOfWork(db), db);
        foreach (var id in new[] { paying, cancelledPaidUp, cancelledOver, trialOn, trialInGrace, trialLapsed, neverPaid })
            Assert.Equal(!await entitlements.IsAccessGatedAsync(id), may.Contains(id));
    }

    [Fact]
    public void Every_scheduled_sender_filters_by_it_inside_its_own_query()
    {
        foreach (var (job, predicate) in new[]
                 {
                     ("NewsLetterDispatchJob", ".Where(s => maySend.Contains(s.AgentUserId))"),
                     ("ECardDispatchJob", ".Where(c => maySend.Contains(c.AgentUserId))"),
                     ("ELetterDispatchJob", ".Where(l => maySend.Contains(l.AgentUserId))"),
                     ("PollDispatchJob", ".Where(s => maySend.Contains(s.AgentUserId))"),
                     ("DripCampaignJob", ".Where(e => maySend.Contains(e.AgentUserId))"),
                     ("DidYouKnowEmailDispatchJob", "_db.Articles.Any(a => a.Id == q.ArticleId && maySend.Contains(a.AgentUserId))"),
                 })
        {
            var source = Read($@"src\IPRO.Scheduler\{job}.cs");
            Assert.Contains("var maySend = await SendingAdvisers.WhoMaySendAsync(_db, now);", source);
            Assert.Contains(predicate, source);
        }
    }

    // ---- 2. a recurring invoice is made on the adviser's own date ----------------------------------------------

    [Fact]
    public async Task A_recurring_invoice_waits_for_the_advisers_own_date()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        const string toronto = "(GMT-05:00) Eastern Time (US & Canada)";
        var agentId = await AdviserAsync(db, active: true, BillingStatus.Active, paidThrough: null, trialEnds: null, timeZone: toronto);
        var client = new Client { AgentUserId = agentId, FirstName = "Rec", LastName = "Urring", Email = $"r-{Guid.NewGuid():N}"[..12] + "@example.test", Province = "Ontario" };
        db.Add(client);
        await db.SaveChangesAsync();
        var localToday = AgentLocalTime.FromUtc(DateTime.UtcNow, toronto).Date;
        RecurringInvoiceSchedule Schedule(DateTime runDate, string what) => new()
        {
            AgentUserId = agentId, ClientId = client.Id, Frequency = RecurringInvoiceFrequency.Monthly, NextRunDate = runDate, DueInDays = 15, IsActive = true,
            Notes = what, LineItems = { new RecurringInvoiceLineItem { Description = what, Quantity = 1, UnitPrice = 100m, IsTaxable = false, SortOrder = 1 } }
        };
        db.AddRange(Schedule(localToday, "today"), Schedule(localToday.AddDays(1), "tomorrow"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new ClientInvoiceService(new IPRO.DataAccess.Repositories.UnitOfWork(db));
        await new IPRO.Scheduler.RecurringClientInvoiceJob(db, service, NullLogger<IPRO.Scheduler.RecurringClientInvoiceJob>.Instance).RunAsync();

        await using var fresh = testDb.CreateContext();
        var invoice = Assert.Single(await fresh.ClientInvoices.Where(i => i.AgentUserId == agentId).ToListAsync());
        Assert.Equal("today", invoice.Notes);
        Assert.Equal(localToday, invoice.IssueDate.Date);
        Assert.Contains("\"recurring-client-invoices\", job => job.RunAsync(), Cron.Hourly);", Read(@"src\IPRO.Web\Program.cs"));
    }

    // ---- 3 to 7: pinned where they live ------------------------------------------------------------------------

    [Fact]
    public void The_smaller_fixes_stay_in_place()
    {
        // 3. invoice reminders read every open invoice, 500 at a time, not only the 500 due earliest.
        var reminders = Read(@"src\IPRO.Scheduler\OverdueInvoiceReminderJob.cs");
        Assert.Contains("&& i.Id > lastId)", reminders);
        Assert.Contains(".OrderBy(i => i.Id)", reminders);
        Assert.DoesNotContain(".OrderBy(i => i.DueDate)", reminders);

        // 4. an adviser who ever subscribed gets no trial notices; a notice counts only once it was sent.
        var trial = Read(@"src\IPRO.Scheduler\TrialReminderJob.cs");
        Assert.Contains("b.Status == BillingStatus.Active || b.Status == BillingStatus.Cancelled || b.Status == BillingStatus.Expired", trial);
        Assert.Contains("if (!sent)", trial);

        // 5. a capped refund's tax goes through RoundTax (half away from zero), as every other tax line does.
        Assert.Contains("var cappedTax = RoundTax(cappedNet, taxRate);", Read(@"src\IPRO.Billing\PayPalBillingService.cs"));
        Assert.Equal(8.99m, IPRO.Billing.PayPalBillingService.RoundTax(60.00m, 0.14975m));

        // 7. a scheduled e-card or e-letter goes to the client's address now, not the one copied when it was written.
        foreach (var dispatcher in new[] { "ECardDispatcher", "ELetterDispatcher" })
            Assert.Contains("recipient.Email = client.Email.Trim();", Read($@"src\IPRO.Email\{dispatcher}.cs"));
    }

    // ---- harness -----------------------------------------------------------------------------------------------

    private static async Task<int> AdviserAsync(IPRODbContext db, bool active, BillingStatus? billing, DateTime? paidThrough, DateTime? trialEnds, string? timeZone = null)
    {
        var rule = new BillingRule { PackageName = $"S641-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"s641-{Guid.NewGuid():N}"[..20], Email = $"s641-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Sweep", LastName = "Check", DomainName = $"s641-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = rule.Id, TrialEndsAt = trialEnds, IsActive = active,
            TimeZone = timeZone ?? string.Empty
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        if (billing is { } status)
        {
            db.Add(new IPRO.Entities.Billing
            {
                AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 60m, Status = status, Period = BillingPeriod.Monthly,
                StartDate = DateTime.UtcNow.AddDays(-40), NextBillingDate = DateTime.UtcNow.AddDays(20), PaidThroughAt = paidThrough
            });
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        return agent.Id;
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
