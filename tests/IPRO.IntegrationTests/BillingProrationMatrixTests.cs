using System;
using IPRO.Billing;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// Battery 418b: the cross-period proration matrix. Every scenario the platform ever executed
// before 2026-08-16 was a SAME-period change, so the annual->monthly upgrade shipped with a unit
// mismatch that priced ~10.7 months of the new tier as 0.89 of one month and gave the tier
// difference away -- found live, on the owner's own account, the first annual->monthly change in
// the product's history. This matrix exists so no period x direction combination is ever again
// first executed by a real customer.
//
// The convention under test: BOTH sides of an upgrade proration are priced in the units of the
// CYCLE BEING SPLIT (the old subscription's period). The credit comes from what was actually paid
// for that cycle (Billing.Amount); the charge is the new package's price for one old-period cycle
// (GetCycleEquivalentAmount), scaled by the same remaining fraction.
public class BillingProrationMatrixTests
{
    // Mirrors the owner's real account: signed up Jul 6 2026 on Silver ANNUAL at the pre-10x
    // price ($480 = 12 x $40); today's list price is $400. The credit must come from the $480 he
    // paid, never from the repriced list.
    private static BillingRule Silver() => new()
    {
        PackageName = "IPro Silver",
        MonthlyPrice = 40m,
        AnnualPrice = 400m,
        PayPalMonthlyPlanId = "P-SILVER-M",
        PayPalAnnualPlanId = "P-SILVER-A"
    };

    private static BillingRule Gold() => new()
    {
        PackageName = "IPro Gold",
        MonthlyPrice = 60m,
        AnnualPrice = 600m,
        PayPalMonthlyPlanId = "P-GOLD-M",
        PayPalAnnualPlanId = "P-GOLD-A"
    };

    private static readonly DateTime CycleStart = new(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CycleEnd = new(2027, 7, 6, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime UpgradeDay = new(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);

    private static decimal Prorate(decimal cyclePrice, decimal fraction) => Math.Round(cyclePrice * fraction, 2);

    // ---- The owner's exact scenario: Silver annual ($480 paid) -> Gold monthly, day 41 ----

    [Fact]
    public void Annual_to_monthly_upgrade_prices_the_remainder_in_annual_units()
    {
        var fraction = PayPalBillingService.CalculateRemainingFraction(UpgradeDay, CycleStart, CycleEnd);
        Assert.InRange(fraction, 0.88m, 0.89m); // ~324 of 365 days left

        var amountPaidForCycle = 480m; // Billing.Amount, the historical charge
        var credit = Prorate(amountPaidForCycle, fraction);
        var charge = Prorate(PayPalBillingService.GetCycleEquivalentAmount(Gold(), BillingPeriod.Annually), fraction);
        var amountDue = Math.Max(0, charge - credit);

        // Gold's annual price ($600) scaled by the same fraction as the credit -- NOT Gold's
        // monthly price, which is what the shipped bug used ($53.26, clamping amountDue to $0).
        Assert.InRange(charge, 532m, 533m);
        Assert.InRange(credit, 426m, 427m);
        Assert.InRange(amountDue, 106m, 107m); // the tier difference actually gets charged
    }

    [Fact]
    public void Credit_comes_from_the_amount_paid_not_the_repriced_list()
    {
        // After the 10x repricing, Silver's list AnnualPrice is $400 -- but the agent paid $480.
        // Crediting the list price would quietly shave ~$71 off the customer's credit every time
        // the Super Admin edits a price.
        var fraction = 0.8877m;
        var creditFromPaid = Prorate(480m, fraction);
        var creditFromList = Prorate(PayPalBillingService.GetAmount(Silver(), BillingPeriod.Annually), fraction);
        Assert.True(creditFromPaid > creditFromList);
        Assert.InRange(creditFromPaid - creditFromList, 70m, 72m);
    }

    // ---- Same-period changes: the cases the old code got right must stay right ----

    [Fact]
    public void Monthly_to_monthly_upgrade_math_is_unchanged()
    {
        // Silver monthly ($40 paid) -> Gold monthly, half the month left: charge half of Gold's
        // month, credit half of Silver's, pay the difference.
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var halfway = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        var fraction = PayPalBillingService.CalculateRemainingFraction(halfway, start, end);
        var credit = Prorate(40m, fraction);
        var charge = Prorate(PayPalBillingService.GetCycleEquivalentAmount(Gold(), BillingPeriod.Monthly), fraction);

        Assert.InRange(Math.Max(0, charge - credit), 9.5m, 10.5m); // ~half of the $20 difference
    }

    [Fact]
    public void Annual_to_annual_upgrade_prices_both_sides_in_annual_units()
    {
        var fraction = PayPalBillingService.CalculateRemainingFraction(UpgradeDay, CycleStart, CycleEnd);
        var credit = Prorate(480m, fraction);
        var charge = Prorate(PayPalBillingService.GetCycleEquivalentAmount(Gold(), BillingPeriod.Annually), fraction);
        // Same-period annual change happens to match the cross-period result because both use the
        // old cycle's units -- that is the whole point of the convention.
        Assert.InRange(Math.Max(0, charge - credit), 106m, 107m);
    }

    // ---- Guard rails around the helper itself ----

    [Fact]
    public void A_package_without_an_annual_price_still_costs_a_years_worth_for_an_annual_cycle()
    {
        var noAnnual = Gold();
        noAnnual.AnnualPrice = 0m;
        // 12 x monthly, never 0 -- a $0 charge here is exactly the shape of the original bug.
        Assert.Equal(720m, PayPalBillingService.GetCycleEquivalentAmount(noAnnual, BillingPeriod.Annually));
    }

    [Fact]
    public void Forfeited_credit_is_the_clamped_difference_never_negative_billing()
    {
        // Downgrade-priced-as-upgrade can't happen (IsUpgrade gates the branch), but a huge credit
        // against a small charge must clamp to $0 due -- the ToS trades the excess for in-kind
        // service, and the forfeited amount is credit - charge.
        var credit = 426.10m;
        var charge = 53.26m;
        Assert.Equal(0m, Math.Max(0, charge - credit));
        Assert.Equal(372.84m, Math.Max(0, credit - charge));
    }

    // ---- Deferred-start rows: the review-pass finding the first fix version missed ----
    // After an annual->monthly upgrade, the new row is Period=Monthly but its prepaid stretch
    // runs to the OLD paid-through date, months away. Cycle-fraction math clamped to 1 and sold
    // ~11 months of the next tier for one month's difference. The proration function must price
    // the ACTUAL remaining stretch.

    private static BillingRule Platinum() => new()
    {
        PackageName = "IPro Platinum",
        MonthlyPrice = 90m,
        AnnualPrice = 900m,
        PayPalMonthlyPlanId = "P-PLAT-M",
        PayPalAnnualPlanId = "P-PLAT-A"
    };

    [Fact]
    public void Second_upgrade_on_a_deferred_start_row_charges_the_full_remaining_stretch()
    {
        // The owner's account after the first fix: Gold monthly, Amount=$60, NextBillingDate
        // Jul 6 2027 (~10.6 months away). Upgrading to Platinum must charge the tier difference
        // for the WHOLE stretch, not for one clamped month.
        var later = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
        var (credit, charge) = PayPalBillingService.CalculateUpgradeProration(
            Gold(), Platinum(), BillingPeriod.Monthly, 60m, later, CycleEnd);

        var amountDue = Math.Max(0, charge - credit);
        // ~10.5 months x ($90 - $60) = ~$315. The bug produced ~$30.
        Assert.InRange(amountDue, 300m, 330m);
        Assert.True(credit > 600m);  // ~10.5 months of Gold at list, not one month's $60
    }

    [Fact]
    public void Normal_row_proration_still_uses_the_historical_amount_paid()
    {
        // A normal mid-cycle row must be untouched by the deferred-stretch branch: the owner's
        // original scenario still charges ~$106 credited from the $480 he actually paid.
        var (credit, charge) = PayPalBillingService.CalculateUpgradeProration(
            Silver(), Gold(), BillingPeriod.Annually, 480m, UpgradeDay, CycleEnd);
        Assert.InRange(credit, 426m, 427m);
        Assert.InRange(Math.Max(0, charge - credit), 106m, 107m);
    }

    [Fact]
    public void Legacy_zeroed_amount_derives_a_real_cycle_price_never_zero_credit()
    {
        // Quarterly-bug rows carry Amount=0 and Period=Quarterly with QuarterlyPrice=0; the
        // fallback must derive 3x monthly for the credit, not $0 (review pass).
        var quarterEnd = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
        var halfway = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        var (credit, charge) = PayPalBillingService.CalculateUpgradeProration(
            Silver(), Gold(), BillingPeriod.Quarterly, 0m, halfway, quarterEnd);
        Assert.True(credit > 0m);
        Assert.InRange(Math.Max(0, charge - credit), 27m, 33m); // ~half a quarter of the $60/quarter difference
    }

    // ---- NextBillingDate on activation / settled sale: keep only genuinely deferred dates ----

    [Fact]
    public void A_deferred_first_charge_date_beyond_one_period_is_preserved()
    {
        var now = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);
        var deferred = new DateTime(2027, 7, 6, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(deferred, PayPalBillingService.ResolveNextBillingDateOnPayment(deferred, now, BillingPeriod.Monthly));
    }

    [Fact]
    public void Fresh_signups_and_renewals_recompute_exactly_as_before()
    {
        var now = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);
        // Missing, past, or within one period -- all recompute to now + period.
        Assert.Equal(now.AddMonths(1), PayPalBillingService.ResolveNextBillingDateOnPayment(null, now, BillingPeriod.Monthly));
        Assert.Equal(now.AddMonths(1), PayPalBillingService.ResolveNextBillingDateOnPayment(now.AddDays(-3), now, BillingPeriod.Monthly));
        Assert.Equal(now.AddMonths(1), PayPalBillingService.ResolveNextBillingDateOnPayment(now.AddDays(20), now, BillingPeriod.Monthly));
        // An early renewal sale (stored date hours away) must ADVANCE the date, not freeze it.
        Assert.Equal(now.AddMonths(1), PayPalBillingService.ResolveNextBillingDateOnPayment(now.AddHours(12), now, BillingPeriod.Monthly));
    }

    // ---- Classification: which branch a change takes ----

    [Fact]
    public void Cross_period_tier_changes_classify_by_price_not_period()
    {
        Assert.True(PayPalBillingService.IsUpgrade(Silver(), Gold()));   // annual->monthly Silver->Gold is an UPGRADE
        Assert.False(PayPalBillingService.IsUpgrade(Gold(), Silver()));  // and the reverse is a downgrade
    }

    [Fact]
    public void Equal_priced_packages_are_not_upgrades()
    {
        var lateral = Silver();
        lateral.PackageName = "IPro Silver Legacy";
        Assert.False(PayPalBillingService.IsUpgrade(Silver(), lateral)); // takes the scheduled path
    }

    // ---- Date math the downgrade scheduler depends on ----

    [Fact]
    public void Cycle_start_winds_back_exactly_one_period()
    {
        Assert.Equal(CycleStart, PayPalBillingService.GetCurrentCycleStart(CycleEnd, BillingPeriod.Annually));
        Assert.Equal(new DateTime(2026, 8, 6), PayPalBillingService.GetCurrentCycleStart(new DateTime(2026, 9, 6), BillingPeriod.Monthly));
    }

    [Fact]
    public void Remaining_fraction_is_zero_at_or_after_the_boundary_and_clamped_to_one()
    {
        Assert.Equal(0m, PayPalBillingService.CalculateRemainingFraction(CycleEnd, CycleStart, CycleEnd));
        Assert.Equal(0m, PayPalBillingService.CalculateRemainingFraction(CycleEnd.AddDays(3), CycleStart, CycleEnd));
        Assert.Equal(1m, PayPalBillingService.CalculateRemainingFraction(CycleStart, CycleStart, CycleEnd));
    }
}
