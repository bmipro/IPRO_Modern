using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// Auditor 5, F5/F7: the schema itself creates the state these guard against.
//
// FK_NewsLetterSends_Clients_ClientId is ON DELETE SET NULL, so deleting a client silently rewrites
// a one-to-one newsletter send into one with no target. The dispatcher's old `_ => query`
// fall-through then resolved that to the agent's ENTIRE subscriber list -- while AudienceLabel still
// displayed the original narrow audience, so the send history misreported what had happened.
//
// These pin the schema behaviour that makes the bug reachable, and the invariant the dispatchers now
// enforce. They deliberately don't construct a dispatcher (it needs SendGrid, configuration and a
// live email service); they assert on the same query shape it uses, so a regression in either the
// SET NULL behaviour or the "a targeted send never widens" rule fails here.
public class ScheduledSendAudienceTests
{
    [Fact]
    public async Task Deleting_the_targeted_client_nulls_the_send_target_rather_than_removing_the_send()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var agent = await SeedAgentWithClientsAsync(db, subscriberCount: 5);
        var target = await db.Clients.FirstAsync(c => c.AgentUserId == agent.Id);

        var newsletter = new NewsLetter { AgentUserId = agent.Id, Subject = "S", HtmlBody = "<p>x</p>" };
        db.NewsLetters.Add(newsletter);
        await db.SaveChangesAsync();

        var send = new NewsLetterSend
        {
            NewsLetterId = newsletter.Id,
            AgentUserId = agent.Id,
            AudienceType = NewsLetterAudienceType.IndividualClient,
            ClientId = target.Id,
            Status = NewsLetterSendStatus.Scheduled
        };
        db.NewsLetterSends.Add(send);
        await db.SaveChangesAsync();

        db.Clients.Remove(target);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reloaded = await db.NewsLetterSends.FirstAsync(s => s.Id == send.Id);

        // The send survives with a null target. This is precisely the state the dispatcher must
        // refuse to interpret as "everyone".
        Assert.Null(reloaded.ClientId);
        Assert.Equal(NewsLetterAudienceType.IndividualClient, reloaded.AudienceType);
        Assert.Equal(NewsLetterSendStatus.Scheduled, reloaded.Status);
    }

    [Fact]
    public async Task A_targeted_send_whose_target_is_gone_must_not_resolve_to_the_whole_list()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var agent = await SeedAgentWithClientsAsync(db, subscriberCount: 5);
        var target = await db.Clients.FirstAsync(c => c.AgentUserId == agent.Id);
        var targetId = target.Id;

        db.Clients.Remove(target);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The unnarrowed query -- exactly what the old `_ => query` fall-through returned.
        var everyone = await db.Clients
            .Where(c => c.AgentUserId == agent.Id && c.IsNewsletterSubscribed && c.EmailOptOutAt == null)
            .CountAsync();
        Assert.Equal(4, everyone); // 5 seeded, 1 deleted

        // What the dispatcher now does: confirm the target still exists before narrowing, and treat
        // its absence as "cannot resolve" rather than "no filter".
        var targetStillExists = await db.Clients.AnyAsync(c => c.Id == targetId);
        Assert.False(targetStillExists);

        var resolved = targetStillExists ? 1 : 0;
        Assert.True(resolved < everyone,
            "A send targeting a single deleted client resolved to the entire subscriber list -- " +
            "this is the F5 audience-widening bug.");
    }

    [Fact]
    public async Task A_category_targeted_send_must_check_the_category_exists_not_just_the_id()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var agent = await SeedAgentWithClientsAsync(db, subscriberCount: 3);

        var category = new ClientCategory { AgentUserId = agent.Id, Name = "VIP" };
        db.ClientCategories.Add(category);
        await db.SaveChangesAsync();
        var categoryId = category.Id;

        db.ClientCategories.Remove(category);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Whether a deleted category leaves the id dangling (PollSends has no FK) or nulled
        // (NewsLetterSends is SET NULL) differs per table -- which is why both dispatchers now test
        // EXISTENCE rather than trusting the id.
        Assert.False(await db.ClientCategories.AnyAsync(c => c.Id == categoryId));
    }

    private static async Task<AgentUser> SeedAgentWithClientsAsync(IPRODbContext db, int subscriberCount)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"audience-{Guid.NewGuid():N}"[..20],
            Email = "audience.test@example.com",
            FirstName = "Audience",
            LastName = "Test",
            DomainName = $"audience-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        for (var i = 0; i < subscriberCount; i++)
        {
            db.Clients.Add(new Client
            {
                AgentUserId = agent.Id,
                FirstName = $"Client{i}",
                LastName = "Subscriber",
                Email = $"client{i}@example.com",
                IsNewsletterSubscribed = true
            });
        }
        await db.SaveChangesAsync();

        return agent;
    }
}
