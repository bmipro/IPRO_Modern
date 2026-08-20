using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// DOCS/22 — cancelled-but-paid-through + the annual discount clawback. The pure math matrix pins
// the policy; the integration half proves the REAL CancelSubscriptionAsync writes it into the
// database and that gating honors it.
public class PrepaidValueTests
{
    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------------ the pure matrix --

    // Gold: $600/yr paid, $60/mo list, 13% HST. The owner's own worked example is month 5.
    [Theory]
    [InlineData(15, 1, 540.00, 70.20)]    // Jan 15 -> month 1
    [InlineData(135, 5, 300.00, 39.00)]   // mid-May -> month 5: THE example ($339 gross)
    [InlineData(255, 9, 60.00, 7.80)]     // mid-Sep -> month 9: last refundable month
    public void Annual_cancel_before_the_crossover_refunds_the_clawback_remainder(
        int daysIn, int expectedMonthsUsed, decimal expectedNet, decimal expectedTax)
    {
        var o = PrepaidValue.AnnualCancel(600m, 60m, 0.13m, Jan1, Jan1.AddDays(daysIn));
        Assert.Equal(PrepaidValue.CancelKind.RefundAndEndOfMonth, o.Kind);
        Assert.Equal(expectedMonthsUsed, o.MonthsUsed);
        Assert.Equal(expectedNet, o.RefundNet);
        Assert.Equal(expectedTax, o.RefundTax);
        Assert.Equal(expectedNet + expectedTax, o.RefundGross);
        // Access to the end of the month whose monthly rate they just "paid" via clawback, + grace.
        Assert.Equal(Jan1.AddMonths(expectedMonthsUsed).AddDays(PrepaidValue.CancellationGraceDays), o.PaidThroughAt);
    }

    [Theory]
    [InlineData(285)]  // mid-Oct -> month 10: the crossover, refund exactly 0
    [InlineData(320)]  // month 11: would be negative, never charged
    [InlineData(354)]  // month 12
    public void Annual_cancel_from_the_crossover_on_defers_to_the_anniversary(int daysIn)
    {
        var o = PrepaidValue.AnnualCancel(600m, 60m, 0.13m, Jan1, Jan1.AddDays(daysIn));
        Assert.Equal(PrepaidValue.CancelKind.AccessUntilPaidThrough, o.Kind);
        Assert.Equal(0m, o.RefundGross);
        Assert.Equal(Jan1.AddYears(1).AddDays(PrepaidValue.CancellationGraceDays), o.PaidThroughAt);
    }

    [Fact]
    public void The_crossover_is_month_10_on_every_package()
    {
        // Silver 400/40, Gold 600/60, Platinum 900/90 — all "two months free".
        foreach (var (annual, monthly) in new[] { (400m, 40m), (600m, 60m), (900m, 90m) })
        {
            Assert.Equal(PrepaidValue.CancelKind.RefundAndEndOfMonth,
                PrepaidValue.AnnualCancel(annual, monthly, 0.13m, Jan1, Jan1.AddDays(255)).Kind);  // month 9
            Assert.Equal(PrepaidValue.CancelKind.AccessUntilPaidThrough,
                PrepaidValue.AnnualCancel(annual, monthly, 0.13m, Jan1, Jan1.AddDays(285)).Kind);  // month 10
        }
    }

    [Fact]
    public void A_started_month_counts_as_used_and_day_one_uses_month_one()
    {
        Assert.Equal(1, PrepaidValue.MonthsUsedRoundingUp(Jan1, Jan1));                    // day one
        Assert.Equal(4, PrepaidValue.MonthsUsedRoundingUp(Jan1, Jan1.AddMonths(4)));       // exact boundary
        Assert.Equal(5, PrepaidValue.MonthsUsedRoundingUp(Jan1, Jan1.AddMonths(4).AddDays(1)));
        Assert.Equal(12, PrepaidValue.MonthsUsedRoundingUp(Jan1, Jan1.AddYears(3)));       // capped
    }

    [Fact]
    public void Monthly_cancel_keeps_access_to_the_billing_anniversary_plus_grace()
    {
        var next = Jan1.AddMonths(5);
        var o = PrepaidValue.MonthlyCancel(next, Jan1.AddMonths(4).AddDays(10));
        Assert.Equal(PrepaidValue.CancelKind.AccessUntilPaidThrough, o.Kind);
        Assert.Equal(0m, o.RefundGross);
        Assert.Equal(next.AddDays(PrepaidValue.CancellationGraceDays), o.PaidThroughAt);
        // Data-gap fallback: no NextBillingDate still honors the running month.
        var fallback = PrepaidValue.MonthlyCancel(null, Jan1);
        Assert.Equal(Jan1.AddMonths(1).AddDays(PrepaidValue.CancellationGraceDays), fallback.PaidThroughAt);
    }

    [Fact]
    public void Credit_days_round_in_the_agents_favour()
    {
        // $200 at Silver $40/mo -> daily 480/365 -> 152.08 -> 153 days (about the owner's 5 months).
        Assert.Equal(153, PrepaidValue.CreditDays(200m, 40m));
        Assert.Equal(0, PrepaidValue.CreditDays(0m, 40m));
        Assert.Equal(0, PrepaidValue.CreditDays(100m, 0m));
    }

    // ------------------------------------------------------- the real service, real database --

    [Fact]
    public async Task Annual_cancel_in_month_5_stamps_paid_through_and_queues_the_339_refund()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAnnualGoldAsync(db, daysIn: 135);

        var ok = await NewService(db).CancelSubscriptionAsync(seed.AgentId);
        Assert.True(ok);

        db.ChangeTracker.Clear();
        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        Assert.Equal(BillingStatus.Cancelled, billing.Status);
        AssertClose(seed.StartDate.AddMonths(5).AddDays(PrepaidValue.CancellationGraceDays), billing.PaidThroughAt);

        var change = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);
        Assert.Equal(300.00m, change.RefundNetAmount);
        Assert.Equal(39.00m, change.RefundTaxAmount);
        Assert.Equal(339.00m, change.RefundGrossAmount);
        Assert.Equal(RefundStatus.Pending, change.RefundStatus);
        Assert.Equal("TXN-ANNUAL-1", change.RefundPayPalTransactionId);
        AssertClose(PrepaidValue.RefundWindowEndsAt(seed.StartDate), change.RefundWindowEndsAt);
    }

    [Fact]
    public async Task Annual_cancel_in_month_11_defers_to_the_anniversary_with_no_refund()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAnnualGoldAsync(db, daysIn: 320);

        Assert.True(await NewService(db).CancelSubscriptionAsync(seed.AgentId));

        db.ChangeTracker.Clear();
        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        AssertClose(seed.StartDate.AddYears(1).AddDays(PrepaidValue.CancellationGraceDays), billing.PaidThroughAt);
        var change = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);
        Assert.Equal(0m, change.RefundGrossAmount);
        Assert.Equal(RefundStatus.None, change.RefundStatus);
    }

    [Fact]
    public async Task A_cancelled_but_paid_through_agent_is_not_gated_until_the_date_passes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAnnualGoldAsync(db, daysIn: 135);
        Assert.True(await NewService(db).CancelSubscriptionAsync(seed.AgentId));
        db.ChangeTracker.Clear();

        var entitlements = new PackageEntitlementService(new UnitOfWork(db), db);
        Assert.False(await entitlements.IsAccessGatedAsync(seed.AgentId));

        // The bulk resolver must agree (its comment demands it stays logically identical).
        var bulk = await entitlements.HasAccessBulkAsync(new[] { seed.AgentId }, seed.FeatureCode);
        Assert.True(bulk[seed.AgentId]);

        // Now age the paid-through date out and the gate closes like it always did.
        var billing = await db.Billings.SingleAsync(b => b.Id == seed.BillingId);
        billing.PaidThroughAt = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.True(await new PackageEntitlementService(new UnitOfWork(db), db).IsAccessGatedAsync(seed.AgentId));
    }

    [Fact]
    public async Task Monthly_cancel_writes_paid_through_and_no_refund_row()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAnnualGoldAsync(db, daysIn: 10, period: BillingPeriod.Monthly,
            amount: 60m, nextBillingInDays: 20);

        Assert.True(await NewService(db).CancelSubscriptionAsync(seed.AgentId));
        db.ChangeTracker.Clear();

        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        AssertClose(billing.NextBillingDate!.Value.AddDays(PrepaidValue.CancellationGraceDays), billing.PaidThroughAt);
        var change = await db.SubscriptionChanges.AsNoTracking()
            .SingleAsync(c => c.AgentUserId == seed.AgentId && c.ChangeType == SubscriptionChangeType.Cancel);
        Assert.Equal(0m, change.RefundGrossAmount);
        Assert.Equal(RefundStatus.None, change.RefundStatus);
    }

    // ------------------------------------------------- Stage D: the convert-downgrade credit --

    [Fact]
    public void Convert_credit_prices_the_remainder_at_what_was_paid_and_rounds_up()
    {
        // The owner's shape: Gold annual ($600 paid), converting to Silver monthly on Dec 6 --
        // 212 of 365 days left. Remaining value = 600 x 212/365 = $348.49; at Silver's net daily
        // rate (480/365) that is 264.99 days -> 265 free days, rounded in the agent's favour.
        var cycleStart = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc);
        var cycleEnd = cycleStart.AddYears(1);
        var now = new DateTime(2026, 12, 6, 0, 0, 0, DateTimeKind.Utc);

        var (remainingNet, creditDays, creditEnd) =
            IPRO.Billing.PayPalBillingService.ComputeConvertCredit(600m, 40m, now, cycleStart, cycleEnd);

        Assert.Equal(348.49m, remainingNet);
        Assert.Equal(265, creditDays);
        Assert.Equal(now.AddDays(265), creditEnd);
    }

    [Fact]
    public async System.Threading.Tasks.Task Convert_is_refused_for_monthly_subscribers_with_a_plain_explanation()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAnnualGoldAsync(db, daysIn: 10, period: BillingPeriod.Monthly, amount: 60m, nextBillingInDays: 20);
        // A cheaper package to downgrade to.
        var silver = new BillingRule { PackageName = ($"PVs-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m, AnnualPrice = 400m, IsActive = true, PayPalMonthlyPlanId = "P-TEST-M", PayPalAnnualPlanId = "P-TEST-A" };
        db.Add(silver);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await NewService(db).CreateSubscriptionAsync(
            seed.AgentId, silver.Id, BillingPeriod.Monthly, "https://x/return", "https://x/cancel", downgradeMode: "convert");

        Assert.False(result.Success);
        Assert.Contains("annual subscriptions", result.Message);
        // And nothing was written: no pending billing, no change row.
        Assert.Equal(0, await db.SubscriptionChanges.AsNoTracking().CountAsync(c => c.AgentUserId == seed.AgentId));
    }

    [Fact]
    public async System.Threading.Tasks.Task Convert_for_an_annual_subscriber_reaches_checkout_creation()
    {
        // Without PayPal settings the flow must stop at the explicit "PayPal is not configured"
        // gate INSIDE BeginPaidChangeAsync -- proving the convert branch routed all the way to
        // checkout creation with the credit computed, and failed clean without writing a billing.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAnnualGoldAsync(db, daysIn: 135);
        var silver = new BillingRule { PackageName = ($"PVs-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m, AnnualPrice = 400m, IsActive = true, PayPalMonthlyPlanId = "P-TEST-M", PayPalAnnualPlanId = "P-TEST-A" };
        db.Add(silver);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await NewService(db).CreateSubscriptionAsync(
            seed.AgentId, silver.Id, BillingPeriod.Monthly, "https://x/return", "https://x/cancel", downgradeMode: "convert");

        Assert.False(result.Success);
        Assert.Contains("PayPal is not configured", result.Message);
        Assert.Equal(0, await db.Billings.AsNoTracking().CountAsync(b => b.AgentUserId == seed.AgentId && b.Status == BillingStatus.Pending));
    }

    [Fact]
    public void The_view_offers_convert_through_a_framework_form_and_guards_the_term_switch()
    {
        // Source-walking guards, CheckoutHostPreservationTests-style: the convert mode must travel
        // as a hidden field in an asp-action form (host-aware URL -- the WEB-H-1 lesson), never a
        // hardcoded formaction path; and the term-switch buttons must be fenced behind the
        // pending-change check that UX-TERMSWITCH demanded.
        var view = System.IO.File.ReadAllText(FindViewPath());
        Assert.Contains("name=\"downgradeMode\" value=\"convert\"", view);
        Assert.DoesNotContain("formaction=", view);
        Assert.Contains("pendingChange != null", view);
    }

    private static string FindViewPath()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!.FullName, "src", "IPRO.Web", "Views", "Billing", "Index.cshtml");
    }

    // ------------------------------------------------------------- the SuperAdmin refund queue --

    [Fact]
    public void The_refund_queue_is_SuperAdmin_only()
    {
        var attrs = typeof(IPRO.Admin.Controllers.RefundsController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();
        Assert.Contains(attrs, a => a.Policy == "SuperAdmin");
    }

    [Fact]
    public async Task Marking_a_refund_refunded_requires_the_txn_id_and_flips_exactly_once()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAnnualGoldAsync(db, daysIn: 135);
        Assert.True(await NewService(db).CancelSubscriptionAsync(seed.AgentId));
        db.ChangeTracker.Clear();
        var change = await db.SubscriptionChanges.SingleAsync(c => c.ChangeType == SubscriptionChangeType.Cancel && c.AgentUserId == seed.AgentId);

        var controller = NewRefundsController(db);
        // Missing txn id: refused, still Pending.
        await controller.MarkRefunded(change.Id, "");
        db.ChangeTracker.Clear();
        Assert.Equal(RefundStatus.Pending, (await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == change.Id)).RefundStatus);

        // With the txn id: resolved, note carries it, and a second attempt is a 404 (no re-flip).
        await controller.MarkRefunded(change.Id, "REFUND-TXN-9");
        db.ChangeTracker.Clear();
        var resolved = await db.SubscriptionChanges.AsNoTracking().SingleAsync(c => c.Id == change.Id);
        Assert.Equal(RefundStatus.Refunded, resolved.RefundStatus);
        Assert.NotNull(resolved.RefundResolvedAt);
        Assert.Contains("REFUND-TXN-9", resolved.RefundResolutionNote);
        var second = await NewRefundsController(db).MarkRefunded(change.Id, "REFUND-TXN-10");
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(second);
    }

    private static IPRO.Admin.Controllers.RefundsController NewRefundsController(IPRODbContext db)
    {
        var controller = new IPRO.Admin.Controllers.RefundsController(db, new NullAuditLog());
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "1") }, "test"))
        };
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            ctx, new NullTempData());
        return controller;
    }

    private sealed class NullAuditLog : IPRO.Business.Interfaces.IAdminAuditLogService
    {
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details) => Task.CompletedTask;
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public System.Collections.Generic.IDictionary<string, object> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context) => new System.Collections.Generic.Dictionary<string, object>();
        public void SaveTempData(Microsoft.AspNetCore.Http.HttpContext context, System.Collections.Generic.IDictionary<string, object> values) { }
    }

    // ------------------------------------------------------------------------------ plumbing --

    // MySQL datetime(6) keeps microseconds, .NET DateTime keeps 100ns ticks -- exact equality
    // fails on the round trip by design, so date assertions allow a second of slack.
    private static void AssertClose(DateTime expected, DateTime? actual)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs((expected - actual!.Value).TotalSeconds) < 1,
            $"expected ~{expected:O}, got {actual:O}");
    }

    private sealed record Seed(int AgentId, int BillingId, DateTime StartDate, string FeatureCode);

    private static async Task<Seed> SeedAnnualGoldAsync(
        IPRODbContext db, int daysIn,
        BillingPeriod period = BillingPeriod.Annually,
        decimal amount = 600m,
        int? nextBillingInDays = null)
    {
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "ON"))
        {
            db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", Rate = 0.13m, TaxLabel = "ON 13% HST", IsActive = true });
        }
        var rule = new BillingRule { PackageName = $"PV-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = "pv_feature", FeatureName = "PV", IsIncluded = true });

        var agent = new AgentUser
        {
            UserName = $"pv-{Guid.NewGuid():N}"[..20],
            Email = "pv@example.test",
            FirstName = "Prepaid",
            LastName = "Value",
            DomainName = $"pv-{Guid.NewGuid():N}"[..24],
            Country = "Canada",
            Province = "Ontario",
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var start = DateTime.UtcNow.AddDays(-daysIn);
        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id,
            BillingRuleId = rule.Id,
            PayPalSubscriptionId = string.Empty,   // no PayPal settings in tests: cancel is a clean no-op
            PayPalPlanId = string.Empty,
            Amount = amount,
            Currency = "CAD",
            Status = BillingStatus.Active,
            Period = period,
            StartDate = start,
            NextBillingDate = nextBillingInDays.HasValue ? DateTime.UtcNow.AddDays(nextBillingInDays.Value) : start.AddYears(1)
        };
        db.Add(billing);
        await db.SaveChangesAsync();

        db.Add(new Invoice
        {
            BillingId = billing.Id,
            AgentUserId = agent.Id,
            InvoiceNumber = $"PV-{billing.Id}",
            SubTotal = amount,
            TaxRate = 0.13m,
            TaxAmount = Math.Round(amount * 0.13m, 2),
            Total = Math.Round(amount * 1.13m, 2),
            PayPalTransactionId = "TXN-ANNUAL-1",
            IssuedAt = start,
            IsPaid = true
        });
        await db.SaveChangesAsync();
        return new Seed(agent.Id, billing.Id, start, "pv_feature");
    }

    private static PayPalBillingService NewService(IPRODbContext db) => new(
        new UnitOfWork(db),
        db,
        new StubHttpClientFactory(),
        new StubEmailService2(),
        Options.Create(new PayPalSettings()),
        new ConfigurationBuilder().Build(),
        NullLogger<PayPalBillingService>.Instance);

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubEmailService2 : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, System.Collections.Generic.IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(true);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, System.Collections.Generic.IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(IPRO.Email.EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(System.Collections.Generic.IEnumerable<IPRO.Email.EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }
}
