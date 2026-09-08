using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// Owner decision 2026-09-08 (TODO 464), looking at the public package table: four rows describe
// things the product does not offer and had survived every audit -- "Coupon manager", "Need
// analysis calculator", "Did you know manager", "Get a quote form with email function". None of
// their feature codes is checked anywhere in the code. They are WITHDRAWN the same way as the
// 28 August round: gone from the definitions, deleted from any database that already has them.
//
// Two more rows are real features sold under names nobody recognises and are RENAMED: the
// "Prospect manager" is the Website Leads inbox, and "Social media integration" is Social Posts.
public class RetiredFeaturesSeptemberTests
{
    private static readonly string[] Withdrawn =
        { "coupon_manager", "needs_analysis_calculator", "did_you_know_manager", "quote_form" };

    [Fact]
    public async Task The_four_withdrawn_rows_are_deleted_from_a_database_that_already_sells_them()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var rule = new BillingRule { PackageName = $"RF-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.Add(rule);
        await db.SaveChangesAsync();
        foreach (var code in Withdrawn)
        {
            db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = code, FeatureName = "Sold but not offered", IsIncluded = true, SortOrder = 200 });
        }
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.ProspectManager, FeatureName = "Prospect manager", IsIncluded = true, SortOrder = 350 });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.SocialMediaIntegration, FeatureName = "Social media integration", IsIncluded = true, SortOrder = 300 });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.MenuCreator, FeatureName = "Menu and sub-menu creator", IsIncluded = true, SortOrder = 320 });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.SeoTool, FeatureName = "Built-in SEO tool", IsIncluded = true, SortOrder = 220 });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.EmailTracking, FeatureName = "Email report and tracking system", IsIncluded = true, SortOrder = 270 });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.MultilingualEditor, FeatureName = "Supports multilingual content (paste from any editor)", IsIncluded = true, SortOrder = 340 });
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.VisitorTracking, FeatureName = "Detailed visitor/hits tracking system", IsIncluded = true, SortOrder = 280 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await PackageEntitlementSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        // Deleted, not un-ticked: the comparison table renders one row per PackageFeature that
        // exists, so an un-ticked row would still advertise the name against every plan.
        foreach (var code in Withdrawn)
        {
            Assert.False(await db.PackageFeatures.AnyAsync(f => f.FeatureCode == code),
                $"'{code}' is still in the package data and will render as a row on the pricing page");
        }

        foreach (var (code, name) in ShortNames)
        {
            var rows = await db.PackageFeatures.AsNoTracking().Where(f => f.FeatureCode == code).ToListAsync();
            Assert.NotEmpty(rows);
            Assert.All(rows, f => Assert.Equal(name, f.FeatureName));
        }
    }

    // Owner, 2026-09-08: the feature list wants three to five words a row, like the rows around
    // them ("Create and send newsletters", "Poll and survey builder"). The detail lives in the
    // help guides. Applied to every existing row, so production and fresh installs agree.
    private static readonly (string Code, string Name)[] ShortNames =
    {
        (PackageFeatureCodes.ProspectManager, "Website leads inbox"),
        (PackageFeatureCodes.SocialMediaIntegration, "Social posts: draft and track"),
        (PackageFeatureCodes.MenuCreator, "Website menu editor (3 levels)"),
        (PackageFeatureCodes.SeoTool, "Built-in SEO and sitemap"),
        (PackageFeatureCodes.EmailTracking, "Email delivery tracking"),
        (PackageFeatureCodes.MultilingualEditor, "Content in any language"),
        (PackageFeatureCodes.VisitorTracking, "Website analytics"),
    };

    [Fact]
    public async Task A_fresh_database_never_gets_the_four_rows()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        await PackageEntitlementSeeder.SeedAsync(db);

        foreach (var code in Withdrawn)
        {
            Assert.False(await db.PackageFeatures.AnyAsync(f => f.FeatureCode == code), $"'{code}' was seeded on a fresh database");
        }
    }

    [Fact]
    public void The_withdrawn_codes_are_gone_from_the_definitions_and_the_constants()
    {
        // Deleting the rows is only half of it -- if the seeder still defined them, the very next
        // startup would re-add every one. And with the constants gone, a Feature() line for one of
        // them no longer compiles.
        var seeder = File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\PackageEntitlementSeeder.cs"));
        var definitions = seeder[..seeder.IndexOf("RetiredFeatureCodes", StringComparison.Ordinal)];
        foreach (var name in new[] { "CouponManager", "NeedsAnalysisCalculator", "DidYouKnowManager", "QuoteForm" })
        {
            Assert.DoesNotContain($"PackageFeatureCodes.{name}", definitions);
        }
        foreach (var code in Withdrawn)
        {
            Assert.Contains($"\"{code}\"", seeder[seeder.IndexOf("RetiredFeatureCodes", StringComparison.Ordinal)..]);
        }

        var constants = File.ReadAllText(FindRepoFile(@"src\IPRO.Entities\PackageFeatureCodes.cs"));
        foreach (var code in Withdrawn)
        {
            Assert.DoesNotContain($"\"{code}\"", constants);
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
