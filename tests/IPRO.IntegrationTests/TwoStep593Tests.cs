using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using IPRO.Utility;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace IPRO.IntegrationTests;

// 593 (2026-10-20): two-step sign-in for the adviser portal. An experienced founder the owner demoed iPro
// to: advisers hold their clients' personal and financial information, so a stolen password alone must not
// open an account.
//
// These drive the real actions -- the sign-in page, the forgot-password reset, the code page, the person's
// own two-step pages, My Team, SuperAdmin -- against a throwaway MySQL database, carrying cookies from one
// request to the next as a browser would (TwoStepBrowser). Codes are worked out from the secret the set-up
// page shows, as the person's app would.

// ---- the code an authenticator app shows -------------------------------------------------------------------

public class TwoStepCodes593Tests
{
    // RFC 6238 appendix B (SHA1, the 20-byte ASCII secret "12345678901234567890"): the last 6 of the 8 digits.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void The_codes_are_the_standards_test_vectors(long unixSeconds, string expected)
    {
        var secret = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");
        var at = DateTime.UnixEpoch.AddSeconds(unixSeconds);
        Assert.Equal(expected, Totp.CodeAt(secret, Totp.StepAt(at)));
        Assert.Equal(Totp.StepAt(at), Totp.MatchStep(secret, expected, at, -1));
    }

    [Fact]
    public void One_step_of_clock_drift_either_way_is_accepted_two_are_not_and_a_used_step_never_again()
    {
        var secret = Totp.NewSecret();
        var now = new DateTime(2026, 10, 20, 12, 0, 15, DateTimeKind.Utc);
        var step = Totp.StepAt(now);
        Assert.Equal(step - 1, Totp.MatchStep(secret, Totp.CodeAt(secret, step - 1), now, -1));
        Assert.Equal(step + 1, Totp.MatchStep(secret, Totp.CodeAt(secret, step + 1), now, -1));
        Assert.Null(Totp.MatchStep(secret, Totp.CodeAt(secret, step - 2), now, -1));
        Assert.Null(Totp.MatchStep(secret, Totp.CodeAt(secret, step + 2), now, -1));
        // The step already used, or an earlier one, is refused; a later one is not.
        Assert.Null(Totp.MatchStep(secret, Totp.CodeAt(secret, step), now, step));
        Assert.Null(Totp.MatchStep(secret, Totp.CodeAt(secret, step - 1), now, step));
        Assert.Equal(step + 1, Totp.MatchStep(secret, Totp.CodeAt(secret, step + 1), now, step));
        // Spaces and a dash typed between the digits are fine; letters are not a code.
        var code = Totp.CodeAt(secret, step);
        Assert.Equal(step, Totp.MatchStep(secret, code[..3] + " " + code[3..], now, -1));
        Assert.Equal(step, Totp.MatchStep(secret, code[..3] + "-" + code[3..], now, -1));
        Assert.Null(Totp.MatchStep(secret, "abcdef", now, -1));
        Assert.Null(Totp.MatchStep(secret, "", now, -1));
    }

    [Fact]
    public void The_key_people_type_and_the_QR_code_both_carry_the_secret()
    {
        var secret = Totp.NewSecret();
        Assert.Equal(secret, Totp.FromBase32(Totp.ToBase32(secret)));
        Assert.Equal(secret, Totp.FromBase32(Totp.GroupedKey(secret).ToLowerInvariant()));
        var uri = Totp.OtpAuthUri("iPro", "pat@lee.example.com", secret);
        Assert.StartsWith("otpauth://totp/iPro:pat%40lee.example.com?secret=" + Totp.ToBase32(secret), uri);
        Assert.Contains("&issuer=iPro&algorithm=SHA1&digits=6&period=30", uri);
        var svg = QrSvg.Render(uri, "QR code");
        Assert.StartsWith("<svg", svg);
        Assert.Contains("<path d=\"M", svg);
    }

    [Fact]
    public void Recovery_codes_are_ten_different_codes_stored_only_as_slow_salted_hashes()
    {
        var codes = TwoStepStore.NewRecoveryCodes();
        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[2-9a-hjkmnp-z]{5}-[2-9a-hjkmnp-z]{5}$", c));
        var hash = TwoStepStore.HashRecoveryCode(codes[0]);
        Assert.StartsWith("v1:", hash);
        Assert.DoesNotContain(codes[0].Replace("-", ""), hash);
        Assert.NotEqual(hash, TwoStepStore.HashRecoveryCode(codes[0]));   // salted
        Assert.Equal(TwoStepStore.NormalizeRecoveryCode(codes[0]), TwoStepStore.NormalizeRecoveryCode(" " + codes[0].ToUpperInvariant().Replace("-", " ") + " "));
    }

    [Fact]
    public void The_support_number_in_the_emails_is_the_one_on_the_public_pages() =>
        Assert.Equal(PlatformContact.SupportPhone, TwoStepLetters.SupportPhone);
}

// ---- setting it up, and signing in with it --------------------------------------------------------------------

public class TwoStepSignIn593Tests
{
    [Fact]
    public async Task Setting_up_an_authenticator_needs_the_inbox_and_a_right_code_and_shows_ten_recovery_codes_once()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "patlee", "pat@lee.example.com");
        var browser = new TwoStepBrowser(testDb);
        await browser.SignInWithPasswordAsync("patlee");

        // Either way starts with a code to the email on file; then the QR code.
        var intro = Assert.IsType<TwoStepSetupViewModel>(Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.Setup("app"))).Model);
        Assert.Equal(TwoStepMethods.App, intro.Method);
        Assert.False(intro.EmailSent);
        Assert.Empty(intro.QrSvg);
        var setup = Assert.IsType<TwoStepSetupViewModel>(Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.StartEmail("app"))).Model);
        Assert.True(setup.EmailSent);
        Assert.Contains("<svg", setup.QrSvg);
        var secret = Totp.FromBase32(setup.ManualKey)!;
        var emailed = Assert.Single(browser.Notices.Codes, c => c.ForSetup).Code;

        // Another code straight away is refused (one a minute), and the QR code stays the one scanned.
        var again = Assert.IsType<TwoStepSetupViewModel>(Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.StartEmail("app", setup.SetupToken))).Model);
        Assert.Equal(TwoStepMessages.SetupCodeTooSoon, again.Error);
        Assert.Equal(setup.ManualKey, again.ManualKey);
        Assert.Single(browser.Notices.Codes, c => c.ForSetup);

        // A wrong app code, or a wrong or missing emailed code: nothing is turned on. No password is asked
        // (the owner, 2026-10-21): the emailed code is the proof that it is them.
        var wrongCode = Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.Confirm(setup.SetupToken, browser.WrongCodeFor(secret), emailed)));
        Assert.Equal(TwoStepMessages.WrongCode, Assert.IsType<TwoStepSetupViewModel>(wrongCode.Model).Error);
        var wrongEmailed = Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.Confirm(setup.SetupToken, browser.CodeFor(secret), emailed == "000000" ? "111111" : "000000")));
        Assert.Equal(TwoStepMessages.WrongSetupCode, Assert.IsType<TwoStepSetupViewModel>(wrongEmailed.Model).Error);
        var noEmailed = Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.Confirm(setup.SetupToken, browser.CodeFor(secret), null)));
        Assert.Equal(TwoStepMessages.WrongSetupCode, Assert.IsType<TwoStepSetupViewModel>(noEmailed.Model).Error);
        Assert.Null(await TwoStep593.RowAsync(testDb, TwoStepAccount.Adviser(agentId)));

        var done = Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.Confirm(setup.SetupToken, browser.CodeFor(secret), emailed)));
        Assert.Equal("RecoveryCodes", done.ViewName);
        var codes = Assert.IsType<TwoStepRecoveryCodesViewModel>(done.Model).Codes;
        Assert.Equal(10, codes.Count);

        var row = await TwoStep593.RowAsync(testDb, TwoStepAccount.Adviser(agentId));
        Assert.NotNull(row);
        Assert.Equal(TwoStepMethods.App, row!.Method);
        Assert.DoesNotContain(Totp.ToBase32(secret), row.ProtectedSecret);   // the secret is never stored readable
        await using (var db = testDb.CreateContext())
        {
            var hashes = await db.TwoStepRecoveryCodes.AsNoTracking().Where(r => r.AgentUserId == agentId).Select(r => r.CodeHash).ToListAsync();
            Assert.Equal(10, hashes.Count);
            Assert.All(codes, c => Assert.DoesNotContain(hashes, h => h.Contains(c.Replace("-", ""))));
        }
        // This session goes on with the new stamp; the email went out.
        Assert.Equal(row.Stamp, browser.SignedIn!.FindFirstValue(TwoStepGate.ClaimType));
        Assert.Contains(browser.Notices.Queued, n => n.Notice == TwoStepNotice.TurnedOn && n.Account == TwoStepAccount.Adviser(agentId));
    }

    [Fact]
    public async Task With_two_step_on_the_right_password_alone_signs_nobody_in_and_the_code_finishes_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "robin", "robin@example.com");
        var (secret, _) = await TwoStep593.TurnOnAppAsync(testDb, "robin");

        var browser = new TwoStepBrowser(testDb);
        var afterPassword = await browser.AccountAsync(c => c.Login("robin", Lock583.Password, rememberMe: true, returnUrl: "/portal/Clients"));
        Assert.Equal(TwoStepGate.VerifyPath, Assert.IsType<RedirectResult>(afterPassword).Url);
        Assert.Null(browser.SignedIn);
        Assert.True(browser.Cookies.ContainsKey(TwoStepGate.PendingCookie));

        var page = Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.Verify()));
        Assert.Equal(TwoStepMethods.App, Assert.IsType<TwoStepVerifyViewModel>(page.Model).Method);

        var wrong = Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.Verify(browser.WrongCodeFor(secret))));
        Assert.Equal(TwoStepMessages.WrongCode, Assert.IsType<TwoStepVerifyViewModel>(wrong.Model).Error);
        Assert.Null(browser.SignedIn);

        var code = browser.CodeFor(secret);
        Assert.Equal("/portal/Clients", Assert.IsType<LocalRedirectResult>(await browser.TwoStepAsync(c => c.Verify(code))).Url);
        Assert.NotNull(browser.SignedIn);
        Assert.Equal(agentId.ToString(), browser.SignedIn!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal((await TwoStep593.RowAsync(testDb, TwoStepAccount.Adviser(agentId)))!.Stamp, browser.SignedIn.FindFirstValue(TwoStepGate.ClaimType));
        Assert.True(browser.SignedInProperties!.IsPersistent);   // "Remember me" was ticked on the password page
        Assert.False(browser.Cookies.ContainsKey(TwoStepGate.PendingCookie));

        // The same code never works twice, even straight away with the right password again.
        var again = new TwoStepBrowser(testDb);
        Assert.IsType<RedirectResult>(await again.AccountAsync(c => c.Login("robin@example.com", Lock583.Password)));
        Assert.Equal(TwoStepMessages.WrongCode, Assert.IsType<TwoStepVerifyViewModel>(Assert.IsType<ViewResult>(await again.TwoStepAsync(c => c.Verify(code))).Model).Error);
        Assert.Null(again.SignedIn);
    }

    [Fact]
    public async Task A_code_page_without_the_password_step_or_after_ten_minutes_goes_back_to_the_sign_in_page()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await Lock583.SeedAgentAsync(testDb, "late", "late@example.com");
        var (secret, _) = await TwoStep593.TurnOnAppAsync(testDb, "late");

        var stranger = new TwoStepBrowser(testDb);
        Assert.Equal("/portal/Account/Login", Assert.IsType<RedirectResult>(await stranger.TwoStepAsync(c => c.Verify(stranger.CodeFor(secret)))).Url);
        Assert.Null(stranger.SignedIn);

        var slow = new TwoStepBrowser(testDb);
        await slow.AccountAsync(c => c.Login("late", Lock583.Password));
        slow.Shift = TimeSpan.FromMinutes(11);
        Assert.Equal("/portal/Account/Login", Assert.IsType<RedirectResult>(await slow.TwoStepAsync(c => c.Verify(slow.CodeFor(secret)))).Url);
        Assert.Null(slow.SignedIn);
    }

    [Fact]
    public async Task Five_wrong_codes_in_a_row_lock_for_15_minutes_with_one_email_and_a_new_password_does_not_end_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "guess", "guess@example.com");
        var (secret, _) = await TwoStep593.TurnOnAppAsync(testDb, "guess");
        var account = TwoStepAccount.Adviser(agentId);

        var browser = new TwoStepBrowser(testDb);
        await browser.AccountAsync(c => c.Login("guess", Lock583.Password));
        for (var i = 1; i <= 4; i++)
            Assert.Equal(TwoStepMessages.WrongCode, await browser.VerifyErrorAsync(browser.WrongCodeFor(secret)));
        Assert.Empty(browser.LockNotices.CodeLocks);

        Assert.Equal(TwoStepMessages.LockedAdviser, await browser.VerifyErrorAsync(browser.WrongCodeFor(secret)));
        Assert.Equal(account, Assert.Single(browser.LockNotices.CodeLocks));
        Assert.Contains(browser.Log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("Two-step sign-in locked for 15 minutes after 5 wrong codes in a row") && l.Text.Contains($"Adviser {agentId}"));
        var row = await TwoStep593.RowAsync(testDb, account);
        Assert.InRange(row!.CodeLockedUntil!.Value, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(15.1));

        // Locked: the right code is refused too, and no second email.
        Assert.Equal(TwoStepMessages.LockedAdviser, await browser.VerifyErrorAsync(browser.CodeFor(secret)));
        Assert.Null(browser.SignedIn);
        Assert.Single(browser.LockNotices.CodeLocks);

        // Forgot password ends a PASSWORD lock (583), not this one: whoever has the password and the inbox
        // would otherwise reset, guess five more, and reset again.
        string token;
        await using (var db = testDb.CreateContext())
            token = (await new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()).InitiatePasswordResetAsync("guess@example.com"))!.PasswordResetToken!;
        var reset = new TwoStepBrowser(testDb);
        Assert.Equal(TwoStepGate.VerifyPath, Assert.IsType<RedirectResult>(await reset.AccountAsync(c => c.ResetPassword(token, "a-brand-new-one", "a-brand-new-one"))).Url);
        Assert.Equal(TwoStepMessages.LockedAdviser, await reset.VerifyErrorAsync(reset.CodeFor(secret)));
        Assert.Null(reset.SignedIn);

        // After 15 minutes the right code works and the count starts again.
        var later = new TwoStepBrowser(testDb) { Shift = TimeSpan.FromMinutes(16) };
        await later.AccountAsync(c => c.Login("guess", "a-brand-new-one"));
        Assert.IsType<RedirectResult>(await later.TwoStepAsync(c => c.Verify(later.CodeFor(secret))));
        Assert.NotNull(later.SignedIn);
        Assert.Equal(0, (await TwoStep593.RowAsync(testDb, account))!.FailedCodeCount);
    }

    [Fact]
    public async Task The_forgot_password_reset_does_not_skip_the_second_step()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "sam", "sam@example.com");
        var (secret, _) = await TwoStep593.TurnOnAppAsync(testDb, "sam");

        string token;
        await using (var db = testDb.CreateContext())
            token = (await new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()).InitiatePasswordResetAsync("sam@example.com"))!.PasswordResetToken!;

        var browser = new TwoStepBrowser(testDb);
        Assert.Equal(TwoStepGate.VerifyPath, Assert.IsType<RedirectResult>(await browser.AccountAsync(c => c.ResetPassword(token, "a-brand-new-one", "a-brand-new-one"))).Url);
        Assert.Null(browser.SignedIn);
        Assert.IsType<RedirectResult>(await browser.TwoStepAsync(c => c.Verify(browser.CodeFor(secret))));
        Assert.Equal(agentId.ToString(), browser.SignedIn!.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task A_team_member_signs_in_with_their_own_two_step_as_themself()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "owner", "owner@example.com");
        var memberId = await Lock583.SeedTeamMemberAsync(testDb, agentId, "assistant@example.com");
        var (secret, _) = await TwoStep593.TurnOnAppAsync(testDb, "assistant@example.com");
        Assert.NotNull(await TwoStep593.RowAsync(testDb, TwoStepAccount.Member(agentId, memberId)));
        Assert.Null(await TwoStep593.RowAsync(testDb, TwoStepAccount.Adviser(agentId)));   // the owner's is their own

        var browser = new TwoStepBrowser(testDb);
        Assert.Equal(TwoStepGate.VerifyPath, Assert.IsType<RedirectResult>(await browser.AccountAsync(c => c.Login("assistant@example.com", Lock583.Password))).Url);
        Assert.Null(browser.SignedIn);
        Assert.IsType<RedirectResult>(await browser.TwoStepAsync(c => c.Verify(browser.CodeFor(secret))));
        Assert.Equal(memberId.ToString(), browser.SignedIn!.FindFirstValue("TeamMemberId"));
        Assert.Equal(agentId.ToString(), browser.SignedIn.FindFirstValue(ClaimTypes.NameIdentifier));

        // The owner, with two-step off, still signs in with the password.
        var owner = new TwoStepBrowser(testDb);
        Assert.Equal("/portal/Dashboard", Assert.IsType<RedirectResult>(await owner.AccountAsync(c => c.Login("owner", Lock583.Password))).Url);
        Assert.NotNull(owner.SignedIn);
    }

    [Fact]
    public async Task A_remembered_device_skips_the_code_for_30_days_until_the_password_changes_or_two_step_is_turned_off()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "device", "device@example.com");
        var (secret, _) = await TwoStep593.TurnOnAppAsync(testDb, "device");

        var browser = new TwoStepBrowser(testDb);
        await browser.AccountAsync(c => c.Login("device", Lock583.Password));
        Assert.IsType<RedirectResult>(await browser.TwoStepAsync(c => c.Verify(browser.CodeFor(secret), rememberDevice: true)));
        Assert.Contains(browser.Cookies.Keys, k => k.StartsWith(TwoStepGate.DeviceCookiePrefix));

        // Next time, on this browser, the password is enough -- and the session still carries the stamp.
        browser.SignOut();
        Assert.Equal("/portal/Dashboard", Assert.IsType<RedirectResult>(await browser.AccountAsync(c => c.Login("device", Lock583.Password))).Url);
        Assert.Equal((await TwoStep593.RowAsync(testDb, TwoStepAccount.Adviser(agentId)))!.Stamp, browser.SignedIn!.FindFirstValue(TwoStepGate.ClaimType));

        // Another browser is asked; a wrong password is still a wrong password.
        var other = new TwoStepBrowser(testDb);
        Assert.IsType<RedirectResult>(await other.AccountAsync(c => c.Login("device", Lock583.Password)));
        Assert.Null(other.SignedIn);
        browser.SignOut();
        Assert.Equal("Invalid username or password.", Lock583.Error(await browser.AccountAsync(c => c.Login("device", "wrong"))));

        // After 30 days it asks again.
        browser.Shift = TimeSpan.FromDays(31);
        Assert.Equal(TwoStepGate.VerifyPath, Assert.IsType<RedirectResult>(await browser.AccountAsync(c => c.Login("device", Lock583.Password))).Url);
        Assert.Null(browser.SignedIn);
        browser.Shift = TimeSpan.Zero;

        // A new password ends it.
        Assert.Equal("/portal/Dashboard", Assert.IsType<RedirectResult>(await browser.AccountAsync(c => c.Login("device", Lock583.Password))).Url);
        Assert.NotNull(browser.SignedIn);
        Assert.IsType<RedirectResult>(await browser.AccountAsync(c => c.ChangePassword(Lock583.Password, "another-password-1", "another-password-1")));
        browser.SignOut();
        Assert.Equal(TwoStepGate.VerifyPath, Assert.IsType<RedirectResult>(await browser.AccountAsync(c => c.Login("device", "another-password-1"))).Url);
        Assert.Null(browser.SignedIn);
    }

    [Fact]
    public async Task Turning_it_off_and_on_again_ends_a_remembered_device_and_every_older_session()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "cycle", "cycle@example.com");
        var (secret, _) = await TwoStep593.TurnOnAppAsync(testDb, "cycle");

        var browser = new TwoStepBrowser(testDb);
        await browser.AccountAsync(c => c.Login("cycle", Lock583.Password));
        Assert.IsType<RedirectResult>(await browser.TwoStepAsync(c => c.Verify(browser.CodeFor(secret), rememberDevice: true)));
        var oldSession = browser.SignedIn!;
        var oldDevice = browser.Cookies.Where(c => c.Key.StartsWith(TwoStepGate.DeviceCookiePrefix)).ToList();

        // Turning it off needs the password and a code.
        browser.Shift = TimeSpan.FromSeconds(31);
        Assert.IsType<RedirectToActionResult>(await browser.TwoStepAsync(c => c.TurnOff(Lock583.Password, "123")));
        Assert.NotNull(await TwoStep593.RowAsync(testDb, TwoStepAccount.Adviser(agentId)));
        Assert.IsType<RedirectToActionResult>(await browser.TwoStepAsync(c => c.TurnOff("wrong", browser.CodeFor(secret))));
        Assert.NotNull(await TwoStep593.RowAsync(testDb, TwoStepAccount.Adviser(agentId)));
        Assert.IsType<RedirectToActionResult>(await browser.TwoStepAsync(c => c.TurnOff(Lock583.Password, browser.CodeFor(secret))));
        Assert.Null(await TwoStep593.RowAsync(testDb, TwoStepAccount.Adviser(agentId)));
        Assert.Null(browser.SignedIn!.FindFirstValue(TwoStepGate.ClaimType));
        Assert.Contains(browser.Notices.Queued, n => n.Notice == TwoStepNotice.TurnedOff);

        // A session from before it was turned off is ended; this one goes on.
        await using (var db = testDb.CreateContext())
        {
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Reject, await TwoStep593.VerdictAsync(db, oldSession));
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Ok, await TwoStep593.VerdictAsync(db, browser.SignedIn!));
        }

        // On again (a new secret, a new stamp): the old remembered device asks for a code again.
        var (newSecret, _) = await TwoStep593.TurnOnAppAsync(testDb, "cycle", TimeSpan.FromMinutes(2));
        var fresh = new TwoStepBrowser(testDb) { Shift = TimeSpan.FromMinutes(2) };
        foreach (var cookie in oldDevice) fresh.Cookies[cookie.Key] = cookie.Value;
        Assert.Equal(TwoStepGate.VerifyPath, Assert.IsType<RedirectResult>(await fresh.AccountAsync(c => c.Login("cycle", Lock583.Password))).Url);
        Assert.Null(fresh.SignedIn);
        Assert.IsType<RedirectResult>(await fresh.TwoStepAsync(c => c.Verify(fresh.CodeFor(newSecret))));
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once_and_says_how_many_are_left()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "lost", "lost@example.com");
        var (_, codes) = await TwoStep593.TurnOnAppAsync(testDb, "lost");

        var browser = new TwoStepBrowser(testDb);
        await browser.AccountAsync(c => c.Login("lost", Lock583.Password));
        Assert.IsType<RedirectResult>(await browser.TwoStepAsync(c => c.Verify(codes[3].ToUpperInvariant())));
        Assert.NotNull(browser.SignedIn);
        Assert.Contains(browser.Notices.Queued, n => n.Notice == TwoStepNotice.RecoveryCodeUsed && n.Detail == "9");

        var again = new TwoStepBrowser(testDb);
        await again.AccountAsync(c => c.Login("lost", Lock583.Password));
        Assert.Equal(TwoStepMessages.WrongCode, await again.VerifyErrorAsync(codes[3]));
        Assert.Null(again.SignedIn);
        Assert.IsType<RedirectResult>(await again.TwoStepAsync(c => c.Verify(codes[4])));
        await using var db = testDb.CreateContext();
        Assert.Equal(8, await TwoStepStore.RecoveryCodesLeftAsync(db, TwoStepAccount.Adviser(agentId)));
    }

    [Fact]
    public async Task Email_codes_are_sent_after_the_password_work_once_for_ten_minutes_and_are_not_sent_more_than_once_a_minute()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "inbox", "inbox@example.com");
        var account = TwoStepAccount.Adviser(agentId);

        // Set up by email: a code to confirm the address.
        var setupBrowser = new TwoStepBrowser(testDb);
        await setupBrowser.SignInWithPasswordAsync("inbox");
        var started = Assert.IsType<TwoStepSetupViewModel>(Assert.IsType<ViewResult>(await setupBrowser.TwoStepAsync(c => c.StartEmail("email"))).Model);
        Assert.True(started.EmailSent);
        Assert.Empty(started.QrSvg);
        var setupCode = Assert.Single(setupBrowser.Notices.Codes, c => c.ForSetup).Code;
        Assert.Equal("RecoveryCodes", Assert.IsType<ViewResult>(await setupBrowser.TwoStepAsync(c => c.Confirm(started.SetupToken, null, setupCode))).ViewName);
        Assert.Equal(TwoStepMethods.Email, (await TwoStep593.RowAsync(testDb, account))!.Method);

        // Signing in sends a code by itself.
        var browser = new TwoStepBrowser(testDb);
        Assert.Equal(TwoStepGate.VerifyPath, Assert.IsType<RedirectResult>(await browser.AccountAsync(c => c.Login("inbox", Lock583.Password))).Url);
        var first = Assert.Single(browser.Notices.Codes, c => !c.ForSetup);
        Assert.Equal(account, first.Account);
        var page = Assert.IsType<TwoStepVerifyViewModel>(Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.Verify())).Model);
        Assert.Equal("i***@example.com", page.MaskedEmail);

        // Another one straight away is refused (one a minute); the code works once.
        await browser.TwoStepAsync(c => c.SendCode());
        Assert.Single(browser.Notices.Codes, c => !c.ForSetup);
        Assert.IsType<RedirectResult>(await browser.TwoStepAsync(c => c.Verify(first.Code)));
        Assert.NotNull(browser.SignedIn);

        // A minute later the next sign-in gets a new code; the used one is refused.
        var again = new TwoStepBrowser(testDb) { Shift = TimeSpan.FromMinutes(2) };
        await again.AccountAsync(c => c.Login("inbox", Lock583.Password));
        var second = Assert.Single(again.Notices.Codes).Code;
        Assert.Equal(TwoStepMessages.WrongCode, await again.VerifyErrorAsync(first.Code == second ? "000000" : first.Code));
        Assert.Null(again.SignedIn);

        // A code stops working after ten minutes, and a new one replaces the one before it.
        await using var db = testDb.CreateContext();
        var stamp = (await TwoStepStore.FindAsync(db, account))!.Stamp;
        var t0 = DateTime.UtcNow.AddHours(3);
        var late = await TwoStepStore.IssueEmailCodeAsync(db, account, stamp, t0);
        Assert.False(await TwoStepStore.UseEmailCodeAsync(db, account, stamp, late, t0.AddMinutes(11)));
        var replaced = await TwoStepStore.IssueEmailCodeAsync(db, account, stamp, t0.AddMinutes(12));
        var newest = await TwoStepStore.IssueEmailCodeAsync(db, account, stamp, t0.AddMinutes(14));
        Assert.False(replaced == newest || await TwoStepStore.UseEmailCodeAsync(db, account, stamp, replaced, t0.AddMinutes(15)));
        Assert.True(await TwoStepStore.UseEmailCodeAsync(db, account, stamp, newest, t0.AddMinutes(15)));
        Assert.False(await TwoStepStore.UseEmailCodeAsync(db, account, stamp, newest, t0.AddMinutes(15)));
    }

    [Fact]
    public async Task No_more_than_five_email_codes_an_hour()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "flood", "flood@example.com");
        await TwoStep593.TurnOnEmailAsync(testDb, agentId);

        var browser = new TwoStepBrowser(testDb);
        for (var i = 0; i < 7; i++)
        {
            browser.Shift = TimeSpan.FromMinutes(2 * i);
            await browser.AccountAsync(c => c.Login("flood", Lock583.Password));
        }
        Assert.Equal(5, browser.Notices.Codes.Count(c => !c.ForSetup));
        browser.Shift = TimeSpan.FromMinutes(75);
        await browser.AccountAsync(c => c.Login("flood", Lock583.Password));
        Assert.Equal(6, browser.Notices.Codes.Count(c => !c.ForSetup));
    }

    [Fact]
    public async Task New_recovery_codes_need_the_password_and_a_code_and_the_old_ones_stop_working()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await Lock583.SeedAgentAsync(testDb, "renew", "renew@example.com");
        var (secret, codes) = await TwoStep593.TurnOnAppAsync(testDb, "renew");

        var browser = new TwoStepBrowser(testDb);
        await browser.AccountAsync(c => c.Login("renew", Lock583.Password));
        await browser.TwoStepAsync(c => c.Verify(browser.CodeFor(secret)));
        browser.Shift = TimeSpan.FromSeconds(31);
        Assert.IsType<RedirectToActionResult>(await browser.TwoStepAsync(c => c.NewRecoveryCodes(Lock583.Password, "")));
        var made = Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.NewRecoveryCodes(Lock583.Password, browser.CodeFor(secret))));
        var fresh = Assert.IsType<TwoStepRecoveryCodesViewModel>(made.Model).Codes;

        var other = new TwoStepBrowser(testDb);
        await other.AccountAsync(c => c.Login("renew", Lock583.Password));
        Assert.Equal(TwoStepMessages.WrongCode, await other.VerifyErrorAsync(codes[0]));
        Assert.IsType<RedirectResult>(await other.TwoStepAsync(c => c.Verify(fresh[0])));
    }
}

// ---- sessions, and every place a session cookie is made ------------------------------------------------------

public class TwoStepSessions593Tests
{
    [Fact]
    public async Task A_session_from_before_two_step_was_turned_on_is_ended_and_a_required_account_must_set_it_up()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "older", "older@example.com");

        // A "Remember me" session made before two-step existed for this account.
        var before = new TwoStepBrowser(testDb);
        await before.AccountAsync(c => c.Login("older", Lock583.Password, rememberMe: true));
        var oldCookie = before.SignedIn!;
        await using (var db = testDb.CreateContext())
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Ok, await TwoStep593.VerdictAsync(db, oldCookie));

        await TwoStep593.TurnOnAppAsync(testDb, "older");
        await using (var db = testDb.CreateContext())
        {
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Reject, await TwoStep593.VerdictAsync(db, oldCookie));
            var stamp = (await TwoStep593.RowAsync(testDb, TwoStepAccount.Adviser(agentId)))!.Stamp;
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Ok, await TwoStep593.VerdictAsync(db, TwoStep593.With(oldCookie, stamp)));
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Reject, await TwoStep593.VerdictAsync(db, TwoStep593.With(oldCookie, "not-the-stamp")));
        }

        // Required by SuperAdmin, not yet set up: the session may only set it up.
        var otherId = await Lock583.SeedAgentAsync(testDb, "required", "required@example.com");
        var required = new TwoStepBrowser(testDb);
        await required.AccountAsync(c => c.Login("required", Lock583.Password));
        await using (var db = testDb.CreateContext())
        {
            await TwoStepStore.SetRequiredAsync(db, otherId, true, "owner-admin", DateTime.UtcNow);
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.SetupRequired, await TwoStep593.VerdictAsync(db, required.SignedIn!));
        }
        var program = TwoStep593.Read(@"src\IPRO.Web\Program.cs");
        Assert.Contains("if (twoStepSetupRequired && !canChangePassword && !canLogout && !canUseTwoStep)", program);
        Assert.Contains("context.Response.Redirect(IPRO.Web.Infrastructure.TwoStepGate.SetupPath);", program);
        Assert.Contains("context.HttpContext.Items[TwoStepGate.SetupRequiredItem] = true;", TwoStep593.Read(@"src\IPRO.Web\Infrastructure\AgentCookieRevalidator.cs"));
    }

    [Fact]
    public async Task A_team_members_profile_edit_or_colour_change_keeps_them_a_team_member_with_their_stamp()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "boss", "boss@example.com");
        var memberId = await Lock583.SeedTeamMemberAsync(testDb, agentId, "helper@example.com");
        var (secret, _) = await TwoStep593.TurnOnAppAsync(testDb, "helper@example.com");

        var browser = new TwoStepBrowser(testDb);
        await browser.AccountAsync(c => c.Login("helper@example.com", Lock583.Password));
        await browser.TwoStepAsync(c => c.Verify(browser.CodeFor(secret)));
        var stamp = browser.SignedIn!.FindFirstValue(TwoStepGate.ClaimType);
        Assert.NotNull(stamp);

        // Before 593 both re-made the cookie as the adviser: Billing and My Team opened to the assistant.
        await browser.AccountAsync(c => c.SetPortalAccentColor("#1457d9", null));
        Assert.Equal(memberId.ToString(), browser.SignedIn!.FindFirstValue("TeamMemberId"));
        Assert.Equal(stamp, browser.SignedIn.FindFirstValue(TwoStepGate.ClaimType));

        AgentUser agent;
        await using (var db = testDb.CreateContext()) agent = await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId);
        await browser.AccountAsync(c => c.Profile(new AgentProfileViewModel
        {
            FirstName = agent.FirstName, LastName = agent.LastName, Email = agent.Email, CompanyName = agent.CompanyName,
            City = "Toronto", Province = "ON", PostalCode = "M5V 1A1", Country = "Canada", Phone = agent.Phone, BusinessType = "Accountants"
        }));
        Assert.Equal(memberId.ToString(), browser.SignedIn!.FindFirstValue("TeamMemberId"));
        Assert.Equal("Alex Helper", browser.SignedIn.FindFirstValue("FullName"));
        Assert.Equal(stamp, browser.SignedIn.FindFirstValue(TwoStepGate.ClaimType));
    }

    [Fact]
    public async Task Changing_the_password_keeps_the_session_two_step_stamp_for_advisers_and_team_members()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "keeps", "keeps@example.com");
        var memberId = await Lock583.SeedTeamMemberAsync(testDb, agentId, "keeps-helper@example.com");
        var (adviserSecret, _) = await TwoStep593.TurnOnAppAsync(testDb, "keeps");
        var (memberSecret, _) = await TwoStep593.TurnOnAppAsync(testDb, "keeps-helper@example.com");

        foreach (var (name, secret) in new[] { ("keeps", adviserSecret), ("keeps-helper@example.com", memberSecret) })
        {
            var browser = new TwoStepBrowser(testDb) { Shift = TimeSpan.FromSeconds(31) };
            await browser.AccountAsync(c => c.Login(name, Lock583.Password));
            await browser.TwoStepAsync(c => c.Verify(browser.CodeFor(secret)));
            var stamp = browser.SignedIn!.FindFirstValue(TwoStepGate.ClaimType);
            Assert.NotNull(stamp);
            Assert.IsType<RedirectResult>(await browser.AccountAsync(c => c.ChangePassword(Lock583.Password, "changed-password-1", "changed-password-1")));
            Assert.Equal(stamp, browser.SignedIn!.FindFirstValue(TwoStepGate.ClaimType));
        }
    }

    [Fact]
    public void The_portal_session_cookie_is_made_in_one_place_and_every_password_that_signs_in_passes_the_gate()
    {
        // Only PortalSignIn makes the adviser portal's session cookie (the client portal and SuperAdmin have
        // their own schemes): no path can leave the stamp out by making the cookie itself.
        var web = TwoStep593.FindRepoDir(@"src\IPRO.Web");
        var makers = Directory.GetFiles(web, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"SignInAsync\(\s*CookieAuthenticationDefaults\.AuthenticationScheme"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(new[] { "PortalSignIn.cs" }, makers);

        // AccountController: the sign-in page (adviser and team member) and the forgot-password reset ask the
        // gate; the re-issues keep the session's own stamp; registration (a brand-new account) has none.
        var account = TwoStep593.Read(@"src\IPRO.Web\Controllers\AccountController.cs");
        Assert.Equal(3, Regex.Matches(account, @"await _twoStep\.AfterPasswordAsync\(").Count);
        Assert.Contains("await SignInAgentAsync(user, props, twoStep.Stamp);", account);
        Assert.Contains("await SignInTeamMemberAsync(member, owner, props, twoStep.Stamp);", account);
        var bare = Regex.Matches(account, @"await SignIn(Agent|TeamMember)Async\([^;]*\);").Select(m => m.Value)
            .Where(v => !v.Contains("twoStep.Stamp") && !v.Contains("CurrentTwoStepStamp")).ToList();
        Assert.Equal(new[] { "await SignInAgentAsync(agent, new AuthenticationProperties { IsPersistent = false });" }, bare);   // Register
    }
}

// ---- the dashboard, My Team and SuperAdmin -----------------------------------------------------------------------

public class TwoStepManagement593Tests
{
    [Fact]
    public void The_dashboard_asks_everyone_without_two_step_to_turn_it_on()
    {
        var controller = TwoStep593.Read(@"src\IPRO.Web\Controllers\DashboardController.cs");
        Assert.Contains("ViewBag.TwoStepOff = TwoStepGate.AccountOf(User) is { } me && await TwoStepStore.FindAsync(_db, me) == null;", controller);
        var view = TwoStep593.Read(@"src\IPRO.Web\Views\Dashboard\Index.cshtml");
        Assert.Contains("@if (ViewBag.TwoStepOff == true)", view);
        Assert.Contains("href=\"/portal/TwoStep/Setup\"", view);
        Assert.Contains("href=\"/portal/TwoStep\"", TwoStep593.Read(@"src\IPRO.Web\Views\Shared\_Layout.cshtml"));

        Assert.Equal(TwoStepAccount.Adviser(7), TwoStepGate.AccountOf(Lock583.User(new Claim(ClaimTypes.NameIdentifier, "7"))));
        Assert.Equal(TwoStepAccount.Member(7, 9), TwoStepGate.AccountOf(Lock583.User(new Claim(ClaimTypes.NameIdentifier, "7"), new Claim("TeamMemberId", "9"))));
        Assert.Null(TwoStepGate.AccountOf(Lock583.User()));
    }

    [Fact]
    public async Task The_account_owner_resets_a_team_members_two_step_and_removing_the_member_removes_theirs()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "lead", "lead@example.com");
        var memberId = await Lock583.SeedTeamMemberAsync(testDb, agentId, "staff@example.com");
        var otherAgentId = await Lock583.SeedAgentAsync(testDb, "stranger", "stranger@example.com");
        await TwoStep593.TurnOnAppAsync(testDb, "staff@example.com");
        var member = TwoStepAccount.Member(agentId, memberId);
        var notices = new RecordingTwoStepNotices();

        // Another adviser cannot reach this member.
        await using (var db = testDb.CreateContext())
            Assert.IsType<NotFoundResult>(await TwoStep593.Team(db, otherAgentId, notices).ResetTwoStep(memberId));
        Assert.NotNull(await TwoStep593.RowAsync(testDb, member));

        await using (var db = testDb.CreateContext())
            Assert.IsType<RedirectToActionResult>(await TwoStep593.Team(db, agentId, notices).ResetTwoStep(memberId));
        Assert.Null(await TwoStep593.RowAsync(testDb, member));
        Assert.Contains(notices.Queued, n => n.Notice == TwoStepNotice.ResetByOwner && n.Account == member);

        await TwoStep593.TurnOnAppAsync(testDb, "staff@example.com", TimeSpan.FromMinutes(2));
        await using (var db = testDb.CreateContext())
            Assert.IsType<RedirectToActionResult>(await TwoStep593.Team(db, agentId, notices).Delete(memberId));
        Assert.Null(await TwoStep593.RowAsync(testDb, member));
        await using (var db = testDb.CreateContext())
            Assert.False(await db.TwoStepRecoveryCodes.AnyAsync(r => r.TeamMemberId == memberId));
    }

    [Fact]
    public async Task SuperAdmin_requires_two_step_for_one_adviser_resets_it_with_an_audit_entry_and_an_email_and_unlock_ends_a_code_lock()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "client-of-ipro", "advisor@example.com");
        var adminId = await Lock583.SeedAdminAsync(testDb, "owner-admin");
        var email = new TwoStep593.RecordingEmail();

        await using (var db = testDb.CreateContext())
            Assert.IsType<RedirectToActionResult>(await TwoStep593.Agents(db, adminId, email).RequireTwoStep(agentId, true));
        await using (var db = testDb.CreateContext())
        {
            Assert.True(await TwoStepStore.IsRequiredAsync(db, agentId));
            Assert.Contains(await db.AdminAuditLogEntries.AsNoTracking().ToListAsync(), e => e.Action == "TwoStepRequired" && e.Details.Contains("client-of-ipro"));
        }

        var (secret, _) = await TwoStep593.TurnOnAppAsync(testDb, "client-of-ipro");
        var account = TwoStepAccount.Adviser(agentId);
        await using (var db = testDb.CreateContext())
        {
            for (var i = 0; i < 5; i++) { await TwoStepStore.BeginCodeAsync(db, account, DateTime.UtcNow); await TwoStepStore.CodeFailedAsync(db, account, DateTime.UtcNow); }
            Assert.True(TwoStepStore.IsCodeLocked(await TwoStepStore.FindAsync(db, account), DateTime.UtcNow));
        }
        await using (var db = testDb.CreateContext())
            Assert.IsType<RedirectToActionResult>(await TwoStep593.Agents(db, adminId, email).UnlockSignIn(agentId));
        Assert.False(TwoStepStore.IsCodeLocked(await TwoStep593.RowAsync(testDb, account), DateTime.UtcNow));

        await using (var db = testDb.CreateContext())
            Assert.IsType<RedirectToActionResult>(await TwoStep593.Agents(db, adminId, email).ResetTwoStep(agentId));
        Assert.Null(await TwoStep593.RowAsync(testDb, account));
        await using (var db = testDb.CreateContext())
            Assert.Contains(await db.AdminAuditLogEntries.AsNoTracking().ToListAsync(), e => e.Action == "TwoStepReset" && e.Details.Contains("client-of-ipro"));
        var sent = Assert.Single(email.Sent);
        Assert.Equal("advisor@example.com", sent.To);
        Assert.Equal(TwoStepLetters.ResetSubject, sent.Subject);
        Assert.Contains("iPro support reset two-step sign-in", sent.Html);

        // Held after 20 wrong codes: the reset also gives a temporary password, in the same action.
        await TwoStep593.TurnOnAppAsync(testDb, "client-of-ipro", TimeSpan.FromMinutes(2));
        await using (var db = testDb.CreateContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE `TwoStepSignIns` SET `WrongCodes` = {0} WHERE `AgentUserId` = {1}", TwoStepStore.WrongCodesBeforeHold, agentId);
        string message;
        await using (var db = testDb.CreateContext())
        {
            var admin = TwoStep593.Agents(db, adminId, email);
            Assert.IsType<RedirectToActionResult>(await admin.ResetTwoStep(agentId));
            message = (string)admin.TempData["Success"]!;
        }
        var temporary = Regex.Match(message, "temporary password is now (\\S+) ").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(temporary));
        await using (var db = testDb.CreateContext())
        {
            var row = await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId);
            Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<AgentUser>().VerifyHashedPassword(row, row.PasswordHash, temporary));
            Assert.True(row.MustChangePassword);
            Assert.Null(await TwoStepStore.FindAsync(db, account));
            Assert.Contains(await db.AdminAuditLogEntries.AsNoTracking().ToListAsync(), e => e.Action == "TwoStepReset" && e.Details.Contains("temporary password was issued"));
        }

        // Still required: the next session must set it up again.
        await using (var db = testDb.CreateContext())
        {
            Assert.IsType<RedirectToActionResult>(await TwoStep593.Agents(db, adminId, email).RequireTwoStep(agentId, false));
            Assert.False(await TwoStepStore.IsRequiredAsync(db, agentId));
        }
    }

    [Fact]
    public async Task The_tables_are_made_at_start_up_and_go_with_the_adviser()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using (var db = testDb.CreateContext())
        {
            // Production before this deploy has none of the three.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE `TwoStepRecoveryCodes`");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE `TwoStepSignIns`");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE `TwoStepRequirements`");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE `TwoStepSetupCodes`");
            await StartupSchemaRepair.EnsureTwoStepSignInSchemaAsync(db);
            await StartupSchemaRepair.EnsureTwoStepSignInSchemaAsync(db);   // and again: nothing to do
        }
        var agentId = await Lock583.SeedAgentAsync(testDb, "gone", "gone@example.com");
        await TwoStep593.TurnOnAppAsync(testDb, "gone");
        await using (var db = testDb.CreateContext())
        {
            await TwoStepStore.SetRequiredAsync(db, agentId, true, "owner-admin", DateTime.UtcNow);
            Assert.Contains("TwoStepSignIns", AgentDataEraser.CoveredTables);
            Assert.Contains("TwoStepRecoveryCodes", AgentDataEraser.CoveredTables);
            Assert.Contains("TwoStepRequirements", AgentDataEraser.CoveredTables);
            Assert.Contains("TwoStepSetupCodes", AgentDataEraser.CoveredTables);
        }
        // The portal reads the tables on every signed-in request: if the step fails and they are not there,
        // it does not start; SuperAdmin starts regardless.
        var web = TwoStep593.Read(@"src\IPRO.Web\Program.cs");
        var admin = TwoStep593.Read(@"src\IPRO.Admin\Program.cs");
        const string step = "await StartupGuard.RunStepAsync(\"StartupSchemaRepair.EnsureTwoStepSignInSchemaAsync\", () => StartupSchemaRepair.EnsureTwoStepSignInSchemaAsync(db), db, app.Logger)";
        Assert.Contains("    if (!" + step + "\n        && !await StartupSchemaRepair.TwoStepSignInTablesExistAsync(db))\n        throw new InvalidOperationException(", web);
        Assert.Contains(step + ";", admin);
        await using (var db = testDb.CreateContext())
        {
            Assert.True(await StartupSchemaRepair.TwoStepSignInTablesExistAsync(db));
            await db.Database.ExecuteSqlRawAsync("DROP TABLE `TwoStepSetupCodes`");
            Assert.False(await StartupSchemaRepair.TwoStepSignInTablesExistAsync(db));
        }
    }
}

// ---- what the independent review found (593), each closed ------------------------------------------------------

public class TwoStepReview593Tests
{
    [Fact]
    public async Task A_new_password_ends_every_other_session_and_new_recovery_codes_end_other_sessions_and_remembered_devices()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "keys", "keys@example.com");

        var mine = new TwoStepBrowser(testDb);
        await mine.SignInWithPasswordAsync("keys");
        var theirs = new TwoStepBrowser(testDb);
        await theirs.SignInWithPasswordAsync("keys");
        await using (var db = testDb.CreateContext())
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Ok, await TwoStep593.VerdictAsync(db, theirs.SignedIn!));

        // Change Password: this browser goes on, the other one is signed out.
        Assert.IsType<RedirectResult>(await mine.AccountAsync(c => c.ChangePassword(Lock583.Password, "second-password-1", "second-password-1")));
        await using (var db = testDb.CreateContext())
        {
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Ok, await TwoStep593.VerdictAsync(db, mine.SignedIn!));
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Reject, await TwoStep593.VerdictAsync(db, theirs.SignedIn!));
            // A session from before this release has no password claim at all: it signs in again.
            var legacy = new ClaimsPrincipal(new ClaimsIdentity(mine.SignedIn!.Claims.Where(c => c.Type != PortalSignIn.PasswordClaimType), "test"));
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Reject, await TwoStep593.VerdictAsync(db, legacy));
        }

        // Forgot password: every session made before it ends.
        string token;
        await using (var db = testDb.CreateContext())
            token = (await new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()).InitiatePasswordResetAsync("keys@example.com"))!.PasswordResetToken!;
        var reset = new TwoStepBrowser(testDb);
        Assert.IsType<RedirectResult>(await reset.AccountAsync(c => c.ResetPassword(token, "third-password-1", "third-password-1")));
        await using (var db = testDb.CreateContext())
        {
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Reject, await TwoStep593.VerdictAsync(db, mine.SignedIn!));
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Ok, await TwoStep593.VerdictAsync(db, reset.SignedIn!));
        }

        // New recovery codes: the other signed-in browser and its remembered device end; this one goes on.
        await using (var db = testDb.CreateContext())
        {
            var adviser = await db.AgentUsers.SingleAsync(a => a.Id == agentId);
            adviser.PasswordHash = new PasswordHasher<AgentUser>().HashPassword(adviser, Lock583.Password);
            await db.SaveChangesAsync();
        }
        var (secret, _) = await TwoStep593.TurnOnAppAsync(testDb, "keys");
        var remembered = new TwoStepBrowser(testDb);
        await remembered.AccountAsync(c => c.Login("keys", Lock583.Password));
        Assert.IsType<RedirectResult>(await remembered.TwoStepAsync(c => c.Verify(remembered.CodeFor(secret), rememberDevice: true)));
        var renewing = new TwoStepBrowser(testDb) { Shift = TimeSpan.FromSeconds(31) };
        await renewing.AccountAsync(c => c.Login("keys", Lock583.Password));
        Assert.IsType<RedirectResult>(await renewing.TwoStepAsync(c => c.Verify(renewing.CodeFor(secret))));
        renewing.Shift = TimeSpan.FromSeconds(62);
        Assert.IsType<ViewResult>(await renewing.TwoStepAsync(c => c.NewRecoveryCodes(Lock583.Password, renewing.CodeFor(secret))));
        await using (var db = testDb.CreateContext())
        {
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Ok, await TwoStep593.VerdictAsync(db, renewing.SignedIn!));
            Assert.Equal(AgentCookieRevalidator.TwoStepVerdict.Reject, await TwoStep593.VerdictAsync(db, remembered.SignedIn!));
        }
        remembered.SignOut();
        Assert.Equal(TwoStepGate.VerifyPath, Assert.IsType<RedirectResult>(await remembered.AccountAsync(c => c.Login("keys", Lock583.Password))).Url);
        Assert.Null(remembered.SignedIn);
    }

    [Fact]
    public async Task Twenty_wrong_codes_without_a_right_one_hold_the_code_page_until_SuperAdmin_unlocks_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "patient", "patient@example.com");
        var adminId = await Lock583.SeedAdminAsync(testDb, "owner-admin");
        var (secret, _) = await TwoStep593.TurnOnAppAsync(testDb, "patient");
        var account = TwoStepAccount.Adviser(agentId);

        // Four runs of five, each after the last lock ran out: the fourth holds it.
        var guesser = new TwoStepBrowser(testDb);
        for (var run = 0; run < 4; run++)
        {
            guesser.Shift = TimeSpan.FromMinutes(16 * run);
            await guesser.AccountAsync(c => c.Login("patient", Lock583.Password));
            for (var i = 1; i <= 4; i++) Assert.Equal(TwoStepMessages.WrongCode, await guesser.VerifyErrorAsync(guesser.WrongCodeFor(secret)));
            Assert.Equal(run < 3 ? TwoStepMessages.LockedAdviser : TwoStepMessages.HeldAdviser, await guesser.VerifyErrorAsync(guesser.WrongCodeFor(secret)));
        }
        Assert.Equal(4, guesser.LockNotices.CodeLocks.Count);
        Assert.Contains(guesser.Log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("held until SuperAdmin unlocks it after 20 wrong codes"));

        // Long after the 15 minutes, the right code is still refused.
        var owner = new TwoStepBrowser(testDb) { Shift = TimeSpan.FromHours(3) };
        await owner.AccountAsync(c => c.Login("patient", Lock583.Password));
        Assert.Equal(TwoStepMessages.HeldAdviser, await owner.VerifyErrorAsync(owner.CodeFor(secret)));
        Assert.True(TwoStepStore.IsCodeHeld(await TwoStep593.RowAsync(testDb, account)));

        // SuperAdmin's Unlock ends it, and the right code works.
        await using (var db = testDb.CreateContext())
            Assert.IsType<RedirectToActionResult>(await TwoStep593.Agents(db, adminId, new TwoStep593.RecordingEmail()).UnlockSignIn(agentId));
        Assert.IsType<RedirectResult>(await owner.TwoStepAsync(c => c.Verify(owner.CodeFor(secret))));
        Assert.NotNull(owner.SignedIn);
        Assert.Equal(0, (await TwoStep593.RowAsync(testDb, account))!.WrongCodes);
    }

    [Fact]
    public async Task The_code_lock_email_goes_even_when_a_password_lock_email_went_today_and_says_when_it_is_held()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "warned", "warned@example.com");
        var account = TwoStepAccount.Adviser(agentId);
        var email = new TwoStep593.RecordingEmail();
        var now = DateTime.UtcNow;
        await using var db = testDb.CreateContext();
        await TwoStepStore.EnableAsync(db, account, TwoStepMethods.App, "x", 0, now);
        // A password lock emailed today (583's once a day is used up).
        Assert.True(await SignInLockout.ClaimNoticeAsync(db, account.SignIn, now));
        for (var i = 0; i < 5; i++) { await TwoStepStore.BeginCodeAsync(db, account, now); await TwoStepStore.CodeFailedAsync(db, account, now); }

        Assert.NotNull(await SignInLockNotices.SendCodeLockAsync(db, email, Lock583.Config, new RecordingLogger(), account, now));
        var sent = Assert.Single(email.Sent);
        Assert.Equal("warned@example.com", sent.To);
        Assert.Contains("typed your iPro password correctly", sent.Html);
        // Once a day for this email too.
        Assert.Null(await SignInLockNotices.SendCodeLockAsync(db, email, Lock583.Config, new RecordingLogger(), account, now.AddMinutes(20)));

        var agent = await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId);
        Assert.Contains("until iPro support unlocks it", SignInLockNotices.ForAdviserCodes(agent, "https://app.example.test", held: true).Html);
    }

    [Fact]
    public async Task Changing_the_login_email_takes_the_password_and_a_code_and_never_a_team_member()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "inboxes", "first@example.com");
        await Lock583.SeedTeamMemberAsync(testDb, agentId, "assistant-inbox@example.com");

        // A team member: never, even with their own right password.
        var member = new TwoStepBrowser(testDb);
        await member.SignInWithPasswordAsync("assistant-inbox@example.com");
        var refused = Assert.IsType<ViewResult>(await member.AccountAsync(c => c.Profile(TwoStep593.ProfileForm("mine@attacker.example", Lock583.Password, null))));
        Assert.Contains("Only the account owner can change the login email.", Lock583.Error(refused));
        Assert.Equal("first@example.com", await TwoStep593.AgentEmailAsync(testDb, agentId));

        // The adviser: the current password is needed, and the old address is told.
        var adviser = new TwoStepBrowser(testDb);
        await adviser.SignInWithPasswordAsync("inboxes");
        Assert.Contains("type your current password", Lock583.Error(Assert.IsType<ViewResult>(await adviser.AccountAsync(c => c.Profile(TwoStep593.ProfileForm("second@example.com", null, null))))));
        Assert.Equal("Current password is incorrect.", Lock583.Error(Assert.IsType<ViewResult>(await adviser.AccountAsync(c => c.Profile(TwoStep593.ProfileForm("second@example.com", "not-it", null))))));
        Assert.IsType<RedirectToActionResult>(await adviser.AccountAsync(c => c.Profile(TwoStep593.ProfileForm("second@example.com", Lock583.Password, null))));
        Assert.Equal("second@example.com", await TwoStep593.AgentEmailAsync(testDb, agentId));
        Assert.Contains(adviser.Notices.Queued, n => n.Notice == TwoStepNotice.EmailChanged && n.Detail == "first@example.com");
        var letter = TwoStepLetters.EmailChanged(new TwoStepLetters.Person("second@example.com", "Pat Lee", "Pat", false, "Pat Lee", "Lee Advisory"), "first@example.com");
        Assert.Equal("first@example.com", letter.To);
        Assert.Contains("s***@example.com", letter.Html);

        // With two-step on, a code as well.
        var (secret, _) = await TwoStep593.TurnOnAppAsync(testDb, "inboxes");
        var signedIn = new TwoStepBrowser(testDb);
        await signedIn.AccountAsync(c => c.Login("inboxes", Lock583.Password));
        await signedIn.TwoStepAsync(c => c.Verify(signedIn.CodeFor(secret)));
        signedIn.Shift = TimeSpan.FromSeconds(31);
        Assert.Contains("a two-step sign-in code", Lock583.Error(Assert.IsType<ViewResult>(await signedIn.AccountAsync(c => c.Profile(TwoStep593.ProfileForm("third@example.com", Lock583.Password, null))))));
        Assert.Equal("second@example.com", await TwoStep593.AgentEmailAsync(testDb, agentId));
        Assert.IsType<RedirectToActionResult>(await signedIn.AccountAsync(c => c.Profile(TwoStep593.ProfileForm("third@example.com", Lock583.Password, signedIn.CodeFor(secret)))));
        Assert.Equal("third@example.com", await TwoStep593.AgentEmailAsync(testDb, agentId));
    }

    [Fact]
    public async Task Turning_it_on_never_replaces_one_that_is_on_and_a_set_up_code_works_once_with_five_tries()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "once", "once@example.com");
        var account = TwoStepAccount.Adviser(agentId);
        var now = DateTime.UtcNow;
        await using var db = testDb.CreateContext();

        // Five wrong tries spend a set-up code; a new one (a minute later) works once.
        var first = await TwoStepStore.IssueSetupCodeAsync(db, account, now);
        Assert.NotNull(first);
        Assert.Null(await TwoStepStore.IssueSetupCodeAsync(db, account, now.AddSeconds(30)));
        var wrong = first == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++) Assert.Equal(TwoStepStore.SetupCodeResult.Wrong, await TwoStepStore.UseSetupCodeAsync(db, account, wrong, now));
        Assert.Equal(TwoStepStore.SetupCodeResult.NoCode, await TwoStepStore.UseSetupCodeAsync(db, account, first, now));
        var second = await TwoStepStore.IssueSetupCodeAsync(db, account, now.AddMinutes(2));
        Assert.Equal(TwoStepStore.SetupCodeResult.Right, await TwoStepStore.UseSetupCodeAsync(db, account, second, now.AddMinutes(3)));
        Assert.Equal(TwoStepStore.SetupCodeResult.NoCode, await TwoStepStore.UseSetupCodeAsync(db, account, second, now.AddMinutes(3)));
        // And it runs out after ten minutes.
        var third = await TwoStepStore.IssueSetupCodeAsync(db, account, now.AddMinutes(4));
        Assert.Equal(TwoStepStore.SetupCodeResult.NoCode, await TwoStepStore.UseSetupCodeAsync(db, account, third, now.AddMinutes(15)));
        // No more than ten in 24 hours.
        var issued = 3;
        for (var minute = 20; issued < 12; minute += 2) if (await TwoStepStore.IssueSetupCodeAsync(db, account, now.AddMinutes(minute)) != null) issued++; else break;
        Assert.Equal(TwoStepStore.SetupCodesPerWindow, issued);

        // On once: a second turn-on is refused and changes nothing.
        var on = await TwoStepStore.EnableAsync(db, account, TwoStepMethods.Email, null, 0, now);
        Assert.NotNull(on);
        Assert.Null(await TwoStepStore.EnableAsync(db, account, TwoStepMethods.App, "someone-else", 0, now));
        var row = await TwoStepStore.FindAsync(db, account);
        Assert.Equal(on!.Value.Stamp, row!.Stamp);
        Assert.Equal(TwoStepMethods.Email, row.Method);
        Assert.Equal(10, await TwoStepStore.RecoveryCodesLeftAsync(db, account));
    }

    // The re-check (A): a save of the adviser's row, from a request that read it before a password reset,
    // used to write every column back -- the old password hash included.
    [Fact]
    public async Task A_save_from_a_request_that_read_the_row_before_a_new_password_does_not_put_the_old_one_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "stale", "stale@example.com");

        await using var thief = testDb.CreateContext();
        var thiefService = new AgentService(new UnitOfWork(thief), new PasswordHasher<AgentUser>());
        var seenBefore = (await thiefService.GetByIdAsync(agentId))!;   // read before the owner's new password

        await using (var owner = testDb.CreateContext())
            await new AgentService(new UnitOfWork(owner), new PasswordHasher<AgentUser>()).ChangePasswordAsync(agentId, "owners-new-password");

        seenBefore.PortalAccentColor = "#1f7a4d";
        await thiefService.UpdateAsync(seenBefore);
        await thiefService.UpdateLastLoginAsync(agentId);

        await using var check = testDb.CreateContext();
        var row = await check.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId);
        Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<AgentUser>().VerifyHashedPassword(row, row.PasswordHash, "owners-new-password"));
        Assert.Equal("#1f7a4d", row.PortalAccentColor);
        // The whole-row write is gone from every account save (LaunchLoadFixTests' rule for the send rows).
        Assert.DoesNotContain("_uow.AgentUsers.Update(user);\n        await _uow.SaveChangesAsync();\n        // 583", TwoStep593.Read(@"src\IPRO.Business\Services\AgentService.cs"));
        Assert.DoesNotContain("TeamMembers.Update(", TwoStep593.Read(@"src\IPRO.Web\Controllers\TeamController.cs"));
        Assert.DoesNotContain("TeamMembers.Update(", TwoStep593.Read(@"src\IPRO.Web\Controllers\AccountController.cs"));
    }

    [Fact]
    public async Task The_set_up_email_allowance_survives_turning_it_on_and_tries_sent_together_get_five()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "cycler", "cycler@example.com");
        var account = TwoStepAccount.Adviser(agentId);
        var now = DateTime.UtcNow;

        // Tries sent together: five are compared, the rest are refused unseen (re-check E).
        await using (var db = testDb.CreateContext())
            Assert.NotNull(await TwoStepStore.IssueSetupCodeAsync(db, account, now));
        var answers = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            await using var db = testDb.CreateContext();
            return await TwoStepStore.UseSetupCodeAsync(db, account, (100000 + i).ToString(), now);
        }));
        Assert.True(answers.Count(a => a != TwoStepStore.SetupCodeResult.NoCode) <= 5);

        // Nine more emails, then on and off again: the day's allowance is not new (re-check B).
        await using (var db = testDb.CreateContext())
        {
            for (var minute = 2; minute <= 18; minute += 2) Assert.NotNull(await TwoStepStore.IssueSetupCodeAsync(db, account, now.AddMinutes(minute)));
            Assert.NotNull(await TwoStepStore.EnableAsync(db, account, TwoStepMethods.Email, null, 0, now.AddMinutes(19)));
            Assert.True(await TwoStepStore.DisableAsync(db, account));
            Assert.Null(await TwoStepStore.IssueSetupCodeAsync(db, account, now.AddMinutes(30)));
            Assert.NotNull(await TwoStepStore.IssueSetupCodeAsync(db, account, now.AddHours(25)));
        }
    }

    [Fact]
    public async Task Resetting_a_held_team_member_gives_them_a_new_password_too_and_the_held_email_goes_the_same_day()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "lead2", "lead2@example.com");
        var memberId = await Lock583.SeedTeamMemberAsync(testDb, agentId, "held-member@example.com");
        await TwoStep593.TurnOnAppAsync(testDb, "held-member@example.com");
        var member = TwoStepAccount.Member(agentId, memberId);
        var now = DateTime.UtcNow;
        var email = new TwoStep593.RecordingEmail();
        await using (var db = testDb.CreateContext())
        {
            // A 15-minute lock emailed today, then the hold: the held email still goes (re-check C).
            for (var i = 0; i < 5; i++) { await TwoStepStore.BeginCodeAsync(db, member, now); await TwoStepStore.CodeFailedAsync(db, member, now); }
            Assert.NotNull(await SignInLockNotices.SendCodeLockAsync(db, email, Lock583.Config, new RecordingLogger(), member, now));
            await db.Database.ExecuteSqlRawAsync("UPDATE `TwoStepSignIns` SET `WrongCodes` = {0} WHERE `TeamMemberId` = {1}", TwoStepStore.WrongCodesBeforeHold, memberId);
            Assert.NotNull(await SignInLockNotices.SendCodeLockAsync(db, email, Lock583.Config, new RecordingLogger(), member, now.AddMinutes(45)));
            Assert.Contains("20 wrong two-step sign-in codes", email.Sent.Last().Html);
        }

        // My Team shows it, and the reset hands out a temporary password with it (re-check D).
        string oldHash;
        await using (var db = testDb.CreateContext())
        {
            oldHash = (await db.TeamMembers.AsNoTracking().SingleAsync(t => t.Id == memberId)).PasswordHash;
            var index = Assert.IsType<ViewResult>(await TwoStep593.Team(db, agentId, new RecordingTwoStepNotices()).Index());
            Assert.Contains(memberId, (HashSet<int>)index.ViewData["TwoStepHeld"]!);
        }
        await using (var db = testDb.CreateContext())
        {
            var team = TwoStep593.Team(db, agentId, new RecordingTwoStepNotices());
            Assert.IsType<RedirectToActionResult>(await team.ResetTwoStep(memberId));
            Assert.False(string.IsNullOrEmpty(team.TempData["TeamTempPassword"] as string));
        }
        await using (var db = testDb.CreateContext())
        {
            var row = await db.TeamMembers.AsNoTracking().SingleAsync(t => t.Id == memberId);
            Assert.NotEqual(oldHash, row.PasswordHash);
            Assert.True(row.MustChangePassword);
            Assert.Null(await TwoStepStore.FindAsync(db, member));
        }
    }

    [Fact]
    public async Task A_recovery_code_never_remembers_the_device()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await Lock583.SeedAgentAsync(testDb, "nophone", "nophone@example.com");
        var (_, codes) = await TwoStep593.TurnOnAppAsync(testDb, "nophone");
        var browser = new TwoStepBrowser(testDb);
        await browser.AccountAsync(c => c.Login("nophone", Lock583.Password));
        Assert.IsType<RedirectResult>(await browser.TwoStepAsync(c => c.Verify(codes[0], rememberDevice: true)));
        Assert.NotNull(browser.SignedIn);
        Assert.DoesNotContain(browser.Cookies.Keys, k => k.StartsWith(TwoStepGate.DeviceCookiePrefix));
    }
}

// ---- the owner's decisions of 2026-10-21 ----------------------------------------------------------------------

public class TwoStepDecisions593Tests
{
    // "Remember me" never worked: the box sent "on", which does not bind to a bool. It sends "true" now,
    // and a ticked box gives a 7-day session -- with two-step on, only after the code.
    [Fact]
    public async Task Remember_me_on_the_sign_in_page_binds_and_gives_a_seven_day_session_after_the_code()
    {
        var page = await TwoStep593.RenderWebAsync("~/Views/Account/Login.cshtml", (object?)null);
        Assert.Contains("name=\"rememberMe\" id=\"rememberMe\" value=\"true\"", page);
        Assert.Equal(true, System.ComponentModel.TypeDescriptor.GetConverter(typeof(bool)).ConvertFromInvariantString("true"));

        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await Lock583.SeedAgentAsync(testDb, "weekly", "weekly@example.com");
        var plain = new TwoStepBrowser(testDb);
        Assert.IsType<RedirectResult>(await plain.AccountAsync(c => c.Login("weekly", Lock583.Password, rememberMe: true)));
        Assert.True(plain.SignedInProperties!.IsPersistent);
        Assert.InRange(plain.SignedInProperties.ExpiresUtc!.Value, DateTimeOffset.UtcNow.AddDays(6.9), DateTimeOffset.UtcNow.AddDays(7.1));
        // Seven days from signing in: the cookie is not renewed as it is used (the re-check of 2026-10-21).
        Assert.False(plain.SignedInProperties.AllowRefresh);
        Assert.Null(PortalSignIn.Session(false).AllowRefresh);

        var (secret, _) = await TwoStep593.TurnOnAppAsync(testDb, "weekly");
        var withCode = new TwoStepBrowser(testDb);
        Assert.Equal(TwoStepGate.VerifyPath, Assert.IsType<RedirectResult>(await withCode.AccountAsync(c => c.Login("weekly", Lock583.Password, rememberMe: true))).Url);
        Assert.Null(withCode.SignedIn);   // no long cookie before the code
        await withCode.TwoStepAsync(c => c.Verify(withCode.CodeFor(secret)));
        Assert.True(withCode.SignedInProperties!.IsPersistent);
        Assert.False(withCode.SignedInProperties.AllowRefresh);
        Assert.InRange(withCode.SignedInProperties.ExpiresUtc!.Value, DateTimeOffset.UtcNow.AddDays(6.9), DateTimeOffset.UtcNow.AddDays(7.1));
    }

    // Without the password at set-up, the emailed code is what stops someone who holds a session left open:
    // they cannot turn two-step on (and shut the owner out) without the account's inbox.
    [Fact]
    public async Task A_session_holder_without_the_inbox_cannot_turn_it_on()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "openSession", "open-session@example.com");
        var browser = new TwoStepBrowser(testDb);
        await browser.SignInWithPasswordAsync("openSession");

        var setup = Assert.IsType<TwoStepSetupViewModel>(Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.StartEmail("app"))).Model);
        var secret = Totp.FromBase32(setup.ManualKey)!;
        // Guessing the emailed code: five tries and it is spent; the right one then does not work either.
        var emailed = Assert.Single(browser.Notices.Codes, c => c.ForSetup).Code;
        for (var n = 0; n < 5; n++)
        {
            var guess = (200000 + n).ToString();
            if (guess == emailed) continue;
            var refused = Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.Confirm(setup.SetupToken, browser.CodeFor(secret), guess)));
            Assert.Equal(TwoStepMessages.WrongSetupCode, Assert.IsType<TwoStepSetupViewModel>(refused.Model).Error);
        }
        var spent = Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.Confirm(setup.SetupToken, browser.CodeFor(secret), emailed)));
        Assert.Equal(TwoStepMessages.SetupCodeSpent, Assert.IsType<TwoStepSetupViewModel>(spent.Model).Error);
        Assert.Null(await TwoStep593.RowAsync(testDb, TwoStepAccount.Adviser(agentId)));

        // Another person's set-up page is no use: the token belongs to the account (and its password).
        var other = new TwoStepBrowser(testDb);
        await Lock583.SeedAgentAsync(testDb, "otherAccount", "other-account@example.com");
        await other.SignInWithPasswordAsync("otherAccount");
        Assert.IsType<RedirectToActionResult>(await other.TwoStepAsync(c => c.Confirm(setup.SetupToken, other.CodeFor(secret), emailed)));
        Assert.Null(await TwoStep593.RowAsync(testDb, TwoStepAccount.Adviser(agentId)));
    }

    // Turning it on takes a browser where they were signed in and the inbox, not the password: the email says
    // so, and a team member's adviser is told as well (the re-check of 2026-10-21).
    [Fact]
    public void The_turned_on_email_gives_the_right_advice_and_the_adviser_hears_about_a_team_member()
    {
        var adviser = new TwoStepLetters.Person("pat@lee.example.com", "Pat Lee", "Pat", false, "Pat Lee", "Lee Advisory");
        var member = new TwoStepLetters.Person("alex@lee.example.com", "Alex Helper", "Alex", true, "Pat Lee", "Lee Advisory");
        Assert.Contains("someone used a browser where you were signed in, and your email: call iPro support", TwoStepLetters.TurnedOn(adviser, TwoStepMethods.App).Html);
        Assert.Contains("ask Pat Lee to reset your two-step sign-in and your password", TwoStepLetters.TurnedOn(member, TwoStepMethods.App).Html);
        var told = TwoStepLetters.MemberTurnedOn(adviser, "Alex Helper", "alex@lee.example.com");
        Assert.Equal("pat@lee.example.com", told.To);
        Assert.Contains("Alex Helper (alex@lee.example.com) turned on two-step sign-in", told.Html);
        Assert.Contains("TwoStepLetters.MemberTurnedOn(adviser, person.FullName, person.Email)", TwoStep593.Read(@"src\IPRO.Web\Infrastructure\TwoStepNotices.cs"));
        // The local mail folder never defaults into the repository (its files hold codes and reset links).
        Assert.Contains("Environment.SpecialFolder.LocalApplicationData), \"ipro-local-mail\")", TwoStep593.Read(@"src\IPRO.Web\Program.cs"));
    }

    // On this PC there is no mail service: Email__Provider=Folder writes each email as a file instead,
    // and only in Development (a production setting of it is ignored).
    [Fact]
    public async Task The_local_mail_folder_keeps_each_email_as_a_file_and_only_in_development()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ipro593-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var mail = new IPRO.Email.FolderEmailService(folder);
            var sent = await mail.SendDetailedAsync("pat@lee.example.com", "Pat Lee", "Your code to turn on two-step sign-in", "<p>123456</p>");
            Assert.True(sent.Success);
            var file = Assert.Single(Directory.GetFiles(folder, "*.html"));
            Assert.Contains("pat@lee.example.com", Path.GetFileName(file));
            var text = File.ReadAllText(file);
            Assert.Contains("Your code to turn on two-step sign-in", text);
            Assert.Contains("<p>123456</p>", text);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        var program = TwoStep593.Read(@"src\IPRO.Web\Program.cs");
        Assert.Contains("if (builder.Environment.IsDevelopment() && string.Equals(builder.Configuration[\"Email:Provider\"], \"Folder\", StringComparison.OrdinalIgnoreCase))", program);
    }
}

// ---- the pages, rendered from the real views --------------------------------------------------------------------

public class TwoStepScreens593Tests
{
    // Razor errors a build cannot see show up only when a view runs; these run each page with real models.
    // TWOSTEP_SCREENS_OUT names a folder to write the markup to, for the eye (never set in the gate).
    [Fact]
    public async Task Every_two_step_page_renders_with_its_words_and_forms()
    {
        var secret = Totp.NewSecret();
        var pages = new Dictionary<string, string>
        {
            ["verify-app"] = await TwoStep593.RenderWebAsync("~/Views/TwoStep/Verify.cshtml",
                new TwoStepVerifyViewModel { Method = TwoStepMethods.App, MaskedEmail = "p***@lee.example.com", Error = TwoStepMessages.WrongCode }),
            ["verify-email"] = await TwoStep593.RenderWebAsync("~/Views/TwoStep/Verify.cshtml",
                new TwoStepVerifyViewModel { Method = TwoStepMethods.Email, MaskedEmail = "p***@lee.example.com", IsTeamMember = true, Notice = TwoStepMessages.CodeSent }),
            ["setup-choose"] = await TwoStep593.RenderWebAsync("~/Views/TwoStep/Setup.cshtml",
                new TwoStepSetupViewModel { MaskedEmail = "p***@lee.example.com", Required = true }),
            ["setup-app-intro"] = await TwoStep593.RenderWebAsync("~/Views/TwoStep/Setup.cshtml",
                new TwoStepSetupViewModel { Method = TwoStepMethods.App, MaskedEmail = "p***@lee.example.com" }),
            ["setup-app"] = await TwoStep593.RenderWebAsync("~/Views/TwoStep/Setup.cshtml",
                new TwoStepSetupViewModel { Method = TwoStepMethods.App, SetupToken = "token-1", ManualKey = Totp.GroupedKey(secret), EmailSent = true,
                    QrSvg = QrSvg.Render(Totp.OtpAuthUri("iPro", "patlee", secret), "QR code for your authenticator app") }),
            ["setup-email"] = await TwoStep593.RenderWebAsync("~/Views/TwoStep/Setup.cshtml",
                new TwoStepSetupViewModel { Method = TwoStepMethods.Email, SetupToken = "token-2", EmailSent = true, MaskedEmail = "p***@lee.example.com", Error = TwoStepMessages.WrongPassword }),
            ["status-off"] = await TwoStep593.RenderWebAsync("~/Views/TwoStep/Index.cshtml", new TwoStepStatusViewModel()),
            ["status-on"] = await TwoStep593.RenderWebAsync("~/Views/TwoStep/Index.cshtml",
                new TwoStepStatusViewModel { IsOn = true, Method = TwoStepMethods.Email, EnabledAt = new DateTime(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc), RecoveryCodesLeft = 2, MaskedEmail = "p***@lee.example.com", TimeZone = "America/Toronto" }),
            ["recovery-codes"] = await TwoStep593.RenderWebAsync("~/Views/TwoStep/RecoveryCodes.cshtml",
                new TwoStepRecoveryCodesViewModel { Codes = TwoStepStore.NewRecoveryCodes(), JustTurnedOn = true }),
        };

        Assert.Contains("Open your authenticator app", pages["verify-app"]);
        Assert.Contains(TwoStepMessages.WrongCode, pages["verify-app"]);
        // Ticked by default, with the reminder beside it (the owner, 2026-10-21).
        Assert.Contains("name=\"rememberDevice\" id=\"rememberDevice\" value=\"true\" checked", pages["verify-app"]);
        Assert.Contains("Only on your own computer or phone", pages["verify-app"]);
        Assert.Contains("autocomplete=\"one-time-code\"", pages["verify-app"]);
        Assert.Contains("Call iPro support at " + TwoStepLetters.SupportPhone, pages["verify-app"]);
        Assert.DoesNotContain("Email me a new code", pages["verify-app"]);
        Assert.Contains("We emailed a 6-digit code to <strong>p***@lee.example.com</strong>", pages["verify-email"]);
        Assert.Contains("Email me a new code", pages["verify-email"]);
        Assert.Contains("Ask the account owner to reset your two-step sign-in on My Team", pages["verify-email"]);
        Assert.Contains("iPro requires two-step sign-in for this account", pages["setup-choose"]);
        Assert.Contains("href=\"/portal/TwoStep/Setup?method=app\"", pages["setup-choose"]);
        Assert.Contains("href=\"/portal/TwoStep/Setup?method=email\"", pages["setup-choose"]);
        Assert.Contains("<svg", pages["setup-app"]);
        Assert.Contains(Totp.GroupedKey(secret), pages["setup-app"]);
        Assert.Contains("name=\"setupToken\" value=\"token-1\"", pages["setup-app"]);
        Assert.DoesNotContain("name=\"currentPassword\"", pages["setup-app"]);
        Assert.DoesNotContain("name=\"currentPassword\"", pages["setup-email"]);
        Assert.Contains("name=\"emailCode\"", pages["setup-app"]);
        Assert.Contains("name=\"code\"", pages["setup-app"]);
        Assert.DoesNotContain("name=\"code\"", pages["setup-email"]);
        Assert.Contains("name=\"emailCode\"", pages["setup-email"]);
        Assert.Contains("we email a 6-digit code to <strong>p***@lee.example.com</strong>", pages["setup-app-intro"]);
        Assert.Contains("name=\"method\" value=\"app\"", pages["setup-app-intro"]);
        Assert.DoesNotContain("<svg", pages["setup-app-intro"]);
        Assert.Contains(TwoStepMessages.WrongPassword, pages["setup-email"]);
        Assert.Contains("name=\"setupToken\" value=\"token-2\"", pages["setup-email"]);
        Assert.Contains("Set up two-step sign-in", pages["status-off"]);
        Assert.Contains("Recovery codes left: <strong>2</strong> of 10", pages["status-on"]);
        Assert.Contains("On since October 20, 2026.", pages["status-on"]);
        Assert.Contains("Email me a code", pages["status-on"]);
        Assert.Contains("Turn off", pages["status-on"]);
        Assert.Contains("This is the only time iPro shows them", pages["recovery-codes"]);
        Assert.Equal(10, Regex.Matches(pages["recovery-codes"], "<li class=\"col\">[2-9a-z]{5}-[2-9a-z]{5}</li>").Count);
        foreach (var (name, html) in pages)
            if (name != "status-off" && name != "setup-choose" && name != "recovery-codes")
                Assert.Contains("name=\"__RequestVerificationToken\"", html);

        var folder = Environment.GetEnvironmentVariable("TWOSTEP_SCREENS_OUT");
        if (!string.IsNullOrEmpty(folder))
            foreach (var (name, html) in pages)
                File.WriteAllText(Path.Combine(folder, name + ".html"), name.StartsWith("verify") ? html : BuilderImportScreens571Tests.Shell(html));
    }

    [Fact]
    public async Task The_agent_record_shows_two_step_with_require_reset_and_unlock()
    {
        var agent = new AgentUser { Id = 42, UserName = "patlee", FirstName = "Pat", LastName = "Lee", Email = "pat@lee.example.com", IsActive = true };
        var html = await TwoStep593.RenderAdminAsync("~/Views/Agents/Details.cshtml", agent, viewBag =>
        {
            viewBag["TwoStep"] = new TwoStepSignIn { AgentUserId = 42, Method = TwoStepMethods.App, Stamp = "s", EnabledAt = new DateTime(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc), CodeLockedUntil = DateTime.UtcNow.AddMinutes(10) };
            viewBag["TwoStepRequired"] = true;
            viewBag["TwoStepTeam"] = new List<AgentsController.TwoStepTeamRow>
            {
                new(7, "Alex Helper", "helper@example.com", TwoStepMethods.Email, null, 0),
                new(8, "Sam Held", "held@example.com", TwoStepMethods.App, null, TwoStepStore.WrongCodesBeforeHold)
            };
        });
        Assert.Contains("Two-step sign-in", html);
        Assert.Contains("On (authenticator app) since Oct 20, 2026", html);
        Assert.Contains("(5 wrong codes in a row)", html);
        Assert.Contains("action=\"/Agents/ResetTwoStep/42\"", html);
        Assert.Contains("action=\"/Agents/RequireTwoStep/42\"", html);
        Assert.Contains("name=\"required\" value=\"false\"", html);
        Assert.Contains("Make optional", html);
        Assert.Contains("Alex Helper (helper@example.com), email codes", html);
        Assert.Contains("name=\"accountId\" value=\"7\"", html);
        Assert.Contains("Sam Held (held@example.com), authenticator app", html);
        Assert.Contains(", locked until you unlock it (20 wrong codes)", html);

        // Held, with the 15 minutes long over: no lock time to show, and the page still renders.
        var held = await TwoStep593.RenderAdminAsync("~/Views/Agents/Details.cshtml", agent, viewBag =>
            viewBag["TwoStep"] = new TwoStepSignIn { AgentUserId = 42, Method = TwoStepMethods.App, Stamp = "s", EnabledAt = DateTime.UtcNow, WrongCodes = TwoStepStore.WrongCodesBeforeHold });
        Assert.Contains("Locked until you unlock it (20 wrong codes without a right one", held);

        var folder = Environment.GetEnvironmentVariable("TWOSTEP_SCREENS_OUT");
        if (!string.IsNullOrEmpty(folder)) File.WriteAllText(Path.Combine(folder, "admin-agent.html"), BuilderImportScreens571Tests.Shell(html));
    }
}

// ---- the harness -----------------------------------------------------------------------------------------------

// A browser for the adviser portal: its cookies go from one request to the next, and the session cookie a
// request made (or ended) is what the next request is signed in as. One data-protection key ring per
// browser's server, so the protected cookies read back.
internal sealed class TwoStepBrowser
{
    private readonly TestDatabase _db;
    private readonly IDataProtectionProvider _protection;
    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);
    public ClaimsPrincipal? SignedIn { get; private set; }
    public AuthenticationProperties? SignedInProperties { get; private set; }
    public RecordingTwoStepNotices Notices { get; }
    public RecordingLockNotices LockNotices { get; } = new();
    public RecordingLogger Log { get; } = new();
    // How far this browser's server clock is ahead of now.
    public TimeSpan Shift { get; set; }

    public TwoStepBrowser(TestDatabase db, IDataProtectionProvider? protection = null, RecordingTwoStepNotices? notices = null)
    {
        _db = db;
        _protection = protection ?? TwoStep593.Protection;
        Notices = notices ?? new RecordingTwoStepNotices();
    }

    public DateTime Now => DateTime.UtcNow + Shift;

    public string CodeFor(byte[] secret) => Totp.CodeAt(secret, Totp.StepAt(Now));

    public string WrongCodeFor(byte[] secret)
    {
        var step = Totp.StepAt(Now);
        var right = new[] { step - 1, step, step + 1 }.Select(s => Totp.CodeAt(secret, s)).ToHashSet();
        for (var n = 100000; ; n++) if (!right.Contains(n.ToString())) return n.ToString();
    }

    public void SignOut() => SignedIn = null;

    public TwoStepGate Gate(IPRODbContext db) => new(db, _protection, Notices, LockNotices, Log.For<TwoStepGate>(), () => Now);

    public Task<IActionResult> AccountAsync(Func<AccountController, Task<IActionResult>> act) =>
        RunAsync(db => new AccountController(new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()), null!, new UnitOfWork(db), null!, db,
            null!, null!, Log.For<AccountController>(), Lock583.Config, null!, new PasswordHasher<TeamMember>(), LockNotices, Gate(db)), act);

    public Task<IActionResult> TwoStepAsync(Func<TwoStepController, Task<IActionResult>> act) =>
        RunAsync(db => new TwoStepController(db, new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()), Gate(db), Notices, LockNotices,
            new PasswordHasher<TeamMember>(), Log.For<TwoStepController>()), act);

    // Signs in with the password alone (two-step off for the account): the session the set-up pages need.
    public async Task SignInWithPasswordAsync(string name)
    {
        Assert.IsType<RedirectResult>(await AccountAsync(c => c.Login(name, Lock583.Password)));
        Assert.NotNull(SignedIn);
    }

    public async Task<string?> VerifyErrorAsync(string code) =>
        Assert.IsType<TwoStepVerifyViewModel>(Assert.IsType<ViewResult>(await TwoStepAsync(c => c.Verify(code))).Model).Error;

    private async Task<IActionResult> RunAsync<T>(Func<IPRODbContext, T> make, Func<T, Task<IActionResult>> act) where T : Controller
    {
        await using var db = _db.CreateContext();
        var controller = make(db);
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(new Recorder(this));
        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = SignedIn ?? new ClaimsPrincipal(new ClaimsIdentity())
        };
        http.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.9");
        if (Cookies.Count > 0) http.Request.Headers.Cookie = string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, new NoTempData());
        controller.Url = new LocalUrls();
        var result = await act(controller);
        foreach (var header in SetCookieHeaderValue.ParseList(http.Response.Headers.SetCookie.Select(v => v ?? string.Empty).ToList()))
        {
            var name = header.Name.ToString();
            if (header.Expires is { } expires && expires < DateTimeOffset.UtcNow) Cookies.Remove(name);
            else Cookies[name] = header.Value.ToString();
        }
        return result;
    }

    private sealed class Recorder : IAuthenticationService
    {
        private readonly TwoStepBrowser _browser;
        public Recorder(TwoStepBrowser browser) => _browser = browser;
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            _browser.SignedIn = principal;
            _browser.SignedInProperties = properties;
            return Task.CompletedTask;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            _browser.SignedIn = null;
            return Task.CompletedTask;
        }
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    public sealed class LocalUrls : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(Microsoft.AspNetCore.Mvc.Routing.UrlActionContext actionContext) => null;
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => url != null && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\");
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(Microsoft.AspNetCore.Mvc.Routing.UrlRouteContext routeContext) => null;
    }
}

public sealed class RecordingTwoStepNotices : ITwoStepNotices
{
    public ConcurrentQueue<(TwoStepAccount Account, string Code, bool ForSetup)> Codes { get; } = new();
    public ConcurrentQueue<(TwoStepAccount Account, TwoStepNotice Notice, string? Detail)> Queued { get; } = new();

    public Task<bool> SendCodeAsync(TwoStepAccount account, string code, bool forSetup)
    {
        Codes.Enqueue((account, code, forSetup));
        return Task.FromResult(true);
    }

    public void Queue(TwoStepAccount account, TwoStepNotice notice, string? detail = null) => Queued.Enqueue((account, notice, detail));
}

internal static class TwoStep593
{
    public static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    public static async Task<TwoStepSignIn?> RowAsync(TestDatabase testDb, TwoStepAccount account)
    {
        await using var db = testDb.CreateContext();
        return await TwoStepStore.FindAsync(db, account);
    }

    // Signs in with the password, sets up an authenticator app through the real pages, and signs out.
    // `at`: how far ahead of now (a second set-up for the same person waits out the one-a-minute email rule).
    public static async Task<(byte[] Secret, IReadOnlyList<string> RecoveryCodes)> TurnOnAppAsync(TestDatabase testDb, string name, TimeSpan at = default)
    {
        var browser = new TwoStepBrowser(testDb);
        await browser.SignInWithPasswordAsync(name);
        // The code that confirms the set-up is used up; sign-ins in these tests use the next one.
        browser.Shift = at - TimeSpan.FromSeconds(31);
        var setup = Assert.IsType<TwoStepSetupViewModel>(Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.StartEmail("app"))).Model);
        var secret = Totp.FromBase32(setup.ManualKey)!;
        var emailed = browser.Notices.Codes.Last(c => c.ForSetup).Code;
        var done = Assert.IsType<ViewResult>(await browser.TwoStepAsync(c => c.Confirm(setup.SetupToken, browser.CodeFor(secret), emailed)));
        Assert.Equal("RecoveryCodes", done.ViewName);
        return (secret, Assert.IsType<TwoStepRecoveryCodesViewModel>(done.Model).Codes);
    }

    // The profile form as the page posts it, with a login email and (only for a new one) the proof.
    public static AgentProfileViewModel ProfileForm(string email, string? currentPassword, string? code) => new()
    {
        FirstName = "Pat", LastName = "Lee", Email = email, CompanyName = "Lee Advisory", City = "Toronto", Province = "ON",
        PostalCode = "M5V 1A1", Country = "Canada", Phone = "416-555-0100", BusinessType = "Accountants",
        CurrentPassword = currentPassword, TwoStepCode = code
    };

    public static async Task<string> AgentEmailAsync(TestDatabase testDb, int agentId)
    {
        await using var db = testDb.CreateContext();
        return (await db.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId)).Email;
    }

    public static async Task TurnOnEmailAsync(TestDatabase testDb, int agentId)
    {
        await using var db = testDb.CreateContext();
        await TwoStepStore.EnableAsync(db, TwoStepAccount.Adviser(agentId), TwoStepMethods.Email, null, 0, DateTime.UtcNow);
    }

    public static Task<AgentCookieRevalidator.TwoStepVerdict> VerdictAsync(IPRODbContext db, ClaimsPrincipal session) =>
        AgentCookieRevalidator.EvaluateSignInAsync(db, session.FindFirstValue(ClaimTypes.NameIdentifier), session.FindFirstValue("TeamMemberId"),
            session.FindFirstValue(TwoStepGate.ClaimType), session.FindFirstValue(PortalSignIn.PasswordClaimType));

    // The same session with another stamp (or with one, where it had none).
    public static ClaimsPrincipal With(ClaimsPrincipal session, string stamp) =>
        new(new ClaimsIdentity(session.Claims.Where(c => c.Type != TwoStepGate.ClaimType).Append(new Claim(TwoStepGate.ClaimType, stamp)), "test"));

    public static TeamController Team(IPRODbContext db, int agentId, RecordingTwoStepNotices notices)
    {
        var controller = new TeamController(new UnitOfWork(db), new PasswordHasher<TeamMember>(), new RecordingLogger().For<TeamController>());
        var services = new ServiceCollection();
        services.AddSingleton<ITwoStepNotices>(notices);
        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = Lock583.User(new Claim(ClaimTypes.NameIdentifier, agentId.ToString()))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, new NullTempData());
        controller.Url = new TwoStepBrowser.LocalUrls();
        return controller;
    }

    public static AgentsController Agents(IPRODbContext db, int adminId, RecordingEmail email)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmailService>(email);
        services.AddSingleton<IConfiguration>(Lock583.Config);
        var controller = new AgentsController(new AgentService(new UnitOfWork(db), new PasswordHasher<AgentUser>()), null!, new UnitOfWork(db), null!,
            new PasswordHasher<AgentUser>(), new RecordingLogger().For<AgentsController>(), new AdminAuditLogService(new UnitOfWork(db)), db, services.BuildServiceProvider());
        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = Lock583.User(new Claim(ClaimTypes.NameIdentifier, adminId.ToString()), new Claim(ClaimTypes.Name, "owner-admin"), new Claim("Role", AdminRoles.SuperAdmin))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, new NullTempData());
        controller.Url = new TwoStepBrowser.LocalUrls();
        return controller;
    }

    public sealed class RecordingEmail : IEmailService
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { lock (Sent) Sent.Add((toEmail, subject, htmlBody)); return Task.FromResult(EmailSendResult.Sent("msg-593")); }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private sealed class NullTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    // The portal's or SuperAdmin's compiled view, run with a real model (BuilderImportScreens571Tests.RenderAsync
    // for the portal app); the layout is not part of it.
    public static Task<string> RenderWebAsync<TModel>(string viewPath, TModel model) =>
        RenderAsync(typeof(TwoStepController).Assembly, @"src\IPRO.Web", viewPath, model, null,
            new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "42"), new Claim("FullName", "Pat Lee") }, "test")));

    public static Task<string> RenderAdminAsync<TModel>(string viewPath, TModel model, Action<ViewDataDictionary> viewBag) =>
        RenderAsync(typeof(AgentsController).Assembly, @"src\IPRO.Admin", viewPath, model, viewBag,
            new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "owner-admin"), new Claim("Role", AdminRoles.SuperAdmin) }, "test")));

    private static async Task<string> RenderAsync<TModel>(System.Reflection.Assembly app, string root, string viewPath, TModel model,
        Action<ViewDataDictionary>? viewBag, ClaimsPrincipal user)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions
        {
            ApplicationName = app.GetName().Name,
            ContentRootPath = FindRepoDir(root),
            EnvironmentName = "Production"
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(app);
        await using var web = builder.Build();
        using var scope = web.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = user };
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "render"));
        var actionContext = new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var engine = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Mvc.Razor.IRazorViewEngine>();
        var found = engine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        Assert.True(found.Success, $"{viewPath} was not found among the compiled views");
        var viewData = new ViewDataDictionary<TModel>(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(),
            new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary()) { Model = model };
        viewBag?.Invoke(viewData);
        await using var writer = new StringWriter();
        await found.View!.RenderAsync(new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(
            actionContext, found.View, viewData, new TempDataDictionary(http, new NullTempData()), writer, new Microsoft.AspNetCore.Mvc.ViewFeatures.HtmlHelperOptions()));
        return writer.ToString();
    }

    public static string Read(string relative) =>
        File.ReadAllText(Path.Combine(FindRepoDir("."), relative)).Replace("\r\n", "\n");

    public static string FindRepoDir(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.GetFullPath(Path.Combine(dir!, relative));
    }
}
