using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// H3 (launch runway Phase 1, 2026-08-27). IsAccessGatedAsync and HasAccessBulkAsync were updated
// for PaidThroughAt when DOCS/22 shipped; ResolveBillingRuleIdAsync — the singular path behind
// GetAccessAsync, which nearly every controller calls — was not. It matches Status == Active only,
// then falls through to AgentUser.PackageId, a value that goes stale the moment a plan changes
// without that column being rewritten. Same agent, two package answers.
//
// The two resolvers carry comments demanding they "stay logically identical". Nothing enforced it.
// These tests do.
public class ResolverAgreementTests
{
    private const string GoldOnly = "resolver_gold_only";

    [Fact]
    public async Task H3_a_cancelled_but_paid_through_agent_gets_the_package_they_paid_for()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        // The shape DOCS/22 created and the singular resolver never learned: billing cancelled,
        // access owned until PaidThroughAt, and an AgentUser.PackageId still pointing at the
        // OLD package because nothing rewrites that column on a plan change.
        var seed = await SeedPaidThroughAsync(db, staleColumnPointsAtSilver: true);
        var svc = new PackageEntitlementService(new UnitOfWork(db), db);

        var access = await svc.GetAccessAsync(seed.AgentId, GoldOnly);

        // Pre-fix: the singular path returned Silver (the stale column) and refused a feature the
        // agent had actually paid for and was still inside the paid period of.
        Assert.True(access.IsIncluded,
            "the agent paid for Gold and their paid-through period has not ended — GetAccessAsync must say yes");
    }

    [Fact]
    public async Task H3_the_singular_and_bulk_resolvers_agree_on_a_paid_through_agent()
    {
        // The invariant the two comments promise each other. This is the test whose absence let
        // them drift: OverdueInvoiceReminderJob switched between them and silently changed answer.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedPaidThroughAsync(db, staleColumnPointsAtSilver: true);
        var svc = new PackageEntitlementService(new UnitOfWork(db), db);

        var singular = (await svc.GetAccessAsync(seed.AgentId, GoldOnly)).IsIncluded;
        var bulk = (await svc.HasAccessBulkAsync(new[] { seed.AgentId }, GoldOnly))[seed.AgentId];

        Assert.Equal(bulk, singular);
        Assert.True(singular);
    }

    [Fact]
    public async Task H3_the_two_resolvers_agree_once_the_paid_through_date_passes()
    {
        // The other side of the same invariant: when the paid period really has ended, BOTH must
        // withdraw access. A fix that made the singular path permissive in the wrong direction
        // would hand out a package nobody is paying for.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedPaidThroughAsync(db, staleColumnPointsAtSilver: true);

        var billing = await db.Billings.SingleAsync(b => b.Id == seed.BillingId);
        billing.PaidThroughAt = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var svc = new PackageEntitlementService(new UnitOfWork(db), db);
        var singular = (await svc.GetAccessAsync(seed.AgentId, GoldOnly)).IsIncluded;
        var bulk = (await svc.HasAccessBulkAsync(new[] { seed.AgentId }, GoldOnly))[seed.AgentId];

        Assert.Equal(bulk, singular);
        Assert.False(singular, "the paid period is over — neither resolver may still grant the package");
    }

    [Fact]
    public async Task H3_an_active_billing_still_wins_over_a_stale_package_column()
    {
        // Regression pin for the ordinary case: an ACTIVE row has always been authoritative and
        // must stay that way. If this broke, every paying customer would resolve to whatever
        // AgentUser.PackageId happened to hold.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedPaidThroughAsync(db, staleColumnPointsAtSilver: true, status: BillingStatus.Active,
            paidThrough: null);

        var svc = new PackageEntitlementService(new UnitOfWork(db), db);
        var singular = (await svc.GetAccessAsync(seed.AgentId, GoldOnly)).IsIncluded;
        var bulk = (await svc.HasAccessBulkAsync(new[] { seed.AgentId }, GoldOnly))[seed.AgentId];

        Assert.Equal(bulk, singular);
        Assert.True(singular);
    }

    [Fact]
    public async Task H3_an_expired_row_with_paid_through_time_agrees_too()
    {
        // Wave-2 E taught the gates to honour Expired rows alongside Cancelled. The resolvers must
        // read the same set, or the gate lets an agent in and the resolver hands them nothing.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedPaidThroughAsync(db, staleColumnPointsAtSilver: true, status: BillingStatus.Expired);

        var svc = new PackageEntitlementService(new UnitOfWork(db), db);
        var singular = (await svc.GetAccessAsync(seed.AgentId, GoldOnly)).IsIncluded;
        var bulk = (await svc.HasAccessBulkAsync(new[] { seed.AgentId }, GoldOnly))[seed.AgentId];

        Assert.Equal(bulk, singular);
        Assert.True(singular);
    }

    // ------------------------------------------------------------------------------ plumbing --

    private sealed record Seed(int AgentId, int BillingId);

    private static async Task<Seed> SeedPaidThroughAsync(
        IPRODbContext db, bool staleColumnPointsAtSilver,
        BillingStatus status = BillingStatus.Cancelled,
        DateTime? paidThrough = default)
    {
        if (paidThrough == default) paidThrough = DateTime.UtcNow.AddDays(20);

        var silver = new BillingRule { PackageName = $"RS-{Guid.NewGuid():N}"[..20], MonthlyPrice = 40m, AnnualPrice = 400m };
        var gold = new BillingRule { PackageName = $"RG-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m, AnnualPrice = 600m };
        db.AddRange(silver, gold);
        await db.SaveChangesAsync();

        // The feature that separates them: Gold includes it, Silver explicitly does not.
        db.Add(new PackageFeature { BillingRuleId = gold.Id, FeatureCode = GoldOnly, FeatureName = "Gold only", IsIncluded = true });
        db.Add(new PackageFeature { BillingRuleId = silver.Id, FeatureCode = GoldOnly, FeatureName = "Gold only", IsIncluded = false });
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"rs-{Guid.NewGuid():N}"[..20],
            Email = $"rs-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Resolver", LastName = "Split",
            DomainName = $"rs-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario",
            // The stale column: the agent moved to Gold, this was never rewritten.
            PackageId = staleColumnPointsAtSilver ? silver.Id : gold.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id,
            BillingRuleId = gold.Id,          // what they actually paid for
            Amount = 60m,
            Status = status,
            Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-40),
            NextBillingDate = DateTime.UtcNow.AddDays(20),
            PaidThroughAt = status == BillingStatus.Active ? null : paidThrough,
            CancelledAt = status == BillingStatus.Active ? null : DateTime.UtcNow.AddDays(-1)
        };
        db.Add(billing);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(agent.Id, billing.Id);
    }
}
