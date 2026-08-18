using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// JOBS-1 (the last CRITICAL): an opted-out client could still be ENROLLED into a drip campaign.
// The job cancelled the enrollment at its first due send, so no mail went — but the campaign
// screen showed a running enrollment for someone who asked to be left alone, for up to the first
// step's full delay, and the agent was never told. Three layers close it, tested here:
//   1. enrollment refuses suppressed clients, with agent-visible feedback;
//   2. SuppressAllAsync cancels active enrollments at the moment of a NEW opt-out (LB-2 —
//      re-pinned here so JOBS-1's fix cannot regress it);
//   3. the job's truth sweep cancels enrollments whose client opted out BEFORE LB-2 existed,
//      instead of leaving them "Active" until a send comes due.
public class DripEnrollmentConsentTests
{
    // ------------------------------------------------------------------ 1. the enrollment gate --

    [Fact]
    public async Task Enrolling_a_suppressed_client_is_refused_and_the_agent_is_told()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedCampaignAsync(db);
        var optedOut = await SeedClientAsync(db, seed.AgentId, "optout", suppressed: true);

        var controller = NewController(db, seed.AgentId);
        await controller.EnrollClient(seed.CampaignId, optedOut.Id);

        Assert.Equal(0, await db.DripCampaignEnrollments.CountAsync(e => e.ClientId == optedOut.Id));
        Assert.Contains("unsubscribed", controller.TempData["Warning"]?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enrolling_a_subscribed_client_still_works()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedCampaignAsync(db);
        var subscribed = await SeedClientAsync(db, seed.AgentId, "clean", suppressed: false);

        var controller = NewController(db, seed.AgentId);
        await controller.EnrollClient(seed.CampaignId, subscribed.Id);

        Assert.Equal(1, await db.DripCampaignEnrollments.CountAsync(
            e => e.ClientId == subscribed.Id && e.Status == DripCampaignEnrollmentStatus.Active));
    }

    // ------------------------------------------- 2. a NEW opt-out cancels the live enrollment --

    [Fact]
    public async Task An_opt_out_cancels_the_clients_active_enrollments_immediately()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedCampaignAsync(db);
        var client = await SeedClientAsync(db, seed.AgentId, "enrolled", suppressed: false);
        await EnrollDirectlyAsync(db, seed, client, nextSendDaysAway: 21);

        await NewConsent(db).SuppressAllAsync(client, "test:preferences-page");

        db.ChangeTracker.Clear();
        var enrollment = await db.DripCampaignEnrollments.AsNoTracking().SingleAsync(e => e.ClientId == client.Id);
        Assert.Equal(DripCampaignEnrollmentStatus.Cancelled, enrollment.Status);
    }

    // --------------------------------------------------------------- 3. the job's truth sweep --

    [Fact]
    public async Task The_sweep_cancels_enrollments_of_already_suppressed_clients_and_spares_the_rest()
    {
        // The legacy shape: the client opted out BEFORE SuppressAllAsync learned to cancel
        // enrollments, so an Active row survived with its next send weeks away. The sweep must
        // cancel exactly that row and not touch the subscribed client's.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var seed = await SeedCampaignAsync(db);

        var legacyOptOut = await SeedClientAsync(db, seed.AgentId, "legacy", suppressed: true);
        var subscribed = await SeedClientAsync(db, seed.AgentId, "active", suppressed: false);
        await EnrollDirectlyAsync(db, seed, legacyOptOut, nextSendDaysAway: 21);
        await EnrollDirectlyAsync(db, seed, subscribed, nextSendDaysAway: 21);
        db.ChangeTracker.Clear();

        var cancelled = await NewConsent(db).CancelSuppressedDripEnrollmentsAsync();

        Assert.Equal(1, cancelled);
        var rows = await db.DripCampaignEnrollments.AsNoTracking().ToListAsync();
        Assert.Equal(DripCampaignEnrollmentStatus.Cancelled,
            rows.Single(e => e.ClientId == legacyOptOut.Id).Status);
        Assert.Equal(DripCampaignEnrollmentStatus.Active,
            rows.Single(e => e.ClientId == subscribed.Id).Status);
    }

    [Fact]
    public async Task The_sweep_is_a_noop_when_nothing_needs_cancelling()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        Assert.Equal(0, await NewConsent(db).CancelSuppressedDripEnrollmentsAsync());
    }

    // ------------------------------------------------------------------------------- plumbing --

    private static EmailConsentService NewConsent(IPRODbContext db) =>
        new(db, new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());

    private static CampaignsController NewController(IPRODbContext db, int agentId)
    {
        var controller = new CampaignsController(db, new GrantAllEntitlements(), NewConsent(db));
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            context, new DiscardingTempDataProvider());
        return controller;
    }

    private sealed record CampaignSeed(int AgentId, int CampaignId);

    private static async Task<CampaignSeed> SeedCampaignAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"drip-{Guid.NewGuid():N}"[..20],
            Email = "drip.agent@example.com",
            FirstName = "Drip",
            LastName = "Agent",
            DomainName = $"drip-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var campaign = new DripCampaign { AgentUserId = agent.Id, Name = "Welcome series", IsActive = true };
        db.Add(campaign);
        await db.SaveChangesAsync();
        db.Add(new DripCampaignStep
        {
            DripCampaignId = campaign.Id,
            SortOrder = 0,
            Subject = "Step 1",
            HtmlBody = "<p>Hello</p>",
            DelayDays = 21
        });
        await db.SaveChangesAsync();
        return new CampaignSeed(agent.Id, campaign.Id);
    }

    private static async Task<Client> SeedClientAsync(IPRODbContext db, int agentId, string tag, bool suppressed)
    {
        var client = new Client
        {
            AgentUserId = agentId,
            FirstName = tag,
            LastName = "Consent",
            Email = $"{tag}.{Guid.NewGuid():N}"[..24] + "@example.com",
            IsNewsletterSubscribed = !suppressed,
            EmailOptOutAt = suppressed ? DateTime.UtcNow.AddDays(-30) : null
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task EnrollDirectlyAsync(IPRODbContext db, CampaignSeed seed, Client client, int nextSendDaysAway)
    {
        db.Add(new DripCampaignEnrollment
        {
            AgentUserId = seed.AgentId,
            DripCampaignId = seed.CampaignId,
            ClientId = client.Id,
            Status = DripCampaignEnrollmentStatus.Active,
            NextStepIndex = 0,
            StartedAt = DateTime.UtcNow,
            NextSendAt = DateTime.UtcNow.AddDays(nextSendDaysAway),
            UnsubscribeToken = Guid.NewGuid().ToString("N")
        });
        await db.SaveChangesAsync();
    }

    private sealed class GrantAllEntitlements : IPackageEntitlementService
    {
        public Task<PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) =>
            throw new NotSupportedException();
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class DiscardingTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
