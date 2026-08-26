using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// Billing wave 3 (2026-08-25) — the owner-selected audit remainder: F2 (renewal re-tax at the
// current province + Billing.Amount rewrite), F3 (province entry silently zero-rating tax), F5
// (promo plan price divergence), and the owner's decision on the post-upgrade annual cancel
// refund policy ("least complicated; worst case I lose ~2 months" -> unused new-rate value capped
// at everything actually settled in the cycle across the agent's rows). Every defect test here was
// run against the pre-fix code and observed to FAIL.
public class BillingWave3Tests
{
    // -------------------------------------------------------- F3: province entry and aliases --

    [Fact]
    public void F3_the_register_dropdowns_own_labels_normalize_to_tax_codes()
    {
        // The register's dropdown emits "Yukon Territory"; the alias map only knew "YUKON" -- a
        // Yukon signup zero-rated every charge from day one. PEI's universal abbreviation was
        // missing too.
        Assert.Equal("YT", PayPalBillingService.NormalizeProvince("Yukon Territory"));
        Assert.Equal("PE", PayPalBillingService.NormalizeProvince("PEI"));
        Assert.Equal("PE", PayPalBillingService.NormalizeProvince("p.e.i."));
        Assert.Equal("NT", PayPalBillingService.NormalizeProvince("NWT"));
        // The existing shapes must keep working.
        Assert.Equal("ON", PayPalBillingService.NormalizeProvince("Ontario"));
        Assert.Equal("QC", PayPalBillingService.NormalizeProvince("Québec"));
        Assert.Equal("AB", PayPalBillingService.NormalizeProvince("ab"));
    }

    [Fact]
    public void F3_the_profile_page_offers_a_province_dropdown_not_a_free_text_box()
    {
        // Register uses a dropdown; Profile was a free-text input saved raw -- an agent typing
        // anything the normalizer misses silently zero-rates all future tax, with no log.
        var view = System.IO.File.ReadAllText(FindViewPath("IPRO.Web", "Views", "Account", "Profile.cshtml"));
        Assert.Contains("<select asp-for=\"Province\"", view);
        Assert.Contains("Prince Edward Island", view);
        Assert.DoesNotContain("<input asp-for=\"Province\"", view);
    }

    // ------------------------------ F2: renewals keep the tax split they were actually sold at --

    [Fact]
    public async Task F2_a_renewal_after_a_province_move_keeps_the_invoiced_rate_and_the_stored_amount()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var seed = await SeedBillingAsync(db, amount: 600m, payPalSubscriptionId: "I-F2MOVE",
            invoice: (600m, 0.13m, now.AddYears(-1), "TXN-F2-Y1"));

        // The agent has since moved to Alberta (5%); PayPal's plan was built tax-inclusive in
        // Ontario and keeps charging $678.00 gross.
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "AB"))
        {
            db.Add(new ProvinceTaxRate { ProvinceCode = "AB", ProvinceName = "Alberta", Rate = 0.05m, TaxLabel = "AB 5% GST", IsActive = true });
        }
        var agent = await db.AgentUsers.SingleAsync(a => a.Id == seed.AgentId);
        agent.Province = "Alberta";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.True(await NewService(db).HandleSubscriptionPaymentCompletedWebhookAsync("I-F2MOVE", "TXN-F2-Y2", 678.00m));
        db.ChangeTracker.Clear();

        // Pre-fix: de-taxed at TODAY'S 5% -> invoice $645.71 net + $32.29 GST for money PayPal
        // collected as $600 + $78 Ontario HST, and Billing.Amount silently became 645.71 --
        // corrupting CRA remittance on both provinces and every later proration and clawback.
        var renewal = await db.Invoices.AsNoTracking()
            .SingleAsync(i => i.AgentUserId == seed.AgentId && i.PayPalTransactionId.Contains("TXN-F2-Y2"));
        Assert.Equal(600.00m, renewal.SubTotal);
        Assert.Equal(0.13m, renewal.TaxRate);
        Assert.Equal(78.00m, renewal.TaxAmount);
        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        Assert.Equal(600.00m, billing.Amount);
    }

    [Fact]
    public async Task F2_a_lapsed_promo_price_still_updates_the_stored_amount_to_what_paypal_bills()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        // Duration-limited promo wrote the discounted $300 into Amount; the promo cycles lapsed
        // and PayPal now charges full $678 gross in the SAME province. The 2026-08-16 behavior
        // (Amount follows what PayPal actually bills) must survive the F2 fix.
        var seed = await SeedBillingAsync(db, amount: 300m, payPalSubscriptionId: "I-F2LAPSE",
            invoice: (300m, 0.13m, now.AddMonths(-1), "TXN-F2-PROMO"));

        Assert.True(await NewService(db).HandleSubscriptionPaymentCompletedWebhookAsync("I-F2LAPSE", "TXN-F2-FULL", 678.00m));
        db.ChangeTracker.Clear();

        var billing = await db.Billings.AsNoTracking().SingleAsync(b => b.Id == seed.BillingId);
        Assert.Equal(600.00m, billing.Amount);   // de-taxed at the invoiced 13%: 678/1.13
    }

    // ------------------------------------------ F5: promo plans cannot outlive a price change --

    [Fact]
    public async Task F5_syncing_a_packages_plans_clears_the_frozen_promo_plans_that_price_against_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var rule = new BillingRule
        {
            PackageName = $"W3-{Guid.NewGuid():N}"[..20],
            MonthlyPrice = 50m, AnnualPrice = 500m, IsActive = true,
            PayPalMonthlyPlanId = "P-W3-M", PayPalAnnualPlanId = "P-W3-A"
        };
        db.Add(rule);
        await db.SaveChangesAsync();
        var promo = new PromotionCode
        {
            Code = $"W3{Guid.NewGuid():N}"[..10],
            IsActive = true,
            RestrictedBillingRuleId = rule.Id,
            RecurringDiscountType = PromoDiscountType.PercentOff,
            RecurringDiscountValue = 20m,
            PayPalPromoPlanIdMonthly = "P-PROMO-OLD-M",
            PayPalPromoPlanIdAnnual = "P-PROMO-OLD-A"
        };
        db.Add(promo);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The owner re-syncs after a price edit (the divergence guard makes this mandatory before
        // checkouts resume). Pre-fix the promo's frozen plan ids survived the sync: the invoice
        // then said the NEW price while PayPal charged the OLD one, forever. The PayPal legs fail
        // offline here -- the clearing must land regardless, because a promo plan that lazily
        // recreates at the next checkout prices against the CURRENT package either way.
        await NewServiceWithPayPal(db).SyncPayPalPlansAsync(rule.Id);
        db.ChangeTracker.Clear();

        var after = await db.PromotionCodes.AsNoTracking().SingleAsync(p => p.Id == promo.Id);
        Assert.True(string.IsNullOrEmpty(after.PayPalPromoPlanIdMonthly),
            $"monthly promo plan id must be cleared, was '{after.PayPalPromoPlanIdMonthly}'");
        Assert.True(string.IsNullOrEmpty(after.PayPalPromoPlanIdAnnual),
            $"annual promo plan id must be cleared, was '{after.PayPalPromoPlanIdAnnual}'");
    }

    // ------------- POLICY: post-upgrade annual cancel (owner decision 2026-08-25, this thread) --

    [Fact]
    public async Task POLICY_an_upgraded_annual_cancel_refunds_unused_value_capped_at_everything_settled_this_cycle()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var now = DateTime.UtcNow;
        var cycleAnchor = now.AddMonths(8).AddDays(10);   // 4 months into the year, off-boundary

        // The OLD Gold row: superseded by the upgrade, its $600 renewal bought the same year.
        var old = await SeedBillingAsync(db, amount: 600m, payPalSubscriptionId: "I-POL-OLD",
            invoice: (600m, 0.13m, cycleAnchor.AddYears(-1), "TXN-POL-GOLD"),
            status: BillingStatus.Cancelled, nextBillingDate: cycleAnchor);
        // The NEW Platinum row on the SAME agent: only the $500 proration difference was captured.
        var neu = await SeedBillingAsync(db, amount: 1200m, payPalSubscriptionId: "",
            invoice: (500m, 0.13m, cycleAnchor.AddYears(-1).AddMonths(2), "TXN-POL-DIFF"),
            nextBillingDate: cycleAnchor, agentId: old.AgentId);

        Assert.True(await NewService(db).CancelSubscriptionAsync(old.AgentId));
        db.ChangeTracker.Clear();

        var change = await db.SubscriptionChanges.AsNoTracking()
            .Where(c => c.AgentUserId == old.AgentId && c.ChangeType == SubscriptionChangeType.Cancel)
            .OrderByDescending(c => c.Id).FirstAsync();
        // Unused value at the new rate: $1,200 - 4 x $120 = $720, and the cycle genuinely
        // collected $1,100 across both rows -- so $720 stands. The interim (wave-2) cap at the
        // row's own $500 short-changed the agent by the old row's remainder; the fair cross-row
        // figure is $740, so the simple rule costs the agent $20, not $720.
        Assert.Equal(720.00m, change.RefundNetAmount);
        Assert.Equal(93.60m, change.RefundTaxAmount);
        Assert.Contains("TXN-POL-GOLD", change.RefundResolutionNote);   // owner splits across captures
    }

    // ------------------------------------------------------------------------------ plumbing --

    private sealed record Seed(int AgentId, int BillingId);

    private static async Task<Seed> SeedBillingAsync(
        IPRODbContext db, decimal amount, string payPalSubscriptionId,
        (decimal SubTotal, decimal TaxRate, DateTime IssuedAt, string Txn) invoice,
        BillingStatus status = BillingStatus.Active,
        DateTime? nextBillingDate = null,
        int? agentId = null)
    {
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "ON"))
        {
            db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", Rate = 0.13m, TaxLabel = "ON 13% HST", IsActive = true });
        }
        var rule = new BillingRule { PackageName = $"W3-{Guid.NewGuid():N}"[..20], MonthlyPrice = amount / 10m, AnnualPrice = amount };
        db.Add(rule);
        await db.SaveChangesAsync();

        int theAgentId;
        if (agentId.HasValue)
        {
            theAgentId = agentId.Value;
        }
        else
        {
            var agent = new AgentUser
            {
                UserName = $"w3-{Guid.NewGuid():N}"[..20],
                Email = $"w3-{Guid.NewGuid():N}"[..12] + "@example.test",
                FirstName = "Wave", LastName = "Three",
                DomainName = $"w3-{Guid.NewGuid():N}"[..24],
                Country = "Canada", Province = "Ontario", PackageId = rule.Id
            };
            db.Add(agent);
            await db.SaveChangesAsync();
            theAgentId = agent.Id;
        }

        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = theAgentId,
            BillingRuleId = rule.Id,
            PayPalSubscriptionId = payPalSubscriptionId,
            Amount = amount,
            Status = status,
            Period = BillingPeriod.Annually,
            StartDate = DateTime.UtcNow.AddYears(-1),
            NextBillingDate = nextBillingDate ?? DateTime.UtcNow.AddDays(30),
            CancelledAt = status == BillingStatus.Cancelled ? DateTime.UtcNow.AddMonths(-2) : null
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        db.Add(new Invoice
        {
            BillingId = billing.Id,
            AgentUserId = theAgentId,
            InvoiceNumber = $"W3-{billing.Id}",
            SubTotal = invoice.SubTotal,
            TaxRate = invoice.TaxRate,
            TaxAmount = Math.Round(invoice.SubTotal * invoice.TaxRate, 2),
            Total = Math.Round(invoice.SubTotal * (1 + invoice.TaxRate), 2),
            TaxRegion = "ON 13% HST",
            PayPalTransactionId = invoice.Txn,
            IssuedAt = invoice.IssuedAt,
            IsPaid = true
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(theAgentId, billing.Id);
    }

    private static PayPalBillingService NewService(IPRODbContext db) => new(
        new UnitOfWork(db), db, new StubHttpClientFactory(), new StubEmailService(),
        Options.Create(new PayPalSettings()),
        new ConfigurationBuilder().Build(),
        NullLogger<PayPalBillingService>.Instance);

    private static PayPalBillingService NewServiceWithPayPal(IPRODbContext db) => new(
        new UnitOfWork(db), db, new OfflineHttpClientFactory(), new StubEmailService(),
        Options.Create(new PayPalSettings { ClientId = "test-client", ClientSecret = "test-secret" }),
        new ConfigurationBuilder().Build(),
        NullLogger<PayPalBillingService>.Instance);

    private static string FindViewPath(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return System.IO.Path.Combine(new[] { dir!.FullName, "src" }.Concat(parts).ToArray());
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class OfflineHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RefuseHandler());
        private sealed class RefuseHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken ct)
                => throw new HttpRequestException("offline test handler");
        }
    }

    private sealed class StubEmailService : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(true);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(IPRO.Email.EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }
}
