using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 508 (2026-09-21, launch day). The owner wants to send prospects "one month free, or more". A
// code's cycle is whatever billing period the customer picks, and a code could not be limited to
// one: "100% off, 1 cycle" was a free first month on monthly billing and a free first YEAR -- $600
// on Gold -- for anyone who chose annual. A code can now be limited to monthly or to annual billing
// (PromotionCodePeriodLimit; no row = both, which is every code that existed before). The limit is
// enforced where the period is finally known, checkout, and SAID where the customer chooses:
// the registration page. Every defect test observed RED on the pre-fix code.
public class PromotionCodePeriod508Tests
{
    // ---- the limit itself, on the real database ------------------------------------------------

    [Fact]
    public async Task A_code_with_no_limit_works_with_both_periods_and_a_limit_can_be_set_changed_and_lifted()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db, limit: null);

        Assert.Null(await PromotionCodePeriod.LimitAsync(db, seed.PromoId));
        Assert.True(await PromotionCodePeriod.AllowsAsync(db, seed.PromoId, BillingPeriod.Monthly));
        Assert.True(await PromotionCodePeriod.AllowsAsync(db, seed.PromoId, BillingPeriod.Annually));

        await PromotionCodePeriod.SetAsync(db, seed.PromoId, BillingPeriod.Monthly);
        await PromotionCodePeriod.SetAsync(db, seed.PromoId, BillingPeriod.Monthly);   // saving the same answer twice is not an error
        Assert.Equal(BillingPeriod.Monthly, await PromotionCodePeriod.LimitAsync(db, seed.PromoId));
        Assert.True(await PromotionCodePeriod.AllowsAsync(db, seed.PromoId, BillingPeriod.Monthly));
        Assert.False(await PromotionCodePeriod.AllowsAsync(db, seed.PromoId, BillingPeriod.Annually));

        await PromotionCodePeriod.SetAsync(db, seed.PromoId, BillingPeriod.Annually);
        Assert.Equal(BillingPeriod.Annually, await PromotionCodePeriod.LimitAsync(db, seed.PromoId));

        await PromotionCodePeriod.SetAsync(db, seed.PromoId, null);
        Assert.Null(await PromotionCodePeriod.LimitAsync(db, seed.PromoId));
    }

    [Fact]
    public async Task Deleting_a_code_takes_its_limit_with_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db, limit: BillingPeriod.Monthly);

        await db.PromotionCodes.Where(p => p.Id == seed.PromoId).ExecuteDeleteAsync();

        Assert.Equal(0, await db.PromotionCodePeriodLimits.CountAsync(l => l.PromotionCodeId == seed.PromoId));
    }

    // ---- checkout: the one place the period is finally known ------------------------------------
    // A free-for-ever code (100% recurring and 100% setup) activates without PayPal at all, so what
    // checkout decided is plain to see: an Active row and a redemption, or neither.

    [Fact]
    public async Task A_monthly_only_code_is_not_this_checkouts_code_when_the_customer_picks_annual()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db, limit: BillingPeriod.Monthly);

        await NewService(db).CreateSubscriptionAsync(seed.AgentId, seed.RuleId, BillingPeriod.Annually, "https://x/r", "https://x/c");
        db.ChangeTracker.Clear();

        // Without the code this is an ordinary paid checkout, which the offline PayPal refuses: the
        // point is that nothing was given away.
        Assert.Equal(0, await db.Billings.CountAsync(b => b.AgentUserId == seed.AgentId && b.Status == BillingStatus.Active));
        Assert.Equal(0, await db.PromotionCodeRedemptions.CountAsync(r => r.AgentUserId == seed.AgentId));
    }

    [Fact]
    public async Task Control_the_same_code_works_with_the_period_it_is_for()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db, limit: BillingPeriod.Monthly);

        var result = await NewService(db).CreateSubscriptionAsync(seed.AgentId, seed.RuleId, BillingPeriod.Monthly, "https://x/r", "https://x/c");
        db.ChangeTracker.Clear();

        Assert.True(result.Success, result.Message);
        var active = await db.Billings.AsNoTracking().SingleAsync(b => b.AgentUserId == seed.AgentId && b.Status == BillingStatus.Active);
        Assert.Equal(BillingPeriod.Monthly, active.Period);
        Assert.Equal(1, await db.PromotionCodeRedemptions.CountAsync(r => r.AgentUserId == seed.AgentId));
    }

    [Fact]
    public async Task Control_a_code_with_no_limit_still_works_with_annual()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedAsync(db, limit: null);

        var result = await NewService(db).CreateSubscriptionAsync(seed.AgentId, seed.RuleId, BillingPeriod.Annually, "https://x/r", "https://x/c");
        db.ChangeTracker.Clear();

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, await db.PromotionCodeRedemptions.CountAsync(r => r.AgentUserId == seed.AgentId));
    }

    // ---- what the prospect is told ---------------------------------------------------------------

    [Theory]
    [InlineData(1, "Monthly", "Code accepted: 100% off the recurring price for your first month, with monthly billing only.")]
    [InlineData(2, "Monthly", "Code accepted: 100% off the recurring price for your first 2 months, with monthly billing only.")]
    [InlineData(1, "Annually", "Code accepted: 100% off the recurring price for your first year, with annual billing only.")]
    public void The_registration_page_says_which_billing_the_code_is_for(int cycles, string limit, string expected)
    {
        var promo = new PromotionCode { RecurringDiscountType = PromoDiscountType.PercentOff, RecurringDiscountValue = 100m, RecurringDurationCycles = cycles };

        Assert.Equal(expected, PromotionCodeText.Accepted(promo, PromotionCodePeriod.Parse(limit)));
    }

    [Fact]
    public void Control_a_code_for_both_periods_reads_as_it_always_did()
    {
        var promo = new PromotionCode
        {
            RecurringDiscountType = PromoDiscountType.PercentOff, RecurringDiscountValue = 20m, RecurringDurationCycles = 1,
            SetupFeeDiscountType = PromoDiscountType.FlatAmountOff, SetupFeeDiscountValue = 50m
        };

        Assert.Equal("Code accepted: 20% off the recurring price on your first billing cycle only and $50 off the setup fee.",
            PromotionCodeText.Accepted(promo, null));
    }

    [Fact]
    public void The_wrong_period_is_answered_with_the_right_one()
    {
        Assert.Equal("This code works with monthly billing only. Choose Monthly above to use it, or remove the code.",
            PromotionCodeText.WrongPeriod(BillingPeriod.Monthly));
    }

    // ---- the wiring --------------------------------------------------------------------------------

    [Fact]
    public void Registration_checks_the_chosen_period_on_the_page_and_again_on_the_server()
    {
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\AccountController.cs"));
        Assert.Contains("PromotionCodeText.WrongPeriod(", controller);
        Assert.Contains("PromotionCodeText.Accepted(", controller);
        Assert.Contains("ValidatePromoCode(string code, int packageId, string? period", controller);

        var page = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Account\Register.cshtml"));
        Assert.Contains("'&period=' + encodeURIComponent(", page);
    }

    [Fact]
    public void SuperAdmin_can_say_which_billing_a_code_is_for_and_sees_it_in_the_list()
    {
        var form = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\PromotionCodes\Edit.cshtml"));
        Assert.Contains("name=\"appliesTo\"", form);
        Assert.Contains("Monthly billing only", form);
        Assert.Contains("Annual billing only", form);

        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Controllers\PromotionCodesController.cs"));
        Assert.Contains("PromotionCodePeriod.SetAsync(", controller);

        var list = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\PromotionCodes\Index.cshtml"));
        Assert.Contains("PeriodLimits", list);
    }

    [Fact]
    public void Both_apps_create_the_table_at_startup()
    {
        foreach (var program in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
            Assert.Contains("StartupSchemaRepair.EnsurePromotionCodePeriodLimitSchemaAsync(db)", File.ReadAllText(FindRepoFile(program)));
    }

    // ---- plumbing ----------------------------------------------------------------------------------

    private sealed record Seed(int AgentId, int RuleId, int PromoId);

    private static async Task<Seed> SeedAsync(IPRODbContext db, BillingPeriod? limit)
    {
        var rule = new BillingRule
        {
            PackageName = ($"PP-{Guid.NewGuid():N}")[..20],
            MonthlyPrice = 60m, AnnualPrice = 600m, SetupFee = 200m, IsActive = true,
            PayPalMonthlyPlanId = "P-PP-M", PayPalAnnualPlanId = "P-PP-A"
        };
        db.Add(rule);
        await db.SaveChangesAsync();
        var promo = new PromotionCode
        {
            Code = ($"PP{Guid.NewGuid():N}")[..12].ToUpperInvariant(),
            IsActive = true,
            RestrictedBillingRuleId = rule.Id,
            RecurringDiscountType = PromoDiscountType.PercentOff, RecurringDiscountValue = 100m,
            SetupFeeDiscountType = PromoDiscountType.PercentOff, SetupFeeDiscountValue = 100m
        };
        db.Add(promo);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"pp-{Guid.NewGuid():N}")[..20],
            Email = $"pp-{Guid.NewGuid():N}"[..12] + "@example.test", FirstName = "Period", LastName = "Limit",
            DomainName = ($"pp-{Guid.NewGuid():N}")[..24],
            IsActive = true,
            PackageId = rule.Id,
            PromotionCode = promo.Code
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        if (limit.HasValue) await PromotionCodePeriod.SetAsync(db, promo.Id, limit);
        return new Seed(agent.Id, rule.Id, promo.Id);
    }

    private static PayPalBillingService NewService(IPRODbContext db) => new(
        new UnitOfWork(db), db, new OfflineHttpClientFactory(), new QuietEmail(),
        Options.Create(new PayPalSettings { ClientId = "test-client", ClientSecret = "test-secret" }),
        new ConfigurationBuilder().Build(),
        NullLogger<PayPalBillingService>.Instance);

    private sealed class OfflineHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RefuseHandler());
        private sealed class RefuseHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken ct)
                => throw new HttpRequestException("offline test handler");
        }
    }

    private sealed class QuietEmail : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(true);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(IPRO.Email.EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
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
