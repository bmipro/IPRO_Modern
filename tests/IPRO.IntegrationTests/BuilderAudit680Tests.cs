using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// iPro TODO 680: the 2026-11-24 audit's Batch 2, iPro's half: G-1 a builder session ends with the sign-in that opened
// it; G-4 one look a site every few seconds.
public class BuilderAudit680Tests
{
    [Fact]
    public async Task A_builder_session_stands_only_while_the_sign_in_that_opened_it_would()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agent = new AgentUser
        {
            UserName = $"g1-{Guid.NewGuid():N}"[..20], Email = $"g1-{Guid.NewGuid():N}"[..14] + "@example.test",
            FirstName = "Gee", LastName = "One", DomainName = $"g1-{Guid.NewGuid():N}"[..24], PasswordHash = "owner-hash-1", IsActive = true
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var member = new TeamMember { AgentUserId = agent.Id, FullName = "Tee", Email = $"t-{Guid.NewGuid():N}"[..12] + "@example.test", PasswordHash = "member-hash-1", IsActive = true };
        db.Add(member);
        await db.SaveChangesAsync();

        WebsiteBuilderSession Session(int teamMemberId, string? passwordStamp) => new()
        {
            AgentUserId = agent.Id, AgentWebsiteId = 1, Kind = WebsiteBuilderSessionKinds.Session, TokenSha256 = Guid.NewGuid().ToString("N"),
            HandoverId = string.Empty, ExpiresAt = DateTime.UtcNow.AddHours(1), TeamMemberId = teamMemberId, PasswordStamp = passwordStamp
        };
        var owners = Session(0, TwoStepGate.Fingerprint("owner-hash-1"));
        var members = Session(member.Id, TwoStepGate.Fingerprint("member-hash-1"));
        var older = Session(0, null);

        Assert.True(await BuilderSessionSignIn.StandsAsync(db, owners));
        Assert.True(await BuilderSessionSignIn.StandsAsync(db, members));
        Assert.True(await BuilderSessionSignIn.StandsAsync(db, older));   // from before: stands until it runs out

        // The member removed from the team: their builder ends; the owner's stands.
        await db.TeamMembers.Where(t => t.Id == member.Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.IsActive, false));
        Assert.False(await BuilderSessionSignIn.StandsAsync(db, members));
        Assert.True(await BuilderSessionSignIn.StandsAsync(db, owners));

        // The owner's password changed: the owner's builder ends too.
        await db.AgentUsers.Where(a => a.Id == agent.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.PasswordHash, "owner-hash-2"));
        Assert.False(await BuilderSessionSignIn.StandsAsync(db, owners));
    }

    [Fact]
    public async Task The_pass_remembers_who_opened_the_builder_and_signing_out_ends_only_their_sessions()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "7"), new Claim("TeamMemberId", "42"),
            new Claim(PortalSignIn.PasswordClaimType, "pw-stamp"), new Claim(TwoStepGate.ClaimType, "ts-stamp")
        }, "test"));
        var signedIn = BuilderSessionSignIn.From(user);
        Assert.Equal(new BuilderBeta.SignedIn(42, "pw-stamp", "ts-stamp"), signedIn);

        var pass = await BuilderBeta.IssuePassAsync(db, new AgentWebsite { Id = 9, AgentUserId = 7 }, false, DateTime.UtcNow, signedIn);
        var row = await db.WebsiteBuilderSessions.AsNoTracking().SingleAsync(s => s.TokenSha256 == BuilderBetaHash(pass));
        Assert.Equal((42, "pw-stamp", "ts-stamp"), (row.TeamMemberId, row.PasswordStamp, row.TwoStepStamp));
        var beta = Read(@"src\IPRO.Business\SiteImport\BuilderBeta.cs");
        Assert.Contains("TeamMemberId = row.TeamMemberId,", beta);   // and the session the pass is traded for

        db.WebsiteBuilderSessions.AddRange(
            new WebsiteBuilderSession { AgentUserId = 7, AgentWebsiteId = 9, Kind = WebsiteBuilderSessionKinds.Session, TokenSha256 = "a1", HandoverId = "", ExpiresAt = DateTime.UtcNow.AddHours(1), TeamMemberId = 42 },
            new WebsiteBuilderSession { AgentUserId = 7, AgentWebsiteId = 9, Kind = WebsiteBuilderSessionKinds.Session, TokenSha256 = "a2", HandoverId = "", ExpiresAt = DateTime.UtcNow.AddHours(1), TeamMemberId = 0 });
        await db.SaveChangesAsync();
        Assert.Equal(2, await BuilderBeta.EndSessionsOfAsync(db, 7, 42, DateTime.UtcNow));   // the session and the pass this person opened
        Assert.Null(await db.WebsiteBuilderSessions.AsNoTracking().Where(s => s.TokenSha256 == "a2").Select(s => s.EndedAt).SingleAsync());

        Assert.Contains("BuilderBeta.EndSessionsOfAsync(_db, agentId, signedIn.TeamMemberId, DateTime.UtcNow);", Read(@"src\IPRO.Web\Controllers\AccountController.cs"));
        var api = Read(@"src\IPRO.Web\Controllers\BuilderApiController.cs");
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(api, @"await FindStandingSessionAsync\(\);").Count);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(api, @"BuilderBeta\.FindSessionAsync\("));
    }

    [Fact]
    public void A_site_has_a_few_looks_a_minute()
    {
        var key = "test:" + Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        for (var i = 0; i < BuilderSessionSignIn.LooksPerMinute; i++) Assert.True(BuilderSessionSignIn.TryTakeLook(key, now.AddSeconds(i)));
        Assert.False(BuilderSessionSignIn.TryTakeLook(key, now.AddSeconds(30)));
        Assert.True(BuilderSessionSignIn.TryTakeLook(key + "-other", now.AddSeconds(30)));   // another site's are its own
        Assert.True(BuilderSessionSignIn.TryTakeLook(key, now.AddSeconds(61)));             // the first has left the minute
    }

    private static string BuilderBetaHash(string token) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
