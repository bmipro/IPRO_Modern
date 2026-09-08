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

        var prospects = await db.PackageFeatures.AsNoTracking().Where(f => f.FeatureCode == PackageFeatureCodes.ProspectManager).ToListAsync();
        Assert.NotEmpty(prospects);
        Assert.All(prospects, f => Assert.Equal("Website leads inbox (prospect manager)", f.FeatureName));

        var social = await db.PackageFeatures.AsNoTracking().Where(f => f.FeatureCode == PackageFeatureCodes.SocialMediaIntegration).ToListAsync();
        Assert.NotEmpty(social);
        Assert.All(social, f => Assert.Equal("Social posts: draft, check platform limits, and track", f.FeatureName));
    }

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
