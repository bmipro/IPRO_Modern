using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Admin.Controllers;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
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
using Microsoft.Extensions.Logging;
using Xunit;

namespace IPRO.IntegrationTests;

// 583 (2026-10-16), the owner's rule: 5 wrong passwords in a row lock that account's sign-in for 15
// minutes, and the account holder gets one email when it locks. Before this there was no lockout at all:
// only the per-visitor limit (582: 10 sign-in posts in 5 minutes per visitor) stood between many
// visitors and one account's password.
//
// These drive the real sign-in actions -- advisers and team members (AccountController), clients
// (ClientPortalAccountController), admins (AdminController) -- against a throwaway MySQL database, and
// the SuperAdmin screens that see and clear a lock. A refusal takes 1.5 seconds on purpose (the same for
// every name), so each class keeps its attempts few.

// ---- advisers and team members: the portal's one sign-in form -------------------------------------------

public class SignInLockoutPortal583Tests
{
    [Fact]
    public async Task An_advisers_fifth_wrong_password_locks_and_the_sixth_is_refused_even_with_the_right_one()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "patlee", "pat@lee.example.com");
        var notices = new RecordingLockNotices();
        var log = new RecordingLogger();

        for (var i = 1; i <= 4; i++)
            Assert.Equal("Invalid username or password.", await Lock583.PortalRefusalAsync(testDb, "patlee", "wrong-" + i, notices, log));
        Assert.Empty(notices.Queued);

        // The fifth locks it, and says so.
        Assert.Equal(SignInMessages.PortalLocked, await Lock583.PortalRefusalAsync(testDb, "patlee", "wrong-5", notices, log));
        var account = Assert.Single(notices.Queued);
        Assert.Equal(new SignInAccount(SignInAccountKind.Adviser, agentId), account);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("Sign-in locked for 15 minutes") && l.Text.Contains($"Adviser {agentId}"));

        await using (var db = testDb.CreateContext())
        {
            var row = await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId);
            Assert.NotNull(row.SignInLockedUntil);
            Assert.InRange(row.SignInLockedUntil!.Value, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(15.1));
        }

        // The right password does not open it, and gets the same sentence; no second email.
        Assert.Equal(SignInMessages.PortalLocked, await Lock583.PortalRefusalAsync(testDb, "patlee", Lock583.Password, notices, log));
        Assert.Equal(SignInMessages.PortalLocked, await Lock583.PortalRefusalAsync(testDb, "pat@lee.example.com", Lock583.Password, notices, log));
        Assert.Single(notices.Queued);
    }

    [Fact]
    public async Task After_15_minutes_the_right_password_works_and_a_sign_in_that_works_starts_the_count_again()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "robin", "robin@example.com");
        await Lock583.SetLockAsync(testDb, "AgentUsers", agentId, count: 5, lockedUntil: DateTime.UtcNow.AddMinutes(-1));   // ran out a minute ago

        Assert.IsType<RedirectResult>(await Lock583.PortalSignInAsync(testDb, "robin", Lock583.Password));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "AgentUsers", agentId));

        // Four wrong, one right, four wrong: never five in a row, never locked.
        var notices = new RecordingLockNotices();
        await Lock583.SetLockAsync(testDb, "AgentUsers", agentId, count: 4, lockedUntil: null);
        Assert.IsType<RedirectResult>(await Lock583.PortalSignInAsync(testDb, "robin@example.com", Lock583.Password));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "AgentUsers", agentId));
        await Lock583.SetLockAsync(testDb, "AgentUsers", agentId, count: 3, lockedUntil: null);
        Assert.Equal("Invalid username or password.", await Lock583.PortalRefusalAsync(testDb, "robin", "nope", notices, new RecordingLogger()));
        Assert.Equal(4, await Lock583.CountAsync(testDb, "AgentUsers", agentId));
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "AgentUsers", agentId));
        Assert.Empty(notices.Queued);
    }

    [Fact]
    public async Task The_forgot_password_reset_unlocks_at_once()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "sam", "sam@example.com");
        await Lock583.SetLockAsync(testDb, "AgentUsers", agentId, count: 5, lockedUntil: DateTime.UtcNow.AddMinutes(14));
        var log = new RecordingLogger();

        string token;
        await using (var db = testDb.CreateContext())
            token = (await new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()).InitiatePasswordResetAsync("sam@example.com"))!.PasswordResetToken!;

        await using (var db = testDb.CreateContext())
        {
            var controller = Lock583.Portal(db, new RecordingLockNotices(), log);
            Assert.IsType<RedirectResult>(await controller.ResetPassword(token, "a-brand-new-one", "a-brand-new-one"));
        }
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "AgentUsers", agentId));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "AgentUsers", agentId));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("Sign-in lock cleared (forgot-password reset)"));
        Assert.IsType<RedirectResult>(await Lock583.PortalSignInAsync(testDb, "sam", "a-brand-new-one"));
    }

    [Fact]
    public async Task Guesses_sent_together_get_five_passwords_checked_and_one_email()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "burst", "burst@example.com");
        var notices = new RecordingLockNotices();
        var hasher = new CountingHasher();

        var answers = await Task.WhenAll(Enumerable.Range(1, 12).Select(async i =>
        {
            await using var db = testDb.CreateContext();
            var controller = Lock583.Portal(db, notices, new RecordingLogger(), hasher);
            var result = await controller.Login("burst", "guess-" + i);
            return Lock583.Error(result);
        }));

        Assert.Equal(5, hasher.Verified);
        Assert.Single(notices.Queued);
        Assert.True(answers.Count(a => a == SignInMessages.PortalLocked) >= 7);
        Assert.NotNull(await Lock583.LockedUntilAsync(testDb, "AgentUsers", agentId));
    }

    [Fact]
    public async Task The_current_password_on_Change_Password_counts_like_a_sign_in()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "open", "open@example.com");
        var notices = new RecordingLockNotices();
        var signedIn = Lock583.User(new Claim(ClaimTypes.NameIdentifier, agentId.ToString()));

        for (var i = 1; i <= 5; i++)
        {
            await using var db = testDb.CreateContext();
            var controller = Lock583.Portal(db, notices, new RecordingLogger(), user: signedIn);
            var result = await controller.ChangePassword("not-it-" + i, "new-password-1", "new-password-1");
            Assert.Equal(i < 5 ? "Current password is incorrect." : SignInMessages.ChangePasswordLocked, Lock583.Error(result));
        }
        Assert.Single(notices.Queued);

        // Locked for sign-in as well, and the right current password is refused until the lock ends.
        Assert.Equal(SignInMessages.PortalLocked, await Lock583.PortalRefusalAsync(testDb, "open", Lock583.Password, notices, new RecordingLogger()));
        await using (var db = testDb.CreateContext())
        {
            var controller = Lock583.Portal(db, notices, new RecordingLogger(), user: signedIn);
            Assert.Equal(SignInMessages.ChangePasswordLocked, Lock583.Error(await controller.ChangePassword(Lock583.Password, "new-password-1", "new-password-1")));
        }
    }

    [Fact]
    public async Task A_team_members_fifth_wrong_password_locks_their_sign_in_not_the_owners_and_the_owners_reset_unlocks_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "owner", "owner@example.com");
        var memberId = await Lock583.SeedTeamMemberAsync(testDb, agentId, "assistant@example.com");
        var notices = new RecordingLockNotices();
        var log = new RecordingLogger();

        for (var i = 1; i <= 4; i++)
            Assert.Equal("Invalid username or password.", await Lock583.PortalRefusalAsync(testDb, "assistant@example.com", "wrong-" + i, notices, log));
        Assert.Equal(SignInMessages.PortalLocked, await Lock583.PortalRefusalAsync(testDb, "assistant@example.com", "wrong-5", notices, log));
        Assert.Equal(new SignInAccount(SignInAccountKind.TeamMember, memberId), Assert.Single(notices.Queued));
        Assert.Equal(SignInMessages.PortalLocked, await Lock583.PortalRefusalAsync(testDb, "assistant@example.com", Lock583.Password, notices, log));

        // The owner's own sign-in is not touched.
        Assert.IsType<RedirectResult>(await Lock583.PortalSignInAsync(testDb, "owner", Lock583.Password));
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "AgentUsers", agentId));

        // The owner resets the member's password on My Team: unlocked, and the temporary password works.
        string temporary;
        await using (var db = testDb.CreateContext())
        {
            var team = Lock583.Prepare(new TeamController(new UnitOfWork(db), new PasswordHasher<TeamMember>(), log.For<TeamController>()),
                Lock583.User(new Claim(ClaimTypes.NameIdentifier, agentId.ToString())));
            Assert.IsType<RedirectToActionResult>(await team.ResetPassword(memberId));
            temporary = (string)team.TempData["TeamTempPassword"]!;
        }
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "TeamMembers", memberId));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("Sign-in lock cleared (password reset by the account owner)"));
        Assert.IsType<RedirectToActionResult>(await Lock583.PortalSignInAsync(testDb, "assistant@example.com", temporary));   // on to Change Password
    }

    [Fact]
    public void The_Team_page_says_a_member_is_locked_and_that_a_reset_unlocks_it()
    {
        var view = Lock583.Read(@"src\IPRO.Web\Views\Team\Index.cshtml");
        Assert.Contains("SignInLockout.IsLocked(m.SignInLockedUntil, DateTime.UtcNow)", view);
        Assert.Contains("Resetting the password unlocks it.", view);
    }
}

// ---- clients: the client portal ----------------------------------------------------------------------

public class SignInLockoutClients583Tests
{
    [Fact]
    public async Task A_clients_fifth_wrong_password_locks_and_the_sixth_is_refused_even_with_the_right_one()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "adv1", "adv1@example.com");
        var clientId = await Lock583.SeedClientAsync(testDb, agentId, "kim@example.com", Lock583.Password);
        var notices = new RecordingLockNotices();
        var log = new RecordingLogger();

        for (var i = 1; i <= 4; i++)
            Assert.Equal("Invalid email or password.", await Lock583.ClientRefusalAsync(testDb, "kim@example.com", "wrong-" + i, notices, log));
        Assert.Equal(SignInMessages.ClientPortalLocked, await Lock583.ClientRefusalAsync(testDb, "KIM@example.com ", "wrong-5", notices, log));
        Assert.Equal(new SignInAccount(SignInAccountKind.Client, clientId), Assert.Single(notices.Queued));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains($"Client {clientId}"));

        Assert.Equal(SignInMessages.ClientPortalLocked, await Lock583.ClientRefusalAsync(testDb, "kim@example.com", Lock583.Password, notices, log));
        Assert.Single(notices.Queued);

        // When the 15 minutes are over the right password works, and the count starts again.
        await Lock583.SetLockAsync(testDb, "Clients", clientId, count: 5, lockedUntil: DateTime.UtcNow.AddSeconds(-1));
        Assert.IsType<RedirectToActionResult>(await Lock583.ClientSignInAsync(testDb, "kim@example.com", Lock583.Password));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", clientId));
    }

    [Fact]
    public async Task A_new_portal_invitation_unlocks_the_client()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "adv2", "adv2@example.com");
        var clientId = await Lock583.SeedClientAsync(testDb, agentId, "lee@example.com", Lock583.Password);
        await Lock583.SetLockAsync(testDb, "Clients", clientId, count: 5, lockedUntil: DateTime.UtcNow.AddMinutes(10));
        const string token = "0123456789abcdef0123456789abcdef";
        await using (var db = testDb.CreateContext())
        {
            // What ClientsController.InvitePortal does: a token, no password until it is used.
            var client = await db.Clients.SingleAsync(c => c.Id == clientId);
            client.PortalInviteToken = token;
            client.PortalInviteTokenExpiresAt = DateTime.UtcNow.AddDays(7);
            client.PortalPasswordHash = null;
            await db.SaveChangesAsync();
        }
        var log = new RecordingLogger();
        await using (var db = testDb.CreateContext())
        {
            var controller = Lock583.Prepare(new ClientPortalAccountController(db, new PasswordHasher<Client>(), new RecordingLockNotices(), log.For<ClientPortalAccountController>()));
            Assert.IsType<RedirectToActionResult>(await controller.Activate(token, "chosen-password", "chosen-password"));
        }
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "Clients", clientId));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("Sign-in lock cleared (new portal invitation)"));
        Assert.IsType<RedirectToActionResult>(await Lock583.ClientSignInAsync(testDb, "lee@example.com", "chosen-password"));
    }

    // The review's finding: an adviser can give their own client the email of another adviser's client
    // and a password of their choosing (the activation link is on the client's profile). 583 counted each
    // guess that opened the adviser's own row as a wrong password for the other client, so the guessing
    // was not free. 586 closed the path itself: a password is checked against one adviser's clients only,
    // so the guesses never reach the other client -- here the guesser's row is the older, and the shared
    // address with no adviser in the link goes to it -- and at the other adviser's own sign-in a guess
    // counts as anyone's would.
    [Fact]
    public async Task Guesses_through_a_planted_row_never_reach_the_other_advisers_client_and_count_there_as_anyones()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var guesser = await Lock583.SeedAgentAsync(testDb, "guesser", "guesser@example.com");
        var other = await Lock583.SeedAgentAsync(testDb, "other", "other@example.com");
        var ownRow = await Lock583.SeedClientAsync(testDb, guesser, "jo@example.com", "guess-1");
        var victim = await Lock583.SeedClientAsync(testDb, other, "jo@example.com", "the-real-one");
        var notices = new RecordingLockNotices();

        for (var i = 1; i <= 5; i++)
        {
            await Lock583.SetPortalPasswordAsync(testDb, ownRow, "guess-" + i);   // the guesser's own row takes each guess
            await using var db = testDb.CreateContext();
            var answer = await Lock583.ClientPortal(db, notices, new RecordingLogger()).Login("jo@example.com", "guess-" + i);
            Assert.IsType<RedirectToActionResult>(answer);                       // into their own row
        }
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", victim));
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "Clients", victim));
        Assert.Empty(notices.Queued);

        // The right guess opens only the guesser's own row: no choice of accounts, so nothing says it was right.
        await Lock583.SetPortalPasswordAsync(testDb, ownRow, "the-real-one");
        await using (var db = testDb.CreateContext())
        {
            var answer = await Lock583.ClientPortal(db, notices, new RecordingLogger()).Login("jo@example.com", "the-real-one");
            Assert.IsType<RedirectToActionResult>(answer);   // not the ChooseAccount view
        }
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", victim));

        // At the other adviser's sign-in a guess is a wrong password for their client, as anyone's is.
        Assert.Equal("Invalid email or password.", await Lock583.ClientRefusalAsync(testDb, "jo@example.com", "guess-6", notices, new RecordingLogger(), adviser: other.ToString()));
        Assert.Equal(1, await Lock583.CountAsync(testDb, "Clients", victim));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", ownRow));
    }

    [Fact]
    public async Task A_client_of_two_advisers_has_a_sign_in_with_each_and_one_lock_does_not_stop_the_other()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var first = await Lock583.SeedAgentAsync(testDb, "first", "first@example.com");
        var second = await Lock583.SeedAgentAsync(testDb, "second", "second@example.com");
        var atFirst = await Lock583.SeedClientAsync(testDb, first, "jo@example.com", "same-password");
        var atSecond = await Lock583.SeedClientAsync(testDb, second, "jo@example.com", "same-password");

        // 586: one sign-in with each adviser, never both at once. With no adviser in the link the older
        // record decides (no choice of advisers is offered); the link each adviser hands out names its own.
        Assert.IsType<RedirectToActionResult>(await Lock583.ClientSignInAsync(testDb, "jo@example.com", "same-password"));
        Assert.IsType<RedirectToActionResult>(await Lock583.ClientSignInAsync(testDb, "jo@example.com", "same-password", adviser: second.ToString()));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", atFirst));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "Clients", atSecond));

        // One locked does not stop the other, and a wrong password counts only with the adviser it was typed for.
        await Lock583.SetLockAsync(testDb, "Clients", atSecond, count: 5, lockedUntil: DateTime.UtcNow.AddMinutes(10));
        Assert.IsType<RedirectToActionResult>(await Lock583.ClientSignInAsync(testDb, "jo@example.com", "same-password"));
        Assert.Equal(SignInMessages.ClientPortalLocked, await Lock583.ClientRefusalAsync(testDb, "jo@example.com", "same-password", new RecordingLockNotices(), new RecordingLogger(), adviser: second.ToString()));
        Assert.Equal("Invalid email or password.", await Lock583.ClientRefusalAsync(testDb, "jo@example.com", "wrong", new RecordingLockNotices(), new RecordingLogger()));
        Assert.Equal(1, await Lock583.CountAsync(testDb, "Clients", atFirst));
        Assert.Equal(5, await Lock583.CountAsync(testDb, "Clients", atSecond));
    }

    [Fact]
    public void The_clients_profile_says_the_portal_sign_in_is_locked_in_the_advisers_time_and_offers_a_new_invitation()
    {
        var view = Lock583.Read(@"src\IPRO.Web\Views\Clients\Details.cshtml");
        Assert.Contains("@if (IPRO.DataAccess.SignInLockout.IsLocked(Model.SignInLockedUntil, DateTime.UtcNow))", view);
        Assert.Contains("FromUtc(Model.SignInLockedUntil!.Value, ViewBag.AgentTimeZone as string)", view);
        Assert.Contains("<form asp-action=\"InvitePortal\" asp-route-id=\"@Model.Id\" method=\"post\">\n              @Html.AntiForgeryToken()", view);
        Assert.Contains(">Send a New Invitation</button>", view);
        Assert.Contains("ViewBag.AgentTimeZone = await AgentTimeZoneHelper.ResolveForAgentAsync(_db, AgentId);", Lock583.Read(@"src\IPRO.Web\Controllers\ClientsController.cs"));
    }

    [Fact]
    public async Task A_new_contact_has_no_portal_sign_in_whatever_the_form_posts()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "creator", "creator@example.com");
        await using (var db = testDb.CreateContext())
        {
            var controller = Lock583.Prepare(new ClientsController(null!, null!, new UnitOfWork(db), db, new Lock583.GrantAll(), null!, null!, null!,
                    new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), Lock583.Config, null!),
                Lock583.User(new Claim(ClaimTypes.NameIdentifier, agentId.ToString())));
            var posted = new Client
            {
                FirstName = "Posted", LastName = "Fields", Email = "posted@example.com", Country = "Canada",
                PortalPasswordHash = new PasswordHasher<Client>().HashPassword(new Client(), "chosen-by-the-form"),
                PortalInviteToken = "ffffffffffffffffffffffffffffffff", PortalInviteTokenExpiresAt = DateTime.UtcNow.AddDays(7),
                PortalActivatedAt = DateTime.UtcNow,
                FailedSignInCount = -1000, SignInLockedUntil = DateTime.UtcNow.AddYears(5), SignInLockNoticeAt = DateTime.UtcNow
            };
            Assert.IsType<RedirectToActionResult>(await controller.Create(posted, Array.Empty<int>()));
        }
        await using var check = testDb.CreateContext();
        var row = await check.Clients.AsNoTracking().SingleAsync(c => c.Email == "posted@example.com");
        Assert.Null(row.PortalPasswordHash);
        Assert.Null(row.PortalInviteToken);
        Assert.Null(row.PortalActivatedAt);
        Assert.Equal(0, row.FailedSignInCount);
        Assert.Null(row.SignInLockedUntil);
        Assert.Null(row.SignInLockNoticeAt);
    }
}

// ---- admins: the SuperAdmin sign-in and the screens that clear a lock ------------------------------------

public class SignInLockoutAdmin583Tests
{
    [Fact]
    public async Task An_admins_fifth_wrong_password_locks_and_the_sixth_is_refused_even_with_the_right_one()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var adminId = await Lock583.SeedAdminAsync(testDb, "boss");
        var log = new RecordingLogger();

        for (var i = 1; i <= 4; i++)
            Assert.Equal("Invalid credentials.", await Lock583.AdminRefusalAsync(testDb, "boss", "wrong-" + i, log));
        Assert.Equal(AdminController.LockedMessage, await Lock583.AdminRefusalAsync(testDb, "boss", "wrong-5", log));
        Assert.Equal(AdminController.LockedMessage, await Lock583.AdminRefusalAsync(testDb, "boss", Lock583.Password, log));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains($"Admin {adminId}"));

        await using var db = testDb.CreateContext();
        var audit = await db.AdminAuditLogEntries.AsNoTracking().ToListAsync();
        Assert.Single(audit, e => e.Action == "SignInLocked" && e.AdminUserId == adminId);
        Assert.Equal(6, audit.Count(e => e.Action == "LoginFailed"));
    }

    [Fact]
    public async Task SuperAdmin_unlocks_an_admin_or_resets_its_password_and_the_audit_log_says_who()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var adminId = await Lock583.SeedAdminAsync(testDb, "locked-one");
        var superId = await Lock583.SeedAdminAsync(testDb, "owner-admin");
        await Lock583.SetLockAsync(testDb, "AdminUsers", adminId, count: 5, lockedUntil: DateTime.UtcNow.AddMinutes(12));
        var log = new RecordingLogger();

        await using (var db = testDb.CreateContext())
        {
            var controller = Lock583.AdminUsers(db, log, superId, "owner-admin");
            Assert.IsType<RedirectToActionResult>(await controller.UnlockSignIn(adminId));
        }
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "AdminUsers", adminId));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("Sign-in lock cleared (unlocked by owner-admin)"));
        await using (var db = testDb.CreateContext())
        {
            var entry = Assert.Single(await db.AdminAuditLogEntries.AsNoTracking().Where(e => e.Action == "SignInUnlocked").ToListAsync());
            Assert.Equal(superId, entry.AdminUserId);
            Assert.Contains("Unlocked sign-in for admin 'locked-one'", entry.Details);
        }
        Assert.IsType<RedirectToActionResult>(await Lock583.AdminSignInAsync(testDb, "locked-one", Lock583.Password));

        // A new password from Admin Users unlocks too.
        await Lock583.SetLockAsync(testDb, "AdminUsers", adminId, count: 5, lockedUntil: DateTime.UtcNow.AddMinutes(12));
        await using (var db = testDb.CreateContext())
            await Lock583.AdminUsers(db, log, superId, "owner-admin").ResetPassword(adminId, "another-password");
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "AdminUsers", adminId));
        Assert.IsType<RedirectToActionResult>(await Lock583.AdminSignInAsync(testDb, "locked-one", "another-password"));

        // The unlock is SuperAdmin's alone (the whole controller is), as on Agents.
        Assert.Contains("[Authorize(Policy = \"SuperAdmin\")]\npublic class AdminUsersController", Lock583.Read(@"src\IPRO.Admin\Controllers\AdminUsersController.cs"));
    }

    [Fact]
    public async Task SuperAdmin_unlocks_an_agent_their_team_member_and_their_client_from_the_agents_page()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "agentx", "agentx@example.com");
        var otherAgent = await Lock583.SeedAgentAsync(testDb, "agenty", "agenty@example.com");
        var memberId = await Lock583.SeedTeamMemberAsync(testDb, agentId, "staff@example.com");
        var clientId = await Lock583.SeedClientAsync(testDb, agentId, "client@example.com", Lock583.Password);
        var othersClient = await Lock583.SeedClientAsync(testDb, otherAgent, "elsewhere@example.com", Lock583.Password);
        var until = DateTime.UtcNow.AddMinutes(9);
        await Lock583.SetLockAsync(testDb, "AgentUsers", agentId, 5, until);
        await Lock583.SetLockAsync(testDb, "TeamMembers", memberId, 5, until);
        await Lock583.SetLockAsync(testDb, "Clients", clientId, 5, until);
        await Lock583.SetLockAsync(testDb, "Clients", othersClient, 5, until);
        var superId = await Lock583.SeedAdminAsync(testDb, "owner-admin");
        var log = new RecordingLogger();

        async Task<IActionResult> UnlockAsync(SignInAccountKind kind, int accountId)
        {
            await using var db = testDb.CreateContext();
            return await Lock583.Agents(db, log, superId, "owner-admin").UnlockSignIn(agentId, kind, accountId);
        }

        // A client of another agent is not this page's to clear.
        Assert.IsType<NotFoundResult>(await UnlockAsync(SignInAccountKind.Client, othersClient));
        Assert.NotNull(await Lock583.LockedUntilAsync(testDb, "Clients", othersClient));

        Assert.IsType<RedirectToActionResult>(await UnlockAsync(SignInAccountKind.Adviser, 0));
        Assert.IsType<RedirectToActionResult>(await UnlockAsync(SignInAccountKind.TeamMember, memberId));
        Assert.IsType<RedirectToActionResult>(await UnlockAsync(SignInAccountKind.Client, clientId));
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "AgentUsers", agentId));
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "TeamMembers", memberId));
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "Clients", clientId));

        await using (var db = testDb.CreateContext())
        {
            var entries = await db.AdminAuditLogEntries.AsNoTracking().Where(e => e.Action == "SignInUnlocked").Select(e => e.Details).ToListAsync();
            Assert.Equal(3, entries.Count);
            Assert.Contains(entries, d => d.Contains("agent 'agentx'"));
            Assert.Contains(entries, d => d.Contains("team member staff@example.com"));
            Assert.Contains(entries, d => d.Contains("client client@example.com"));
        }
        Assert.Equal(3, log.Lines.Count(l => l.Level == LogLevel.Warning && l.Text.Contains("Sign-in lock cleared (unlocked by owner-admin)")));

        // SuperAdmin only; the Details page shows each lock with its button, disabled for support admins.
        var controller = Lock583.Read(@"src\IPRO.Admin\Controllers\AgentsController.cs");
        Assert.Matches(new Regex(@"\[Authorize\(Policy = ""SuperAdmin""\)\]\s*\[HttpPost, ValidateAntiForgeryToken\]\s*public async Task<IActionResult> UnlockSignIn\("), controller);
        var details = Lock583.Read(@"src\IPRO.Admin\Views\Agents\Details.cshtml");
        Assert.Contains("await UnlockButton(\"TeamMember\", member.Id);", details);
        Assert.Contains("await UnlockButton(\"Client\", client.Id);", details);
        Assert.Contains("disabled title=\"SuperAdmin only: lets them sign in again now\"", details);
    }

    [Fact]
    public async Task SuperAdmins_temporary_password_and_the_break_glass_reset_unlock_too()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "temp", "temp@example.com");
        var superId = await Lock583.SeedAdminAsync(testDb, "owner-admin");
        await Lock583.SetLockAsync(testDb, "AgentUsers", agentId, 5, DateTime.UtcNow.AddMinutes(9));

        await using (var db = testDb.CreateContext())
            Assert.IsType<RedirectToActionResult>(await Lock583.Agents(db, new RecordingLogger(), superId, "owner-admin").ResetPassword(agentId));
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "AgentUsers", agentId));

        Assert.Contains("if (await IPRO.DataAccess.SignInLockout.ClearAsync(db, new IPRO.DataAccess.SignInAccount(IPRO.DataAccess.SignInAccountKind.Admin, user.Id), DateTime.UtcNow))",
            Lock583.Read(@"src\IPRO.Admin\Program.cs"));
    }
}

// ---- the counting itself, the names that are no account, the email, the schema ----------------------------

public class SignInLockout583Tests
{
    [Fact]
    public async Task A_name_that_is_no_account_is_answered_as_an_account_is_on_every_sign_in_page()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var notices = new RecordingLockNotices();
        var log = new RecordingLogger();

        for (var i = 1; i <= 4; i++)
        {
            Assert.Equal("Invalid username or password.", await Lock583.PortalRefusalAsync(testDb, "nobody", "x" + i, notices, log));
            Assert.Equal("Invalid email or password.", await Lock583.ClientRefusalAsync(testDb, "nobody@example.com", "x" + i, notices, log));
            Assert.Equal("Invalid credentials.", await Lock583.AdminRefusalAsync(testDb, "nobody", "x" + i, log));
        }
        Assert.Equal(SignInMessages.PortalLocked, await Lock583.PortalRefusalAsync(testDb, " NOBODY", "x5", notices, log));
        Assert.Equal(SignInMessages.ClientPortalLocked, await Lock583.ClientRefusalAsync(testDb, "nobody@example.com", "x5", notices, log));
        Assert.Equal(AdminController.LockedMessage, await Lock583.AdminRefusalAsync(testDb, "nobody", "x5", log));
        Assert.Equal(SignInMessages.PortalLocked, await Lock583.PortalRefusalAsync(testDb, "nobody", "x6", notices, log));
        Assert.Empty(notices.Queued);   // nobody to email
        Assert.Equal(3, log.Lines.Count(l => l.Level == LogLevel.Warning && l.Text.Contains("a name that is no account")));

        // The name itself is not kept, only a hash of it per page.
        await using var db = testDb.CreateContext();
        var keys = await db.SignInNameLocks.AsNoTracking().Select(n => n.NameKey).ToListAsync();
        Assert.Equal(3, keys.Count);
        Assert.All(keys, k => Assert.Matches("^(portal|client|admin):[0-9a-f]{64}$", k));
        Assert.DoesNotContain(keys, k => k.Contains("nobody"));
    }

    [Fact]
    public async Task Every_refusal_takes_the_same_time_whether_the_name_is_an_account_locked_or_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await Lock583.SeedAgentAsync(testDb, "timed", "timed@example.com");
        var lockedId = await Lock583.SeedAgentAsync(testDb, "already", "already@example.com");
        await Lock583.SetLockAsync(testDb, "AgentUsers", lockedId, 5, DateTime.UtcNow.AddMinutes(10));

        async Task<TimeSpan> TimeAsync(string name, string password)
        {
            var watch = Stopwatch.StartNew();
            await Lock583.PortalRefusalAsync(testDb, name, password, new RecordingLockNotices(), new RecordingLogger());
            return watch.Elapsed;
        }

        // No faster than 1.5 seconds on any of the three ways a sign-in is refused.
        Assert.True(await TimeAsync("timed", "wrong") >= TimeSpan.FromMilliseconds(1490));
        Assert.True(await TimeAsync("already", Lock583.Password) >= TimeSpan.FromMilliseconds(1490));
        Assert.True(await TimeAsync("no-such-name", "wrong") >= TimeSpan.FromMilliseconds(1490));
    }

    [Fact]
    public async Task A_save_of_a_row_loaded_before_the_wrong_passwords_never_writes_the_lock_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "stale", "stale@example.com");

        await using var early = testDb.CreateContext();
        var loaded = await early.AgentUsers.SingleAsync(a => a.Id == agentId);   // count 0, not locked

        await using (var db = testDb.CreateContext())
        {
            for (var i = 0; i < 5; i++)
            {
                Assert.True(await SignInLockout.BeginAsync(db, new SignInAccount(SignInAccountKind.Adviser, agentId), DateTime.UtcNow));
                await SignInLockout.FailedAsync(db, new SignInAccount(SignInAccountKind.Adviser, agentId), DateTime.UtcNow);
            }
        }
        Assert.NotNull(await Lock583.LockedUntilAsync(testDb, "AgentUsers", agentId));

        // SuperAdmin's Edit, a last-login write: Update() marks every column -- but not these.
        loaded.CompanyName = "Edited Elsewhere";
        loaded.FailedSignInCount = 0;
        loaded.SignInLockedUntil = null;
        early.AgentUsers.Update(loaded);
        await early.SaveChangesAsync();

        Assert.NotNull(await Lock583.LockedUntilAsync(testDb, "AgentUsers", agentId));
        Assert.Equal(5, await Lock583.CountAsync(testDb, "AgentUsers", agentId));
        await using var check = testDb.CreateContext();
        Assert.Equal("Edited Elsewhere", (await check.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId)).CompanyName);
    }

    [Fact]
    public async Task One_lock_sends_one_email_and_no_more_than_one_a_day()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "mail", "mail@example.com");
        await Lock583.SetLockAsync(testDb, "AgentUsers", agentId, 5, DateTime.UtcNow.AddDays(2));   // locked throughout
        var account = new SignInAccount(SignInAccountKind.Adviser, agentId);
        var email = new RecordingEmail();
        var now = DateTime.UtcNow;

        await using (var db = testDb.CreateContext())
        {
            Assert.True((await SignInLockNotices.SendAsync(db, email, Lock583.Config, new RecordingLogger(), account, now))!.Success);
            // The same lock again (or a second lock within the day): nothing more.
            Assert.Null(await SignInLockNotices.SendAsync(db, email, Lock583.Config, new RecordingLogger(), account, now.AddMinutes(16)));
            Assert.Null(await SignInLockNotices.SendAsync(db, email, Lock583.Config, new RecordingLogger(), account, now.AddHours(23)));
        }
        Assert.Single(email.Sent);

        // A day later, a new lock sends again.
        await using (var db = testDb.CreateContext())
            Assert.True((await SignInLockNotices.SendAsync(db, email, Lock583.Config, new RecordingLogger(), account, now.AddHours(24).AddMinutes(1)))!.Success);
        Assert.Equal(2, email.Sent.Count);

        // Many at once: still one.
        var burst = await Lock583.SeedAgentAsync(testDb, "burstmail", "burstmail@example.com");
        await Lock583.SetLockAsync(testDb, "AgentUsers", burst, 5, DateTime.UtcNow.AddMinutes(15));
        var burstEmail = new RecordingEmail();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = testDb.CreateContext();
            await SignInLockNotices.SendAsync(db, burstEmail, Lock583.Config, new RecordingLogger(), new SignInAccount(SignInAccountKind.Adviser, burst), now);
        }));
        Assert.Single(burstEmail.Sent);
    }

    [Fact]
    public async Task A_right_password_checked_beside_a_wrong_one_ends_the_lock_the_wrong_one_set_and_no_email_goes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "beside", "beside@example.com");
        await Lock583.SetLockAsync(testDb, "AgentUsers", agentId, count: 3, lockedUntil: null);
        var account = new SignInAccount(SignInAccountKind.Adviser, agentId);
        var now = DateTime.UtcNow;
        await using var db = testDb.CreateContext();

        // Two attempts at the same moment: both are taken (the fourth and fifth), the wrong one is decided
        // first and locks -- after only four wrong passwords. The right one, decided next, ends it.
        Assert.True(await SignInLockout.BeginAsync(db, account, now));
        Assert.True(await SignInLockout.BeginAsync(db, account, now));
        Assert.True(await SignInLockout.FailedAsync(db, account, now));
        Assert.True(await SignInLockout.SucceededAsync(db, account));   // it ended a lock (and says so, for the log)
        Assert.Null(await Lock583.LockedUntilAsync(testDb, "AgentUsers", agentId));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "AgentUsers", agentId));

        // The email the wrong one queued finds the lock over, and sends nothing.
        var email = new RecordingEmail();
        Assert.Null(await SignInLockNotices.SendAsync(db, email, Lock583.Config, new RecordingLogger(), account, now));
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task Past_20_lock_emails_in_an_hour_a_lock_is_logged_and_not_emailed()
    {
        var clock = DateTime.UtcNow;
        var notices = new SignInLockNotices(null!, () => clock);
        for (var i = 0; i < SignInLockNotices.MaxPerHour; i++) Assert.True(notices.TryTakeHourlySlot());
        Assert.False(notices.TryTakeHourlySlot());
        clock = clock.AddMinutes(61);
        Assert.True(notices.TryTakeHourlySlot());

        // A lock refused a slot gives its day back, so a later lock can still email.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "capped", "capped@example.com");
        await Lock583.SetLockAsync(testDb, "AgentUsers", agentId, 5, DateTime.UtcNow.AddMinutes(15));
        var account = new SignInAccount(SignInAccountKind.Adviser, agentId);
        var email = new RecordingEmail();
        var log = new RecordingLogger();
        await using var db = testDb.CreateContext();
        Assert.Null(await SignInLockNotices.SendAsync(db, email, Lock583.Config, log, account, DateTime.UtcNow, () => false));
        Assert.Empty(email.Sent);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("lock emails went out in the last hour"));
        Assert.True((await SignInLockNotices.SendAsync(db, email, Lock583.Config, log, account, DateTime.UtcNow, () => true))!.Success);
        Assert.Single(email.Sent);
    }

    // The review's finding: the database compares names without accents ("jose" finds "Jos" + e-acute),
    // the count for a name that is no account went by its letters, so mixing two spellings reached a real
    // account's lock and never an unknown name's. Now an account is found by its letters too.
    [Fact]
    public async Task A_name_counts_by_its_letters_so_a_spelling_the_database_also_matches_is_no_account()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "jose", "jose@example.com");
        var adminId = await Lock583.SeedAdminAsync(testDb, "rene");
        var accented = "Jos" + (char)0xE9;
        await using (var db = testDb.CreateContext())
            Assert.True(await db.AgentUsers.AnyAsync(a => a.UserName == accented), "this MySQL compares without accents -- the case the rule is for");

        // The right password under the other spelling is not this account's and costs it nothing, as for
        // any name that is no account; so three wrong as "jose" and two as the other spelling are not five.
        Assert.Equal("Invalid username or password.", await Lock583.PortalRefusalAsync(testDb, accented, Lock583.Password, new RecordingLockNotices(), new RecordingLogger()));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "AgentUsers", agentId));
        for (var i = 0; i < 3; i++)
            await Lock583.PortalRefusalAsync(testDb, "jose", "wrong", new RecordingLockNotices(), new RecordingLogger());
        Assert.Equal("Invalid username or password.", await Lock583.PortalRefusalAsync(testDb, accented, "wrong", new RecordingLockNotices(), new RecordingLogger()));
        Assert.Equal(3, await Lock583.CountAsync(testDb, "AgentUsers", agentId));

        Assert.Equal("Invalid credentials.", await Lock583.AdminRefusalAsync(testDb, "Ren" + (char)0xE9, Lock583.Password, new RecordingLogger()));
        Assert.Equal(0, await Lock583.CountAsync(testDb, "AdminUsers", adminId));
        Assert.True(SignInLockout.SameName(" JOSE ", "jose"));
        Assert.False(SignInLockout.SameName("jose", accented));
    }

    [Fact]
    public async Task An_audit_entry_longer_than_its_column_is_cut_not_lost()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using (var db = testDb.CreateContext())
            await new AdminAuditLogService(new UnitOfWork(db)).LogAsync(1, "owner-admin", "SignInUnlocked", "Unlocked sign-in for client " + new string('x', 700));
        await using var check = testDb.CreateContext();
        var entry = await check.AdminAuditLogEntries.AsNoTracking().SingleAsync();
        Assert.Equal(500, entry.Details.Length);
        Assert.EndsWith("...", entry.Details);
    }

    [Fact]
    public async Task A_send_that_fails_gives_the_day_back_so_the_next_lock_tries_again()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "fails", "fails@example.com");
        await Lock583.SetLockAsync(testDb, "AgentUsers", agentId, 5, DateTime.UtcNow.AddMinutes(15));
        var account = new SignInAccount(SignInAccountKind.Adviser, agentId);
        var log = new RecordingLogger();
        await using var db = testDb.CreateContext();

        var failed = await SignInLockNotices.SendAsync(db, new RecordingEmail { Fail = true }, Lock583.Config, log, account, DateTime.UtcNow);
        Assert.False(failed!.Success);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("Sign-in lock email to Adviser"));

        var email = new RecordingEmail();
        Assert.True((await SignInLockNotices.SendAsync(db, email, Lock583.Config, log, account, DateTime.UtcNow.AddMinutes(20)))!.Success);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task The_emails_say_what_happened_and_the_way_back_in()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "words", "words@example.com");
        var memberId = await Lock583.SeedTeamMemberAsync(testDb, agentId, "helper@example.com");
        var clientId = await Lock583.SeedClientAsync(testDb, agentId, "reader@example.com", Lock583.Password);
        foreach (var (table, id) in new[] { ("AgentUsers", agentId), ("TeamMembers", memberId), ("Clients", clientId) })
            await Lock583.SetLockAsync(testDb, table, id, 5, DateTime.UtcNow.AddMinutes(15));
        await using var db = testDb.CreateContext();

        var adviser = (await SignInLockNotices.ComposeAsync(db, Lock583.Config, new SignInAccount(SignInAccountKind.Adviser, agentId)))!;
        Assert.Equal("words@example.com", adviser.To);
        Assert.Equal("Your iPro sign-in was locked for 15 minutes", adviser.Subject);
        Assert.Contains("5 times in a row, so we locked sign-in to your account for 15 minutes", adviser.Html);
        Assert.Contains("reset your password", adviser.Html);
        Assert.Contains("href=\"https://app.example.test/Account/ForgotPassword\"", adviser.Html);
        Assert.Contains("at most once a day", adviser.Html);
        Assert.Null(adviser.Tags);   // iPro's own mail, not the adviser's to a client

        var member = (await SignInLockNotices.ComposeAsync(db, Lock583.Config, new SignInAccount(SignInAccountKind.TeamMember, memberId)))!;
        Assert.Equal("helper@example.com", member.To);
        Assert.Contains("ask Pat Lee to reset it on the My Team page", member.Html);
        Assert.Contains("Lee Advisory's iPro account", member.Html);

        var client = (await SignInLockNotices.ComposeAsync(db, Lock583.Config, new SignInAccount(SignInAccountKind.Client, clientId)))!;
        Assert.Equal("reader@example.com", client.To);
        Assert.Equal("Lee Advisory: Your client portal sign-in was locked for 15 minutes", client.Subject);
        Assert.Contains("reply to this email and we will send you a new invitation", client.Html);
        Assert.Contains("Best regards,", client.Html);   // a letter from the adviser (542)
        Assert.Equal("words@example.com", client.ReplyTo);
        Assert.Equal(EmailStreams.Notify, client.Tags![AdviserSender.StreamTag]);

        Assert.Null(await SignInLockNotices.ComposeAsync(db, Lock583.Config, new SignInAccount(SignInAccountKind.Admin, 1)));   // no address
    }

    [Fact]
    public async Task The_startup_repair_adds_what_the_lock_needs_to_a_database_without_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using (var db = testDb.CreateContext())
        {
            foreach (var table in new[] { "AgentUsers", "TeamMembers", "Clients" })
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE `{table}` DROP COLUMN `FailedSignInCount`, DROP COLUMN `SignInLockedUntil`, DROP COLUMN `SignInLockNoticeAt`");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE `SignInNameLocks`");

            await StartupSchemaRepair.EnsureSignInLockoutSchemaAsync(db);
            await StartupSchemaRepair.EnsureSignInLockoutSchemaAsync(db);   // and again: nothing to do
        }
        var agentId = await Lock583.SeedAgentAsync(testDb, "repaired", "repaired@example.com");
        Assert.Equal("Invalid username or password.", await Lock583.PortalRefusalAsync(testDb, "repaired", "wrong", new RecordingLockNotices(), new RecordingLogger()));
        Assert.Equal(1, await Lock583.CountAsync(testDb, "AgentUsers", agentId));
        Assert.Equal("Invalid username or password.", await Lock583.PortalRefusalAsync(testDb, "no-account", "wrong", new RecordingLockNotices(), new RecordingLogger()));

        foreach (var program in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
            Assert.Contains("StartupSchemaRepair.EnsureSignInLockoutSchemaAsync(db)", Lock583.Read(program));
        var admin = Lock583.Read(@"src\IPRO.Admin\Program.cs");
        Assert.Contains("ADD COLUMN `FailedSignInCount` int NOT NULL DEFAULT 0", admin);
        Assert.Contains("ADD COLUMN `SignInLockedUntil` datetime(6) NULL", admin);
    }

    // Every place a password is checked counts: a new VerifyHashedPassword must come with the lock.
    [Fact]
    public void Every_password_check_goes_through_the_lock()
    {
        var root = Lock583.RepoRoot();
        var checks = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("VerifyHashedPassword("))
            .Select(f => Path.GetRelativePath(root, f).Replace('/', '\\'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[]
        {
            @"src\IPRO.Admin\Controllers\AdminController.cs",
            @"src\IPRO.Business\Services\AgentService.cs",
            @"src\IPRO.Web\Controllers\AccountController.cs",
            @"src\IPRO.Web\Controllers\ClientPortalAccountController.cs",
        }, checks);

        // The agent's check is only reached through PasswordMatches, and every caller of that, and every
        // other file above, runs it inside SignInLockout.TryAsync.
        var service = Lock583.Read(@"src\IPRO.Business\Services\AgentService.cs");
        Assert.Equal(1, Regex.Matches(service, @"VerifyHashedPassword\(").Count);
        Assert.Contains("public bool PasswordMatches(AgentUser user, string password) =>", service);
        var account = Lock583.Read(@"src\IPRO.Web\Controllers\AccountController.cs");
        Assert.Equal(1, Regex.Matches(account, @"VerifyHashedPassword\(").Count);   // TeamPasswordMatches
        Assert.Equal(3, Regex.Matches(account, @"SignInLockout\.TryAsync\(").Count);   // Login, both Change Passwords
        // Two candidates in Login, one in each Change Password: every call is a TryAsync candidate.
        Assert.Equal(4, Regex.Matches(account, @"new SignInCandidate\(new SignInAccount\(SignInAccountKind\.(Adviser|TeamMember), [a-z]+\.Id\), \(\) => (_agents\.PasswordMatches|TeamPasswordMatches)\(").Count);
        Assert.Equal(4, Regex.Matches(account, @"_agents\.PasswordMatches\(|TeamPasswordMatches\(member, ").Count);
        Assert.Contains("SignInLockout.TryAsync(_db, SignInDoor.ClientPortal", Lock583.Read(@"src\IPRO.Web\Controllers\ClientPortalAccountController.cs"));
        Assert.Contains("SignInLockout.TryAsync(_uow.Context, SignInDoor.Admin", Lock583.Read(@"src\IPRO.Admin\Controllers\AdminController.cs"));
        Assert.DoesNotContain("PasswordMatches(", Lock583.SourcesExcept(root, @"src\IPRO.Business\Services\AgentService.cs", @"src\IPRO.Business\Interfaces\IAgentService.cs", @"src\IPRO.Web\Controllers\AccountController.cs", @"src\IPRO.Web\Controllers\ClientPortalAccountController.cs", @"src\IPRO.Admin\Controllers\AdminController.cs", @"src\IPRO.DataAccess\SignInLockout.cs"));
    }
}

// ---- the harness ---------------------------------------------------------------------------------------

internal static class Lock583
{
    public const string Password = "the-right-password";

    public static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.example.test" })
        .Build();

    public static async Task<int> SeedAgentAsync(TestDatabase testDb, string userName, string email)
    {
        await using var db = testDb.CreateContext();
        var agent = new AgentUser
        {
            UserName = userName, Email = email, FirstName = "Pat", LastName = "Lee", CompanyName = "Lee Advisory",
            DomainName = $"{userName}.example.test", Phone = "416-555-0100", IsActive = true
        };
        agent.PasswordHash = new PasswordHasher<AgentUser>().HashPassword(agent, Password);
        db.AgentUsers.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    public static async Task<int> SeedTeamMemberAsync(TestDatabase testDb, int agentId, string email)
    {
        await using var db = testDb.CreateContext();
        var member = new TeamMember { AgentUserId = agentId, FullName = "Alex Helper", Email = email, IsActive = true, MustChangePassword = false };
        member.PasswordHash = new PasswordHasher<TeamMember>().HashPassword(member, Password);
        db.TeamMembers.Add(member);
        await db.SaveChangesAsync();
        return member.Id;
    }

    public static async Task<int> SeedClientAsync(TestDatabase testDb, int agentId, string email, string password)
    {
        await using var db = testDb.CreateContext();
        var client = new Client { AgentUserId = agentId, FirstName = "Kim", LastName = "Reader", Email = email, PortalActivatedAt = DateTime.UtcNow };
        client.PortalPasswordHash = new PasswordHasher<Client>().HashPassword(client, password);
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    public static async Task<int> SeedAdminAsync(TestDatabase testDb, string username)
    {
        await using var db = testDb.CreateContext();
        var admin = new AdminUser { Username = username, FullName = "Admin " + username, Role = AdminRoles.SuperAdmin, IsActive = true };
        admin.PasswordHash = new PasswordHasher<AdminUser>().HashPassword(admin, Password);
        db.AdminUsers.Add(admin);
        await db.SaveChangesAsync();
        return admin.Id;
    }

    // The lock columns are SignInLockout's: tests set them the way it does, with a statement.
    public static async Task SetLockAsync(TestDatabase testDb, string table, int id, int count, DateTime? lockedUntil)
    {
        await using var db = testDb.CreateContext();
        if (lockedUntil is { } until)
            await db.Database.ExecuteSqlRawAsync($"UPDATE `{table}` SET `FailedSignInCount` = {{0}}, `SignInLockedUntil` = {{1}} WHERE `Id` = {{2}}", count, until, id);
        else
            await db.Database.ExecuteSqlRawAsync($"UPDATE `{table}` SET `FailedSignInCount` = {{0}}, `SignInLockedUntil` = NULL WHERE `Id` = {{1}}", count, id);
    }

    public static async Task<int> CountAsync(TestDatabase testDb, string table, int id)
    {
        await using var db = testDb.CreateContext();
        return table switch
        {
            "AgentUsers" => (await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == id)).FailedSignInCount,
            "TeamMembers" => (await db.TeamMembers.AsNoTracking().SingleAsync(a => a.Id == id)).FailedSignInCount,
            "Clients" => (await db.Clients.AsNoTracking().SingleAsync(a => a.Id == id)).FailedSignInCount,
            _ => (await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Id == id)).FailedSignInCount
        };
    }

    public static async Task<DateTime?> LockedUntilAsync(TestDatabase testDb, string table, int id)
    {
        await using var db = testDb.CreateContext();
        var until = table switch
        {
            "AgentUsers" => (await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == id)).SignInLockedUntil,
            "TeamMembers" => (await db.TeamMembers.AsNoTracking().SingleAsync(a => a.Id == id)).SignInLockedUntil,
            "Clients" => (await db.Clients.AsNoTracking().SingleAsync(a => a.Id == id)).SignInLockedUntil,
            _ => (await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Id == id)).SignInLockedUntil
        };
        return SignInLockout.IsLocked(until, DateTime.UtcNow) ? until : null;
    }

    // ---- the sign-in pages ----

    public static AccountController Portal(IPRODbContext db, RecordingLockNotices notices, RecordingLogger log,
        IPasswordHasher<AgentUser>? hasher = null, ClaimsPrincipal? user = null) =>
        Prepare(new AccountController(new AgentService(new UnitOfWork(db), hasher ?? new PasswordHasher<AgentUser>()), null!, new UnitOfWork(db), null!, db,
            null!, null!, log.For<AccountController>(), Config, null!, new PasswordHasher<TeamMember>(), notices), user);

    public static async Task<string?> PortalRefusalAsync(TestDatabase testDb, string name, string password, RecordingLockNotices notices, RecordingLogger log)
    {
        await using var db = testDb.CreateContext();
        var result = await Portal(db, notices, log).Login(name, password);
        Assert.IsType<ViewResult>(result);
        return Error(result);
    }

    public static async Task<IActionResult> PortalSignInAsync(TestDatabase testDb, string name, string password)
    {
        await using var db = testDb.CreateContext();
        return await Portal(db, new RecordingLockNotices(), new RecordingLogger()).Login(name, password);
    }

    public static async Task SetPortalPasswordAsync(TestDatabase testDb, int clientId, string password)
    {
        await using var db = testDb.CreateContext();
        var client = await db.Clients.SingleAsync(c => c.Id == clientId);
        client.PortalPasswordHash = new PasswordHasher<Client>().HashPassword(client, password);
        await db.SaveChangesAsync();
    }

    public sealed class GrantAll : IPRO.Business.Interfaces.IPackageEntitlementService
    {
        public Task<IPRO.Business.Interfaces.PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new IPRO.Business.Interfaces.PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) =>
            Task.FromResult(agentIds.Distinct().ToDictionary(a => a, _ => true));
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    public static ClientPortalAccountController ClientPortal(IPRODbContext db, RecordingLockNotices notices, RecordingLogger log) =>
        Prepare(new ClientPortalAccountController(db, new PasswordHasher<Client>(), notices, log.For<ClientPortalAccountController>()));

    // `adviser` is the ?adviser=<id> of the shared address's sign-in link (586); none = a link without one.
    public static async Task<string?> ClientRefusalAsync(TestDatabase testDb, string email, string password, RecordingLockNotices notices, RecordingLogger log, string? adviser = null)
    {
        await using var db = testDb.CreateContext();
        var result = await ClientPortal(db, notices, log).Login(email, password, null, adviser);
        Assert.IsType<ViewResult>(result);
        return Error(result);
    }

    public static async Task<IActionResult> ClientSignInAsync(TestDatabase testDb, string email, string password, string? adviser = null)
    {
        await using var db = testDb.CreateContext();
        return await ClientPortal(db, new RecordingLockNotices(), new RecordingLogger()).Login(email, password, null, adviser);
    }

    private static AdminController Admin(IPRODbContext db, RecordingLogger log) =>
        Prepare(new AdminController(new UnitOfWork(db), new PasswordHasher<AdminUser>(), new AdminAuditLogService(new UnitOfWork(db)), log.For<AdminController>()));

    public static async Task<string?> AdminRefusalAsync(TestDatabase testDb, string name, string password, RecordingLogger log)
    {
        await using var db = testDb.CreateContext();
        var result = await Admin(db, log).Login(name, password);
        Assert.IsType<ViewResult>(result);
        return Error(result);
    }

    public static async Task<IActionResult> AdminSignInAsync(TestDatabase testDb, string name, string password)
    {
        await using var db = testDb.CreateContext();
        return await Admin(db, new RecordingLogger()).Login(name, password);
    }

    public static AdminUsersController AdminUsers(IPRODbContext db, RecordingLogger log, int adminId, string adminName) =>
        Prepare(new AdminUsersController(new UnitOfWork(db), new PasswordHasher<AdminUser>(), new AdminAuditLogService(new UnitOfWork(db)), log.For<AdminUsersController>()),
            User(new Claim(ClaimTypes.NameIdentifier, adminId.ToString()), new Claim(ClaimTypes.Name, adminName), new Claim("Role", AdminRoles.SuperAdmin)));

    public static AgentsController Agents(IPRODbContext db, RecordingLogger log, int adminId, string adminName) =>
        Prepare(new AgentsController(new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()), null!, new UnitOfWork(db), null!,
                new PasswordHasher<AgentUser>(), log.For<AgentsController>(), new AdminAuditLogService(new UnitOfWork(db)), db, null!),
            User(new Claim(ClaimTypes.NameIdentifier, adminId.ToString()), new Claim(ClaimTypes.Name, adminName), new Claim("Role", AdminRoles.SuperAdmin)));

    public static ClaimsPrincipal User(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    public static T Prepare<T>(T controller, ClaimsPrincipal? user = null) where T : Controller
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService, NoAuthentication>();
        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
        };
        http.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
        http.Features.Set<Microsoft.AspNetCore.Http.Features.ISessionFeature>(new SessionFeature { Session = new MemorySession() });   // ChooseAccount keeps its choices there
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, new NoTempData());
        controller.Url = new NoUrls();
        return controller;
    }

    // RedirectToAction only carries the helper; nothing here renders an address.
    private sealed class NoUrls : Microsoft.AspNetCore.Mvc.IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(Microsoft.AspNetCore.Mvc.Routing.UrlActionContext actionContext) => null;
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => url != null && url.StartsWith('/') && !url.StartsWith("//");
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(Microsoft.AspNetCore.Mvc.Routing.UrlRouteContext routeContext) => null;
    }

    public static string? Error(IActionResult result) =>
        result is ViewResult view
            ? view.ViewData.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault()
            : null;

    public static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    public static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative)).Replace("\r\n", "\n");

    public static string SourcesExcept(string root, params string[] except) => string.Concat(
        Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => !except.Contains(Path.GetRelativePath(root, f).Replace('/', '\\')))
            .Select(File.ReadAllText));

    private sealed class NoAuthentication : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class SessionFeature : Microsoft.AspNetCore.Http.Features.ISessionFeature
    {
        public ISession Session { get; set; } = null!;
    }

    private sealed class MemorySession : ISession
    {
        private readonly Dictionary<string, byte[]> _values = new();
        public bool IsAvailable => true;
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public IEnumerable<string> Keys => _values.Keys;
        public void Clear() => _values.Clear();
        public Task CommitAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => _values.Remove(key);
        public void Set(string key, byte[] value) => _values[key] = value;
        public bool TryGetValue(string key, out byte[] value) => _values.TryGetValue(key, out value!);
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}

public sealed class RecordingLockNotices : ISignInLockNotices
{
    public ConcurrentQueue<SignInAccount> Queued { get; } = new();
    public void Queue(SignInAccount account) => Queued.Enqueue(account);
}

public sealed class RecordingLogger : ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Text)> Lines { get; } = new();
    public ILogger<T> For<T>() => new Typed<T>(this);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue((logLevel, formatter(state, exception)));

    private sealed class Typed<T> : ILogger<T>
    {
        private readonly RecordingLogger _owner;
        public Typed(RecordingLogger owner) => _owner = owner;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _owner.Log(logLevel, eventId, state, exception, formatter);
    }
}

internal sealed class CountingHasher : IPasswordHasher<AgentUser>
{
    private readonly PasswordHasher<AgentUser> _inner = new();
    private int _verified;
    public int Verified => _verified;
    public string HashPassword(AgentUser user, string password) => _inner.HashPassword(user, password);
    public PasswordVerificationResult VerifyHashedPassword(AgentUser user, string hashedPassword, string providedPassword)
    {
        System.Threading.Interlocked.Increment(ref _verified);
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}

internal sealed class RecordingEmail : IEmailService
{
    public bool Fail { get; init; }
    public ConcurrentQueue<(string To, string Subject, string Html)> Sent { get; } = new();
    public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
        IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
        (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
    public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
        IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
    {
        if (Fail) return Task.FromResult(EmailSendResult.FailedTransient("provider said no"));
        Sent.Enqueue((toEmail, subject, htmlBody));
        return Task.FromResult(EmailSendResult.Sent("id-" + Sent.Count));
    }
    public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
    public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
}
