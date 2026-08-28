using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// Owner decision 2026-08-28. Four features were sold in every package's comparison table with no
// implementation anywhere: rotating banner, newsboard, mail merge, printable label creator. They
// are withdrawn. A fifth, "Multilingual editor support", is kept but RENAMED -- the capability is
// real (an agent writes in any editor and pastes the content in; there is a live Farsi article
// doing exactly that) but it is not an editor we ship.
//
// The trap this class exists for: EnsureFeaturesAsync only ever ADDS missing rows and never
// re-syncs an existing row's name or IsIncluded. Fixing the definitions alone would clean up fresh
// installs and leave PRODUCTION selling all five forever -- which is precisely how the SMS-reminder
// claim survived its first fix. So the seeder change and the startup repair are both required, and
// both are pinned here.
public class WithdrawnFeatureTests
{
    private static readonly string[] Withdrawn =
        { "rotating_banner", "newsboard", "mail_merge", "printable_label_creator", "framed_link_manager",
          "managed_seo", "designated_support" };

    [Fact]
    public async Task Withdrawn_features_are_deleted_from_a_database_that_already_sells_them()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // A database in the pre-decision state: every withdrawn feature present and ticked.
        var rule = new BillingRule { PackageName = $"WF-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        foreach (var code in Withdrawn)
        {
            db.Add(new PackageFeature
            {
                BillingRuleId = rule.Id, FeatureCode = code, FeatureName = "Sold but never built",
                IsIncluded = true, SortOrder = 100
            });
        }
        db.Add(new PackageFeature
        {
            BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.MultilingualEditor,
            FeatureName = "Multilingual editor support", IsIncluded = true, SortOrder = 340
        });
        db.Add(new PackageFeature
        {
            BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.CustomHomeButtons,
            FeatureName = "Create custom buttons on home page", IsIncluded = true, SortOrder = 200
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await PackageEntitlementSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        // Deleted, not merely un-ticked: the comparison table renders one row per PackageFeature
        // that EXISTS, so an un-ticked row would still advertise the name against every plan.
        foreach (var code in Withdrawn)
        {
            Assert.False(await db.PackageFeatures.AnyAsync(f => f.FeatureCode == code),
                $"'{code}' is still in the package data and will render as a row on the pricing page");
        }

        // The kept one is renamed to what actually happens.
        var multilingual = await db.PackageFeatures.AsNoTracking()
            .Where(f => f.FeatureCode == PackageFeatureCodes.MultilingualEditor).ToListAsync();
        Assert.NotEmpty(multilingual);
        Assert.All(multilingual, f => Assert.Equal(
            "Supports multilingual content (paste from any editor)", f.FeatureName));

        // Same treatment for the other real-but-misnamed one: the CallToAction block carries the
        // agent's own button text and link, on any page -- not just the home page.
        var cta = await db.PackageFeatures.AsNoTracking()
            .Where(f => f.FeatureCode == PackageFeatureCodes.CustomHomeButtons).ToListAsync();
        Assert.NotEmpty(cta);
        Assert.All(cta, f => Assert.Equal(
            "Call-to-action sections with your own button text and link", f.FeatureName));
    }

    [Fact]
    public async Task The_repair_is_idempotent_and_does_not_touch_other_features()
    {
        // It runs on EVERY startup. A second pass must be a no-op, and it must never reach a
        // feature that is not on the withdrawn list.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var rule = new BillingRule { PackageName = $"WF-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature
        {
            BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.Newsletters,
            FeatureName = "Create and send newsletters", IsIncluded = true, SortOrder = 10
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await PackageEntitlementSeeder.SeedAsync(db);
        await PackageEntitlementSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        var newsletters = await db.PackageFeatures.AsNoTracking()
            .SingleAsync(f => f.BillingRuleId == rule.Id && f.FeatureCode == PackageFeatureCodes.Newsletters);
        Assert.True(newsletters.IsIncluded);
        Assert.Equal("Create and send newsletters", newsletters.FeatureName);
    }

    [Fact]
    public void The_withdrawn_codes_are_gone_from_the_definitions_so_they_cannot_come_back()
    {
        // Deleting the rows is only half of it -- if the seeder still defined them, the very next
        // startup would re-add every one. Both halves, or neither works.
        var seeder = File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\PackageEntitlementSeeder.cs"));
        var definitions = seeder[..seeder.IndexOf("RetiredFeatureCodes", StringComparison.Ordinal)];

        foreach (var name in new[] { "RotatingBanner", "Newsboard", "MailMerge", "PrintableLabelCreator", "FramedLinkManager", "ManagedSeo", "DesignatedSupport" })
        {
            Assert.DoesNotContain($"PackageFeatureCodes.{name}", definitions);
        }

        // And the constants themselves are retired, so a future edit cannot casually re-add a
        // Feature() line for one of them.
        var codes = File.ReadAllText(FindRepoFile(@"src\IPRO.Entities\PackageFeatureCodes.cs"));
        foreach (var name in new[] { "RotatingBanner", "Newsboard", "MailMerge", "PrintableLabelCreator", "FramedLinkManager", "ManagedSeo", "DesignatedSupport" })
        {
            Assert.DoesNotContain($"public const string {name} ", codes);
        }
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
