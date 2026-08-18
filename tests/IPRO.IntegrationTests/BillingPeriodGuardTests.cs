using IPRO.Billing;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// Regression suite for the 2026-08-14 ultra-audit finding that CreateSubscriptionAsync accepted any
// BillingPeriod straight from the POST body. Quarterly was the sharp edge: the Super Admin form
// forces QuarterlyPrice to 0 while the quarterly plan lookup returns the MONTHLY plan id, so a
// posted period=Quarterly produced a $0 subscription on a monthly plan -- no tax gross-up, and a
// zeroed Billing.Amount that poisoned every later proration. A period is sellable only when the
// package has BOTH a real price and a real PayPal plan for it.
public class BillingPeriodGuardTests
{
    private static BillingRule FullyConfiguredGold() => new()
    {
        PackageName = "IPro Gold",
        MonthlyPrice = 60m,
        AnnualPrice = 600m,
        QuarterlyPrice = 0m,               // never sold; the admin form forces this to zero
        PayPalMonthlyPlanId = "P-MONTHLY-REAL",
        PayPalAnnualPlanId = "P-ANNUAL-REAL"
    };

    [Fact]
    public void Monthly_and_annual_are_offerable_when_priced_and_synced()
    {
        var package = FullyConfiguredGold();
        Assert.True(PayPalBillingService.IsPeriodOfferable(package, BillingPeriod.Monthly));
        Assert.True(PayPalBillingService.IsPeriodOfferable(package, BillingPeriod.Annually));
    }

    [Fact]
    public void Quarterly_is_never_offerable_even_on_a_fully_synced_package()
    {
        Assert.False(PayPalBillingService.IsPeriodOfferable(FullyConfiguredGold(), BillingPeriod.Quarterly));
    }

    // A package's PayPal plans start empty and are synced by a manual Super Admin button. Until then
    // nobody may subscribe -- otherwise checkout degrades to a one-time order and the agent keeps the
    // package indefinitely for a single payment.
    [Fact]
    public void A_period_with_no_synced_plan_is_not_offerable()
    {
        var unsynced = FullyConfiguredGold();
        unsynced.PayPalMonthlyPlanId = string.Empty;
        unsynced.PayPalAnnualPlanId = string.Empty;

        Assert.False(PayPalBillingService.IsPeriodOfferable(unsynced, BillingPeriod.Monthly));
        Assert.False(PayPalBillingService.IsPeriodOfferable(unsynced, BillingPeriod.Annually));
    }

    [Fact]
    public void Annual_is_not_offerable_when_the_package_has_no_annual_price()
    {
        var monthlyOnly = FullyConfiguredGold();
        monthlyOnly.AnnualPrice = 0m;
        monthlyOnly.PayPalAnnualPlanId = string.Empty; // what SyncPayPalPlansAsync stores for a 0 price

        Assert.False(PayPalBillingService.IsPeriodOfferable(monthlyOnly, BillingPeriod.Annually));
        Assert.True(PayPalBillingService.IsPeriodOfferable(monthlyOnly, BillingPeriod.Monthly));
    }
}

// ADMIN-2 / BILLING-9 regression suite: a PayPal plan charges the price it was CREATED with — the
// snapshot columns record that frozen price at sync time, and editing the package price in Super
// Admin does not touch the plan. When they disagree, checkout must refuse (fail closed) instead of
// charging one number while invoicing another. The Packages screen's banner already warns; a
// warning an admin can ignore is not a guard.
public class PlanPriceDivergenceGuardTests
{
    private static BillingRule SyncedGold() => new()
    {
        PackageName = "IPro Gold",
        MonthlyPrice = 60m,
        AnnualPrice = 600m,
        PayPalMonthlyPlanId = "P-MONTHLY-REAL",
        PayPalAnnualPlanId = "P-ANNUAL-REAL",
        PayPalMonthlyPlanPrice = 60m,     // the snapshot SyncPayPalPlansAsync records
        PayPalAnnualPlanPrice = 600m
    };

    [Fact]
    public void A_freshly_synced_package_has_no_divergence()
    {
        var package = SyncedGold();
        Assert.False(PayPalBillingService.HasDivergentPlanPrice(package, BillingPeriod.Monthly));
        Assert.False(PayPalBillingService.HasDivergentPlanPrice(package, BillingPeriod.Annually));
    }

    [Fact]
    public void Editing_the_monthly_price_after_sync_is_a_divergence_on_monthly_only()
    {
        // The exact ADMIN-2 scenario: admin raises the price, forgets to re-sync. The plan at
        // PayPal still charges $60; the invoice would say $70.
        var package = SyncedGold();
        package.MonthlyPrice = 70m;

        Assert.True(PayPalBillingService.HasDivergentPlanPrice(package, BillingPeriod.Monthly));
        Assert.False(PayPalBillingService.HasDivergentPlanPrice(package, BillingPeriod.Annually));
    }

    [Fact]
    public void Editing_the_annual_price_after_sync_is_a_divergence_on_annual_only()
    {
        var package = SyncedGold();
        package.AnnualPrice = 700m;

        Assert.False(PayPalBillingService.HasDivergentPlanPrice(package, BillingPeriod.Monthly));
        Assert.True(PayPalBillingService.HasDivergentPlanPrice(package, BillingPeriod.Annually));
    }

    [Fact]
    public void A_plan_synced_before_the_snapshot_columns_existed_is_not_treated_as_divergent()
    {
        // Null snapshot = pre-422b sync. Divergence is UNKNOWN, not present — blocking here would
        // brick checkout for every legacy package until someone re-syncs, with no actual evidence
        // of a mismatch. The banner still nags for the re-sync that stamps the snapshot.
        var legacy = SyncedGold();
        legacy.PayPalMonthlyPlanPrice = null;
        legacy.PayPalAnnualPlanPrice = null;
        legacy.MonthlyPrice = 999m;

        Assert.False(PayPalBillingService.HasDivergentPlanPrice(legacy, BillingPeriod.Monthly));
        Assert.False(PayPalBillingService.HasDivergentPlanPrice(legacy, BillingPeriod.Annually));
    }
}
