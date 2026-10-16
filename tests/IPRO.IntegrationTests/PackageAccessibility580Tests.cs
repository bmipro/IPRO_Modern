using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 580 (2026-10-15): the package comparison says what 578 and 579 gave every website. A catalogue line
// the owner asked for, worded and placed by him: "Accessible to visitors with disabilities", second in
// the table so the collapsed comparison (six rows) shows it, included on every package.
public class PackageAccessibility580Tests
{
    [Fact]
    public void The_line_is_wired()
    {
        Assert.Contains("Accessibility = \"accessibility\"", Read(@"src\IPRO.Entities\PackageFeatureCodes.cs"));
        Assert.Contains("Feature(15, PackageFeatureCodes.Accessibility, \"Accessible to visitors with disabilities\", all, all, all, all),",
            Read(@"src\IPRO.DataAccess\PackageEntitlementSeeder.cs"));

        // The owner's choice: the short views show one more line, so the new line pushes nothing off
        // (Silver's "Contacts (500)" and the e-letters line stay on the landing cards; the home table
        // collapses at seven rows, HomepagePricingCollapseTests).
        Assert.Contains(".OrderBy(f => f.SortOrder).Take(7).ToList();", Read(@"src\IPRO.Web\Views\Home\_LandingPricing.cshtml"));
    }

    [Fact]
    public async Task The_catalogue_seeds_it_included_on_every_package_once_and_second_in_order()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        await PackageEntitlementSeeder.SeedAsync(db);
        var rows = await db.PackageFeatures.AsNoTracking().Where(f => f.FeatureCode == PackageFeatureCodes.Accessibility).ToListAsync();
        Assert.Equal(4, rows.Count);                                   // Silver, Gold, Platinum, Broker
        Assert.All(rows, r => Assert.True(r.IsIncluded));
        Assert.All(rows, r => Assert.Equal("Accessible to visitors with disabilities", r.FeatureName));

        // Second on every package: after the website line, before everything else.
        foreach (var package in rows.Select(r => r.BillingRuleId))
        {
            var order = await db.PackageFeatures.AsNoTracking().Where(f => f.BillingRuleId == package)
                .OrderBy(f => f.SortOrder).Select(f => f.FeatureCode).Take(2).ToListAsync();
            Assert.Equal(new[] { PackageFeatureCodes.InstantWebsite, PackageFeatureCodes.Accessibility }, order);
        }

        await PackageEntitlementSeeder.SeedAsync(db);                  // the next start-up adds nothing twice
        Assert.Equal(4, await db.PackageFeatures.CountAsync(f => f.FeatureCode == PackageFeatureCodes.Accessibility));
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
