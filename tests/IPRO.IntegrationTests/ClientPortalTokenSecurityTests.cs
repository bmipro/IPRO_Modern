using System;
using System.Threading.Tasks;
using IPRO.Entities;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IPRO.IntegrationTests;

// Regression suite for the 2026-08-14 ultra-audit CRITICAL: an omitted client-portal invite token
// was rewritten by EF to `PortalInviteToken IS NULL` and matched any client whose token is null
// (every uninvited, activated, or revoked client) — an unauthenticated cross-tenant account
// takeover. These tests drive the REAL controller against a real MySQL schema.
public class ClientPortalTokenSecurityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public async Task Activate_with_missing_or_wrong_token_never_matches_a_client(string? token)
    {
        await using var db = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using (var seed = db.CreateContext())
        {
            // An uninvited client — the normal state: PortalInviteToken is null. Before the fix this
            // is exactly the row an empty token would hand to an anonymous caller.
            seed.Add(NewClient(seed, "uninvited@example.com", inviteToken: null, expiresAt: null));
            await seed.SaveChangesAsync();
        }

        await using var context = db.CreateContext();
        var controller = new ClientPortalAccountController(context, new PasswordHasher<Client>());

        var get = await controller.Activate(token!);
        Assert.IsType<NotFoundResult>(get);

        var post = await controller.Activate(token!, "aValidPassword1", "aValidPassword1");
        Assert.IsType<NotFoundResult>(post);
    }

    [Fact]
    public async Task Activate_with_a_valid_unexpired_token_matches_that_client_only()
    {
        await using var db = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        const string realToken = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
        await using (var seed = db.CreateContext())
        {
            seed.Add(NewClient(seed, "uninvited@example.com", inviteToken: null, expiresAt: null));
            seed.Add(NewClient(seed, "invited@example.com", inviteToken: realToken,
                expiresAt: DateTime.UtcNow.AddDays(7)));
            await seed.SaveChangesAsync();
        }

        await using var context = db.CreateContext();
        var controller = new ClientPortalAccountController(context, new PasswordHasher<Client>());

        var get = await controller.Activate(realToken);
        Assert.IsType<ViewResult>(get); // found — renders the activation form
    }

    [Fact]
    public async Task Activate_with_an_expired_token_is_rejected()
    {
        await using var db = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        const string expiredToken = "ffffffffffffffffffffffffffffffff";
        await using (var seed = db.CreateContext())
        {
            seed.Add(NewClient(seed, "lapsed@example.com", inviteToken: expiredToken,
                expiresAt: DateTime.UtcNow.AddDays(-1)));
            await seed.SaveChangesAsync();
        }

        await using var context = db.CreateContext();
        var controller = new ClientPortalAccountController(context, new PasswordHasher<Client>());

        Assert.IsType<NotFoundResult>(await controller.Activate(expiredToken));
    }

    private static Client NewClient(DataAccess.IPRODbContext db, string email, string? inviteToken, DateTime? expiresAt)
    {
        var agent = new AgentUser
        {
            UserName = $"agent-{Guid.NewGuid():N}"[..20],
            Email = $"{Guid.NewGuid():N}@agent.example.com",
            DomainName = $"d-{Guid.NewGuid():N}"[..24]
        };
        db.Add(agent);
        return new Client
        {
            AgentUser = agent,
            FirstName = "Test",
            LastName = "Client",
            Email = email,
            PortalInviteToken = inviteToken,
            PortalInviteTokenExpiresAt = expiresAt
        };
    }
}
