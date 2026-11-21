using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 641 (418b's last piece, 2026-11-20): "the entitlements match the packages" was the one money-path check never
// written as a test of its own. The pricing page and SuperAdmin's package screen show the PackageFeatures rows; the
// portal decides through PackageEntitlementService. Here the four packages are seeded exactly as a new production
// database is (PackageEntitlementSeeder), an adviser is put on each, and for every line of the seeder's catalogue
// the service must answer what the catalogue says: included or not, and the same limit. And every package line the
// code asks about by name must be a line of the catalogue, or a package would answer "not included" for a feature
// it sells.
public class EntitlementsMatchPackages641Tests
{
    [Fact]
    public async Task Every_package_grants_exactly_what_its_catalogue_lines_say()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await PackageEntitlementSeeder.SeedAsync(db);
        var catalogue = Catalogue();
        var packages = await db.BillingRules.AsNoTracking().Where(p => catalogue.Select(c => c.Package).Contains(p.PackageName)).ToListAsync();
        Assert.Equal(4, packages.Count);

        var service = new PackageEntitlementService(new UnitOfWork(db), db);
        var wrong = new List<string>();
        foreach (var package in packages)
        {
            var agentId = await AdviserOnAsync(db, package);
            foreach (var line in catalogue.Where(c => c.Package == package.PackageName))
            {
                var access = await service.GetAccessAsync(agentId, line.Code);
                if (access.IsIncluded != line.Included)
                    wrong.Add($"{package.PackageName} / {line.Code}: catalogue says {(line.Included ? "included" : "not included")}, the portal says {(access.IsIncluded ? "included" : "not included")}");
                else if (line.Included && line.Limit != null && access.LimitValue != line.Limit)
                    wrong.Add($"{package.PackageName} / {line.Code}: catalogue limit {line.Limit}, the portal's {access.LimitValue}");
                if (await service.HasAccessAsync(agentId, line.Code) != line.Included)
                    wrong.Add($"{package.PackageName} / {line.Code}: HasAccessAsync disagrees with the catalogue");
            }
        }
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void Every_package_line_the_code_asks_about_is_a_line_of_the_catalogue()
    {
        var catalogued = Catalogue().Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        var codes = typeof(PackageFeatureCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);
        var asked = new SortedSet<string>(StringComparer.Ordinal);
        var call = new Regex(@"(?:GetAccessAsync|HasAccessAsync|HasAccessBulkAsync)\([^;]*?PackageFeatureCodes\.([A-Za-z]+)");
        foreach (var file in Directory.GetFiles(Path.Combine(Root(), "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".cshtml", StringComparison.Ordinal))
                                 && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                 && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            foreach (Match m in call.Matches(File.ReadAllText(file)))
                asked.Add(m.Groups[1].Value);
        }
        Assert.NotEmpty(asked);
        var missing = asked.Where(name => !codes.TryGetValue(name, out var code) || !catalogued.Contains(code)).ToList();
        Assert.True(missing.Count == 0, "asked about but not in the catalogue: " + string.Join(", ", missing));
    }

    // ---- the seeder's own catalogue, read as it is (no copy to drift) -----------------------------------

    private sealed record Line(string Package, string Code, bool Included, int? Limit);

    private static List<Line> Catalogue()
    {
        var build = typeof(PackageEntitlementSeeder).GetMethod("BuildFeatureDefinitions", BindingFlags.NonPublic | BindingFlags.Static)!;
        var lines = new List<Line>();
        foreach (var definition in (IEnumerable)build.Invoke(null, null)!)
        {
            var type = definition.GetType();
            var code = (string)type.GetProperty("Code")!.GetValue(definition)!;
            var values = (IEnumerable)type.GetProperty("Values")!.GetValue(definition)!;
            foreach (var entry in values)
            {
                var entryType = entry.GetType();
                var package = (string)entryType.GetProperty("Key")!.GetValue(entry)!;
                var value = entryType.GetProperty("Value")!.GetValue(entry)!;
                var valueType = value.GetType();
                lines.Add(new Line(package, code,
                    (bool)valueType.GetProperty("IsIncluded")!.GetValue(value)!,
                    (int?)valueType.GetProperty("LimitValue")!.GetValue(value)));
            }
        }
        return lines;
    }

    private static async Task<int> AdviserOnAsync(IPRODbContext db, BillingRule package)
    {
        var agent = new AgentUser
        {
            UserName = $"em-{Guid.NewGuid():N}"[..20], Email = $"em-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Plan", LastName = "Check", DomainName = $"em-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", PackageId = package.Id, TrialEndsAt = null
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = package.Id, Amount = package.MonthlyPrice, Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-10), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return agent.Id;
    }

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
