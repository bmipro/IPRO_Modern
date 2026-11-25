using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPRO.IntegrationTests;

// 679 (the owner, 2026-11-25): the AI drafts each account has a day, one number for everyone set in SuperAdmin; and
// the audit's A-3, a team member's way back in while someone keeps their sign-in locked.
public class AiLimitAndTeamLink679Tests
{
    [Fact]
    public async Task SuperAdmin_sets_the_drafts_each_account_has_a_day_and_the_portal_follows_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);
        Assert.Equal(AiDraftAllowance.DefaultPerDay, await AiDraftAllowance.LimitAsync(db));

        var admin = AdminAiUsage(db);
        Assert.IsType<RedirectToActionResult>(await admin.SetDraftLimit(3));
        Assert.Equal(3, await AiDraftAllowance.LimitAsync(db));
        Assert.IsType<RedirectToActionResult>(await AdminAiUsage(db).SetDraftLimit(0));      // refused: out of range
        Assert.Equal(3, await AiDraftAllowance.LimitAsync(db));

        var now = DateTime.UtcNow;
        for (var i = 0; i < 3; i++) Assert.True((await AiDraftAllowance.TryTakeAsync(db, agentId, now)).Allowed);
        var refused = await AiDraftAllowance.TryTakeAsync(db, agentId, now);
        Assert.False(refused.Allowed);
        Assert.Equal(3, refused.Limit);
        // A refused try is not counted, so raising the number lets them go on at once.
        await AdminAiUsage(db).SetDraftLimit(4);
        Assert.True((await AiDraftAllowance.TryTakeAsync(db, agentId, now)).Allowed);
    }

    [Fact]
    public async Task A_draft_the_AI_service_did_not_answer_is_given_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db);
        var now = DateTime.UtcNow;

        Assert.True((await AiDraftAllowance.TryTakeAsync(db, agentId, now)).Allowed);
        await AiDraftAllowance.FinishAsync(db, agentId, 0, 0, now);   // nothing came back
        var day = await AiDraftAllowance.DayAsync(db, agentId, now);
        Assert.Equal(0, await db.AiAdviserDailyUsages.AsNoTracking().Where(u => u.AgentUserId == agentId && u.Date == day).Select(u => u.CallCount).SingleAsync());

        Assert.True((await AiDraftAllowance.TryTakeAsync(db, agentId, now)).Allowed);
        await AiDraftAllowance.FinishAsync(db, agentId, 900, 300, now);
        Assert.Equal(1, await db.AiAdviserDailyUsages.AsNoTracking().Where(u => u.AgentUserId == agentId && u.Date == day).Select(u => u.CallCount).SingleAsync());
    }

    [Fact]
    public async Task The_day_is_the_accounts_own()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await AgentAsync(db, "America/Vancouver");
        // 04:00 UTC is still the evening before in Vancouver.
        var at = new DateTime(2026, 11, 26, 4, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 11, 25), await AiDraftAllowance.DayAsync(db, agentId, at));
    }

    [Fact]
    public async Task The_owner_emails_a_one_time_link_that_sets_a_new_password_and_unlocks_a_locked_member()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var ownerId = await AgentAsync(db);
        var member = new TeamMember { AgentUserId = ownerId, FullName = "Asha Assistant", Email = $"asha-{Guid.NewGuid():N}"[..14] + "@example.test", PasswordHash = "x", IsActive = true, MustChangePassword = false };
        db.Add(member);
        await db.SaveChangesAsync();
        // Someone has kept their sign-in locked.
        await db.TeamMembers.Where(t => t.Id == member.Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.FailedSignInCount, 5).SetProperty(t => t.SignInLockedUntil, DateTime.UtcNow.AddMinutes(15)));
        db.ChangeTracker.Clear();

        var email = new TestEmailRecorder();
        var team = Team(db, ownerId, email);
        Assert.IsType<RedirectToActionResult>(await team.SendSignInLink(member.Id));
        var sent = Assert.Single(email.Sent);
        Assert.Equal(member.Email, sent.To);
        var token = System.Text.RegularExpressions.Regex.Match(sent.Html, @"TeamSignIn\?t=([A-Za-z0-9_\-]+)").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));
        Assert.False(await db.TeamMembers.AnyAsync(t => t.SignInLinkHash == token));   // only its hash is kept

        // A team member's session cannot send links.
        var asMember = Team(db, ownerId, email, teamMember: true);
        Assert.IsType<RedirectResult>(await asMember.SendSignInLink(member.Id));
        Assert.Single(email.Sent);

        // The link page, then the new password: signed in past the lock, and the link is spent.
        await using (var pageDb = testDb.CreateContext())
        {
            var page = Lock583.Portal(pageDb, new RecordingLockNotices(), new RecordingLogger());
            await page.TeamSignIn(token);
            Assert.True(page.ViewBag.TokenValid as bool?);
        }
        await using (var postDb = testDb.CreateContext())
        {
            var post = Lock583.Portal(postDb, new RecordingLockNotices(), new RecordingLogger());
            var result = await post.TeamSignIn(token, "a-new-password-679", "a-new-password-679");
            Assert.Equal("/portal/Dashboard", Assert.IsType<RedirectResult>(result).Url);
        }
        db.ChangeTracker.Clear();
        var after = await db.TeamMembers.AsNoTracking().SingleAsync(t => t.Id == member.Id);
        Assert.Null(after.SignInLockedUntil);
        Assert.Null(after.SignInLinkHash);
        Assert.NotEqual(PasswordVerificationResult.Failed, new PasswordHasher<TeamMember>().VerifyHashedPassword(after, after.PasswordHash, "a-new-password-679"));

        await using (var againDb = testDb.CreateContext())
        {
            var again = Lock583.Portal(againDb, new RecordingLockNotices(), new RecordingLogger());
            await again.TeamSignIn(token, "another-password-1", "another-password-1");
            Assert.False(again.ViewBag.TokenValid as bool?);   // one use
        }
    }

    [Fact]
    public void The_link_runs_out_and_its_pages_are_rate_limited()
    {
        Assert.Equal(60, TeamSignInLinks.ValidMinutes);
        var account = Read(@"src\IPRO.Web\Controllers\AccountController.cs").Replace("\r\n", "\n");
        Assert.Contains("[EnableRateLimiting(EndpointRateLimits.PasswordReset)]\n    [HttpPost, ValidateAntiForgeryToken]\n    public async Task<IActionResult> TeamSignIn(", account);
        Assert.Contains("return await SignInTeamMemberAndRedirectAsync(member, false, null);", account);   // two-step still follows
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }

    private static async Task<int> AgentAsync(IPRODbContext db, string timeZone = "America/Toronto")
    {
        var agent = new AgentUser
        {
            UserName = $"a679-{Guid.NewGuid():N}"[..20], Email = $"a679-{Guid.NewGuid():N}"[..14] + "@example.test",
            FirstName = "Ai", LastName = "Limit", CompanyName = "Limit Co", DomainName = $"a679-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", TimeZone = timeZone, TrialEndsAt = DateTime.UtcNow.AddDays(10), IsActive = true
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static IPRO.Admin.Controllers.AiUsageController AdminAiUsage(IPRODbContext db)
    {
        var controller = new IPRO.Admin.Controllers.AiUsageController(db);
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "owner") }, "test")) };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static TeamController Team(IPRODbContext db, int ownerId, TestEmailRecorder email, bool teamMember = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.example.test" }).Build());
        services.AddSingleton<IPRO.Email.IEmailService>(email);
        services.AddSingleton<Microsoft.AspNetCore.Mvc.Routing.IUrlHelperFactory, Microsoft.AspNetCore.Mvc.Routing.UrlHelperFactory>();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, ownerId.ToString()) };
        if (teamMember) claims.Add(new Claim("TeamMemberId", "1"));
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var controller = new TeamController(new UnitOfWork(db), new PasswordHasher<TeamMember>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx }
        };
        controller.TempData = new TempDataDictionary(ctx, new NoTempData());
        controller.Url = new Microsoft.AspNetCore.Mvc.Routing.UrlHelper(new ActionContext(ctx, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        return controller;
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
