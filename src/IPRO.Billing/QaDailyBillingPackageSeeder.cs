using System;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Billing;

// Clones Silver/Gold/Platinum into hidden (IsHiddenTestPackage) sandbox packages billed every
// 1 day instead of monthly, so a manual buyer-pass test can observe PayPal's own unattended
// renewal engine firing a real charge without waiting out a real monthly cycle. Guarded by
// SeedGuard like every other startup seeder that runs on both apps against the same database.
// Sandbox-only by construction: SyncDailyTestPlanAsync refuses outside PayPal__IsSandbox, and this
// seeder itself no-ops on live so a live deploy never accumulates dead hidden package rows. Lives
// in IPRO.Billing rather than IPRO.DataAccess (unlike its sibling seeders) because it needs
// IBillingService for the PayPal plan-creation call, and DataAccess cannot reference Billing.
public static class QaDailyBillingPackageSeeder
{
    private static readonly (string SourceName, string QaName)[] Tiers =
    {
        ("IPro Silver", "QA Silver (Daily)"),
        ("IPro Gold", "QA Gold (Daily)"),
        ("IPro Platinum", "QA Platinum (Daily)")
    };

    public static async Task SeedAsync(IPRODbContext db, IBillingService billing, bool isSandbox, ILogger? logger = null)
    {
        if (!isSandbox)
        {
            return;
        }

        await SeedGuard.RunAsync(db, "QaDailyBillingPackages", logger, () => SeedCoreAsync(db, billing, logger));
    }

    private static async Task SeedCoreAsync(IPRODbContext db, IBillingService billing, ILogger? logger)
    {
        foreach (var (sourceName, qaName) in Tiers)
        {
            var existing = await db.BillingRules.FirstOrDefaultAsync(p => p.PackageName == qaName);
            if (existing != null)
            {
                continue;
            }

            var source = await db.BillingRules
                .Include(p => p.Features)
                .FirstOrDefaultAsync(p => p.PackageName == sourceName);
            if (source == null)
            {
                logger?.LogWarning("QA daily test package skipped: source package '{Source}' not found.", sourceName);
                continue;
            }

            var qa = new BillingRule
            {
                PackageName = qaName,
                Description = $"QA-only sandbox clone of {sourceName}, billed every 1 day instead of monthly. Never shown to real agents.",
                MonthlyPrice = source.MonthlyPrice,
                SetupFee = source.SetupFee,
                MaxClients = source.MaxClients,
                MaxNewsletters = source.MaxNewsletters,
                DefaultWebsiteTemplateId = source.DefaultWebsiteTemplateId,
                IsActive = true,
                IsHiddenTestPackage = true,
                IsTrialPackage = false,
                CreatedAt = DateTime.UtcNow
            };
            await db.BillingRules.AddAsync(qa);
            await db.SaveChangesAsync();

            foreach (var feature in source.Features)
            {
                await db.PackageFeatures.AddAsync(new PackageFeature
                {
                    BillingRuleId = qa.Id,
                    FeatureCode = feature.FeatureCode,
                    FeatureName = feature.FeatureName,
                    IsIncluded = feature.IsIncluded,
                    LimitValue = feature.LimitValue,
                    LimitLabel = feature.LimitLabel,
                    SortOrder = feature.SortOrder,
                    CreatedAt = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync();

            var syncResult = await billing.SyncDailyTestPlanAsync(qa.Id);
            if (!syncResult.Success)
            {
                logger?.LogWarning(
                    "QA daily test package '{Name}' (id {Id}) was created but its PayPal plan sync failed: {Message}",
                    qaName, qa.Id, syncResult.Message);
            }
            else
            {
                logger?.LogInformation(
                    "QA daily test package '{Name}' (id {Id}) ready with PayPal DAY-frequency plan {PlanId}.",
                    qaName, qa.Id, syncResult.MonthlyPlanId);
            }
        }
    }
}
