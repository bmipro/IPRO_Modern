using System;
using System.IO;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// 503 (2026-09-20, found in the owner's first real-money test on live PayPal): a promotion code
// with a LIMITED number of discounted cycles writes the discounted price into Billing.Amount, and
// the Billing page printed that as the next charge -- "Next billing: October 20 - $3.00" for a
// subscription PayPal was about to bill $60.00 plus tax. The stored amount only catches up when
// the first full-price sale arrives, which is one charge too late for the customer reading the
// page. NextCharge works out which cycle the next billing date is and says the price PayPal's
// plan has for THAT cycle. Every defect test observed RED on the pre-fix code; the controls
// (no promotion, a forever promotion, an amount already caught up) passed before and after.
public class NextCharge503Tests
{
    private static readonly DateTime Start = new(2026, 9, 20, 16, 48, 0, DateTimeKind.Utc);

    // ---- the arithmetic ----------------------------------------------------------------------

    [Fact]
    public void A_one_cycle_promotion_shows_the_regular_price_for_the_second_charge()
    {
        var next = NextCharge.Compute(3.00m, Start, Start.AddMonths(1), BillingPeriod.Monthly, 1, 60.00m, Start.AddHours(1));

        Assert.Equal(60.00m, next.Amount);
        Assert.Equal(0, next.DiscountedCyclesLeft);
        Assert.True(next.PromotionEnded);
    }

    [Theory]
    [InlineData(1, 30.00, 2)]
    [InlineData(2, 30.00, 1)]
    [InlineData(3, 60.00, 0)]
    [InlineData(4, 60.00, 0)]
    public void A_three_cycle_promotion_keeps_its_price_for_exactly_three_charges(int monthsToNextBilling, double expected, int cyclesLeft)
    {
        var nextBilling = Start.AddMonths(monthsToNextBilling);
        var next = NextCharge.Compute(30.00m, Start, nextBilling, BillingPeriod.Monthly, 3, 60.00m, nextBilling.AddDays(-20));

        Assert.Equal((decimal)expected, next.Amount);
        Assert.Equal(cyclesLeft, next.DiscountedCyclesLeft);
    }

    [Fact]
    public void A_free_first_month_is_followed_by_the_regular_price_not_by_zero()
    {
        var next = NextCharge.Compute(0m, Start, Start.AddMonths(1), BillingPeriod.Monthly, 1, 60.00m, Start.AddDays(3));

        Assert.Equal(60.00m, next.Amount);
    }

    [Fact]
    public void An_annual_one_cycle_promotion_shows_the_regular_price_for_next_year()
    {
        var next = NextCharge.Compute(300.00m, Start, Start.AddYears(1), BillingPeriod.Annually, 1, 600.00m, Start.AddDays(40));

        Assert.Equal(600.00m, next.Amount);
        Assert.True(next.PromotionEnded);
    }

    [Fact]
    public void PayPals_own_billing_time_a_day_or_two_off_still_counts_whole_cycles()
    {
        var start = new DateTime(2027, 1, 31, 9, 0, 0, DateTimeKind.Utc);
        var next = NextCharge.Compute(30.00m, start, new DateTime(2027, 2, 28, 10, 0, 0, DateTimeKind.Utc), BillingPeriod.Monthly, 1, 60.00m, start.AddDays(2));

        Assert.Equal(60.00m, next.Amount);
    }

    [Fact]
    public void A_first_charge_that_is_still_ahead_is_a_discounted_one_however_far_away_it_is()
    {
        // Re-subscribing inside a period already paid for: the first charge waits for that date.
        var now = Start.AddMinutes(5);
        var next = NextCharge.Compute(30.00m, Start, Start.AddMonths(10), BillingPeriod.Monthly, 3, 60.00m, now);

        Assert.Equal(30.00m, next.Amount);
        Assert.Equal(3, next.DiscountedCyclesLeft);
        Assert.False(next.PromotionEnded);
    }

    // ---- controls: these read the same before and after ---------------------------------------

    [Fact]
    public void Control_no_promotion_is_the_stored_amount()
    {
        var next = NextCharge.Compute(60.00m, Start, Start.AddMonths(1), BillingPeriod.Monthly, null, null, Start);

        Assert.Equal(60.00m, next.Amount);
        Assert.Equal(0, next.DiscountedCyclesLeft);
        Assert.False(next.PromotionEnded);
    }

    [Fact]
    public void Control_a_forever_promotion_is_the_stored_amount_every_cycle()
    {
        var next = NextCharge.Compute(48.00m, Start, Start.AddMonths(7), BillingPeriod.Monthly, null, 60.00m, Start.AddMonths(6));

        Assert.Equal(48.00m, next.Amount);
        Assert.False(next.PromotionEnded);
    }

    [Fact]
    public void Control_once_the_stored_amount_has_caught_up_nothing_is_added_to_it()
    {
        // The first full-price sale has arrived and the sale webhook wrote 60.00 into the row.
        var next = NextCharge.Compute(60.00m, Start, Start.AddMonths(3), BillingPeriod.Monthly, 1, 60.00m, Start.AddMonths(2));

        Assert.Equal(60.00m, next.Amount);
    }

    // ---- the lookup, on the real database ------------------------------------------------------

    [Fact]
    public async Task The_lookup_finds_the_promotion_through_the_applied_change_of_this_subscription()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, ruleId) = await SeedAgentAsync(db);
        var promoId = await SeedPromotionAsync(db, ruleId, cycles: 1);
        var billing = await SeedSubscriptionAsync(db, agentId, ruleId, amount: 3.00m, promoId);

        var next = await NextCharge.ForAsync(db, billing, Start.AddHours(2));

        Assert.Equal(60.00m, next.Amount);
        Assert.Equal(60.00m, next.RegularAmount);
        Assert.Equal(1, next.PromoCycles);
        Assert.True(next.PromotionEnded);
    }

    [Fact]
    public async Task The_lookup_keeps_the_discount_while_discounted_cycles_remain()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, ruleId) = await SeedAgentAsync(db);
        var promoId = await SeedPromotionAsync(db, ruleId, cycles: 3);
        var billing = await SeedSubscriptionAsync(db, agentId, ruleId, amount: 3.00m, promoId);

        var next = await NextCharge.ForAsync(db, billing, Start.AddHours(2));

        Assert.Equal(3.00m, next.Amount);
        Assert.Equal(2, next.DiscountedCyclesLeft);
        Assert.Equal(60.00m, next.RegularAmount);
    }

    [Fact]
    public async Task Control_a_subscription_bought_without_a_code_reads_as_stored()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, ruleId) = await SeedAgentAsync(db);
        var billing = await SeedSubscriptionAsync(db, agentId, ruleId, amount: 60.00m, promotionCodeId: null);

        var next = await NextCharge.ForAsync(db, billing, Start.AddHours(2));

        Assert.Equal(60.00m, next.Amount);
        Assert.False(next.PromotionEnded);
    }

    [Fact]
    public async Task Control_a_code_used_on_an_earlier_subscription_does_not_follow_the_agent_to_the_next_one()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, ruleId) = await SeedAgentAsync(db);
        var promoId = await SeedPromotionAsync(db, ruleId, cycles: 1);
        var earlier = await SeedSubscriptionAsync(db, agentId, ruleId, amount: 3.00m, promoId);
        earlier.Status = BillingStatus.Cancelled;
        await db.SaveChangesAsync();
        var current = await SeedSubscriptionAsync(db, agentId, ruleId, amount: 45.00m, promotionCodeId: null);

        var next = await NextCharge.ForAsync(db, current, Start.AddHours(2));

        Assert.Equal(45.00m, next.Amount);
    }

    // ---- the two screens that print the next charge --------------------------------------------

    [Theory]
    [InlineData(@"src\IPRO.Web\Views\Billing\Index.cshtml", "subscription.Amount.ToString(\"N2\") @subscription.Currency plus applicable taxes")]
    [InlineData(@"src\IPRO.Admin\Views\Agents\Details.cshtml", "$@activeBilling.Amount.ToString(\"N2\") @activeBilling.Currency / @activeBilling.Period")]
    public void Neither_screen_prints_the_stored_amount_as_the_next_charge(string view, string oldLine)
    {
        var source = File.ReadAllText(FindRepoFile(view));

        Assert.DoesNotContain(oldLine, source);
        Assert.Contains("NextCharge", source);
    }

    [Theory]
    [InlineData(@"src\IPRO.Web\Controllers\BillingController.cs")]
    [InlineData(@"src\IPRO.Admin\Controllers\AgentsController.cs")]
    public void Both_controllers_ask_for_the_next_charge(string controller)
    {
        Assert.Contains("NextCharge.ForAsync(", File.ReadAllText(FindRepoFile(controller)));
    }

    [Fact]
    public void The_customer_is_told_when_the_promotional_price_ends()
    {
        var source = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Billing\Index.cshtml"));

        Assert.Contains("PromotionEnded", source);
        Assert.Contains("DiscountedCyclesLeft", source);
    }

    // ---- seeding ---------------------------------------------------------------------------------

    private static async Task<(int AgentId, int RuleId)> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"NC-{Guid.NewGuid():N}")[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"nc-{Guid.NewGuid():N}")[..20],
            Email = $"nc-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Next",
            LastName = "Charge",
            DomainName = ($"nc-{Guid.NewGuid():N}")[..24],
            IsActive = true,
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return (agent.Id, rule.Id);
    }

    private static async Task<int> SeedPromotionAsync(IPRODbContext db, int ruleId, int? cycles)
    {
        var promo = new PromotionCode
        {
            Code = ($"NC{Guid.NewGuid():N}")[..16].ToUpperInvariant(),
            RestrictedBillingRuleId = ruleId,
            RecurringDiscountType = PromoDiscountType.PercentOff,
            RecurringDiscountValue = 95m,
            RecurringDurationCycles = cycles
        };
        db.Add(promo);
        await db.SaveChangesAsync();
        return promo.Id;
    }

    private static async Task<IPRO.Entities.Billing> SeedSubscriptionAsync(IPRODbContext db, int agentId, int ruleId, decimal amount, int? promotionCodeId)
    {
        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agentId,
            BillingRuleId = ruleId,
            Amount = amount,
            Status = BillingStatus.Active,
            Period = BillingPeriod.Monthly,
            StartDate = Start,
            NextBillingDate = Start.AddMonths(1),
            PayPalSubscriptionId = ($"I-{Guid.NewGuid():N}")[..14].ToUpperInvariant()
        };
        db.Add(billing);
        await db.SaveChangesAsync();

        db.Add(new SubscriptionChange
        {
            AgentUserId = agentId,
            RequestedBillingRuleId = ruleId,
            BillingId = billing.Id,
            PromotionCodeId = promotionCodeId,
            ChangeType = SubscriptionChangeType.Subscribe,
            Status = SubscriptionChangeStatus.Applied,
            Period = BillingPeriod.Monthly,
            EffectiveDate = Start,
            AppliedAt = Start
        });
        if (promotionCodeId.HasValue)
        {
            db.Add(new PromotionCodeRedemption
            {
                PromotionCodeId = promotionCodeId.Value,
                AgentUserId = agentId,
                BillingRuleId = ruleId,
                Period = BillingPeriod.Monthly,
                OriginalRecurringAmount = 60.00m,
                DiscountedRecurringAmount = amount,
                OriginalSetupFee = 0m,
                DiscountedSetupFee = 0m,
                RedeemedAt = Start
            });
        }
        await db.SaveChangesAsync();
        return billing;
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IPRO.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
