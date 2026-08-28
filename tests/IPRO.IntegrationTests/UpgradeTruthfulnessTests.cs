using System;
using System.IO;
using IPRO.Billing;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// The 2026-08-28 live upgrade (the owner's own account: Silver-annual -> Gold-monthly-deferred ->
// Platinum-monthly). The MONEY was right at every layer -- DB, invoice math, and PayPal's own
// billing engine all agreed, verified against the real PayPal sandbox screens -- but the screens
// LIED about it: the invoice called a one-time prorated top-up a "monthly recurring subscription",
// and nothing anywhere explained the deferred start, so the owner read a correct charge as a
// billing bug. A paying customer would have too.
//
// This class pins three things: the exact proration branch that priced the live upgrade (a
// monthly row whose paid-through date sits many cycles out -- the shape chained deferred upgrades
// produce), the honest invoice label, and the wiring that actually uses it.
public class UpgradeTruthfulnessTests
{
    private static BillingRule Gold() => new() { PackageName = "IPro Gold", MonthlyPrice = 60m, AnnualPrice = 600m };
    private static BillingRule Platinum() => new() { PackageName = "IPro Platinum", MonthlyPrice = 90m, AnnualPrice = 900m };

    [Fact]
    public void The_live_upgrade_shape_prices_the_tier_difference_for_the_prepaid_stretch()
    {
        // The exact branch the live upgrade took: a MONTHLY row deferred to a paid-through date
        // ~10 months out, so `now` sits before the final cycle's start and the per-month fallback
        // prices the whole remaining stretch. Expected: the agent pays (90-60)/month for it --
        // the tier difference, nothing else. Clean UTC midnights make the numbers exact.
        var now = new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);
        var paidThrough = new DateTime(2027, 7, 6, 0, 0, 0, DateTimeKind.Utc);   // 312 days

        var (credit, charge) = PayPalBillingService.CalculateUpgradeProration(
            Gold(), Platinum(), BillingPeriod.Monthly,
            amountPaidForCycle: 60m, now, paidThrough);

        Assert.Equal(615.03m, credit);    // 60 x 312 / 30.4375
        Assert.Equal(922.55m, charge);    // 90 x 312 / 30.4375
        Assert.Equal(307.52m, charge - credit);   // the (90-60)-per-month difference, and nothing else
    }

    [Fact]
    public void An_ordinary_monthly_upgrade_still_prorates_inside_its_cycle()
    {
        // The other branch, pinned so the deferred fix can never bleed into the normal case: a
        // monthly row upgraded mid-cycle prorates by the fraction of THIS cycle remaining.
        var now = new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);
        var nextBilling = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);  // cycle Aug 12 - Sep 12

        var (credit, charge) = PayPalBillingService.CalculateUpgradeProration(
            Gold(), Platinum(), BillingPeriod.Monthly,
            amountPaidForCycle: 60m, now, nextBilling);

        Assert.Equal(29.03m, credit);     // 60 x 15/31
        Assert.Equal(43.55m, charge);     // 90 x 15/31
    }

    [Fact]
    public void The_upgrade_invoice_line_tells_the_truth()
    {
        var paidThrough = new DateTime(2027, 7, 6);

        // An upgrade's money line names what it is: one-time, prorated, and until when.
        Assert.Equal(
            $"Upgrade to IPro Platinum - one-time prorated difference through {paidThrough:MMMM d, yyyy}",
            PayPalBillingService.ChangeInvoiceRecurringLabel(
                SubscriptionChangeType.Upgrade, "IPro Platinum", BillingPeriod.Monthly, paidThrough));

        // A plain subscribe keeps the recurring wording -- there it is the truth.
        Assert.Equal("IPro Platinum monthly recurring subscription",
            PayPalBillingService.ChangeInvoiceRecurringLabel(
                SubscriptionChangeType.Subscribe, "IPro Platinum", BillingPeriod.Monthly, paidThrough));

        // An upgrade with no paid-through date (defensive) falls back to the recurring wording
        // rather than printing a hole.
        Assert.Equal("IPro Platinum monthly recurring subscription",
            PayPalBillingService.ChangeInvoiceRecurringLabel(
                SubscriptionChangeType.Upgrade, "IPro Platinum", BillingPeriod.Monthly, null));
    }

    [Fact]
    public void The_change_invoice_call_site_actually_uses_the_label()
    {
        // The C1 lesson: a truthful label nothing calls is worth nothing. BeginPaidChangeAsync's
        // invoice creation must route through ChangeInvoiceRecurringLabel.
        var source = File.ReadAllText(FindRepoFile(@"src\IPRO.Billing\PayPalBillingService.cs"));
        Assert.Contains(
            "recurringLineLabel: ChangeInvoiceRecurringLabel(changeType, requestedPackage.PackageName, period, billing.NextBillingDate)",
            source);
    }

    [Fact]
    public void The_billing_page_explains_a_deferred_start()
    {
        // The sentence whose absence made a correct charge read as a bug. Guarded to fire only
        // when the next charge sits beyond ~1.5 cycles -- an ordinary subscription never sees it.
        var razor = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Billing\Index.cshtml"));
        Assert.Contains("isDeferredStart", razor);
        Assert.Contains("already paid through", razor);
        Assert.Contains("cycleDays * 1.5", razor);
        Assert.Contains("your upgrade covered the difference", razor);
    }

    [Fact]
    public void The_image_library_says_which_blocks_it_feeds()
    {
        // Owner-found 2026-08-28, live on their own site: the page Image Library and an article's
        // cover image are unrelated stores that can hold identical-looking pictures. Removing the
        // library copy did nothing to the blog (correctly -- the block shows the ARTICLE's cover),
        // but no text anywhere said so, and a correct behaviour read as a bug. The captions now
        // name the boundary; this pins them so a rewrite cannot silently drop it.
        var razor = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\WebsitePages\Edit.cshtml"));
        Assert.Contains("this library does not affect them", razor);
        Assert.Contains("Nothing on this page uses library images yet", razor);
        Assert.Contains("the page Image Library above does not affect this block", razor);
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
