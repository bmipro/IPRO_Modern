using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Admin.Controllers;
using IPRO.Admin.Infrastructure;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Verdict = IPRO.Admin.Infrastructure.AdminCookieRevalidator.PrincipalVerdict;

namespace IPRO.IntegrationTests;

// 648 (found in the system check of 2026-11-07). A SuperAdmin sign-in ended only on deactivation,
// deletion or a role change: a Job Scheduler page left open on another computer kept one alive for a
// week, and a new password would not have ended it. Now every sign-in carries a stamp (the password hash
// and SignInVersion) and its start time; a new password or "Sign out everywhere" ends every sign-in made
// before, and none lasts more than 12 hours however often it is renewed.
public class SignOutEverywhere648Tests
{
    private static readonly DateTime Now = new(2026, 11, 8, 17, 0, 0, DateTimeKind.Utc);

    private static AdminUser Account(string hash = "hash-one", int version = 0) =>
        new() { Id = 7, Username = "boss", FullName = "Boss", PasswordHash = hash, Role = AdminRoles.SuperAdmin, IsActive = true, SignInVersion = version };

    [Fact]
    public void A_sign_in_ends_with_a_new_password_a_press_of_sign_out_everywhere_or_twelve_hours()
    {
        var account = Account();
        var stamp = AdminSignIn.Stamp(account);

        Assert.Equal(Verdict.Ok, AdminCookieRevalidator.EvaluateSignIn(account, stamp, Now.AddHours(-1), Now));
        Assert.Equal(Verdict.Ok, AdminCookieRevalidator.EvaluateSignIn(account, stamp, Now.AddHours(-11).AddMinutes(-59), Now));
        // Twelve hours after it began, however often the 4-hour cookie was renewed.
        Assert.Equal(Verdict.Reject, AdminCookieRevalidator.EvaluateSignIn(account, stamp, Now.AddHours(-12).AddMinutes(-1), Now));
        // A start time from the future (beyond five minutes of clock difference) is not believed.
        Assert.Equal(Verdict.Reject, AdminCookieRevalidator.EvaluateSignIn(account, stamp, Now.AddMinutes(6), Now));
        // A cookie from before 648: no stamp, or no start time.
        Assert.Equal(Verdict.Reject, AdminCookieRevalidator.EvaluateSignIn(account, null, Now.AddHours(-1), Now));
        Assert.Equal(Verdict.Reject, AdminCookieRevalidator.EvaluateSignIn(account, "", Now.AddHours(-1), Now));
        Assert.Equal(Verdict.Reject, AdminCookieRevalidator.EvaluateSignIn(account, stamp, null, Now));
        // A new password since it began.
        Assert.Equal(Verdict.Reject, AdminCookieRevalidator.EvaluateSignIn(Account(hash: "hash-two"), stamp, Now.AddHours(-1), Now));
        // "Sign out everywhere" since it began.
        Assert.Equal(Verdict.Reject, AdminCookieRevalidator.EvaluateSignIn(Account(version: 1), stamp, Now.AddHours(-1), Now));
        // The stamp says nothing readable about the password.
        Assert.DoesNotContain("hash-one", stamp);
        Assert.Equal(32, stamp.Length);
    }

    [Fact]
    public void The_start_time_is_read_back_only_as_a_universal_time()
    {
        var at = AdminSignIn.SignedInAtOf(Principal(new Claim(AdminSignIn.SignedInAtClaimType, AdminSignIn.SignedInAt(Now))));
        Assert.Equal(Now, at);
        Assert.Equal(DateTimeKind.Utc, at!.Value.Kind);
        Assert.Null(AdminSignIn.SignedInAtOf(Principal(new Claim(AdminSignIn.SignedInAtClaimType, "2026-11-08 17:00"))));
        Assert.Null(AdminSignIn.SignedInAtOf(Principal(new Claim(AdminSignIn.SignedInAtClaimType, "2026-11-08T17:00:00.0000000"))));
        Assert.Null(AdminSignIn.SignedInAtOf(Principal()));
        Assert.Null(AdminSignIn.SignedInAtOf(null));
    }

    [Fact]
    public async Task Signing_in_stamps_the_sign_in_and_sign_out_everywhere_ends_it_on_every_computer()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var adminId = await Lock583.SeedAdminAsync(testDb, "boss");

        // Two sign-ins of the same account: this computer, and one left open elsewhere.
        var here = await SignInAsync(testDb, "boss");
        var elsewhere = await SignInAsync(testDb, "boss");
        await using (var db = testDb.CreateContext())
        {
            var account = await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Id == adminId);
            Assert.Equal(0, account.SignInVersion);
            foreach (var principal in new[] { here, elsewhere })
            {
                Assert.Equal(AdminSignIn.Stamp(account), principal.FindFirstValue(AdminSignIn.StampClaimType));
                var began = AdminSignIn.SignedInAtOf(principal);
                Assert.NotNull(began);
                Assert.True((DateTime.UtcNow - began!.Value).Duration() < TimeSpan.FromMinutes(1));
                Assert.Equal(Verdict.Ok, AdminCookieRevalidator.EvaluateSignIn(account, principal.FindFirstValue(AdminSignIn.StampClaimType), began, DateTime.UtcNow));
            }
        }

        // "Sign out everywhere", pressed here.
        RecordingAuthentication auth;
        IActionResult result;
        await using (var db = testDb.CreateContext())
        {
            var controller = AdminControllerFor(db, here);
            auth = Record(controller);
            result = await controller.SignOutEverywhere();
            Assert.Equal("You are signed out on every computer and phone. Sign in again here with your usual password.", controller.TempData["SignedOut"]);
        }
        Assert.Equal(nameof(AdminController.Login), Assert.IsType<RedirectToActionResult>(result).ActionName);
        Assert.Equal(1, auth.SignOuts);

        await using (var db = testDb.CreateContext())
        {
            var account = await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Id == adminId);
            Assert.Equal(1, account.SignInVersion);
            // Both sign-ins end at their next request -- the one left open elsewhere included.
            foreach (var principal in new[] { here, elsewhere })
                Assert.Equal(Verdict.Reject, AdminCookieRevalidator.EvaluateSignIn(account, principal.FindFirstValue(AdminSignIn.StampClaimType),
                    AdminSignIn.SignedInAtOf(principal), DateTime.UtcNow));
            var entry = await db.AdminAuditLogEntries.AsNoTracking().SingleAsync(e => e.Action == "SignedOutEverywhere");
            Assert.Equal(adminId, entry.AdminUserId);
            Assert.Equal("boss", entry.AdminUsername);
        }

        // The password did not change: signing in again works, and makes a sign-in that is good.
        var again = await SignInAsync(testDb, "boss");
        await using (var db = testDb.CreateContext())
        {
            var account = await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Id == adminId);
            Assert.Equal(Verdict.Ok, AdminCookieRevalidator.EvaluateSignIn(account, again.FindFirstValue(AdminSignIn.StampClaimType),
                AdminSignIn.SignedInAtOf(again), DateTime.UtcNow));
        }
    }

    [Fact]
    public async Task A_save_of_a_copy_read_before_the_press_does_not_bring_the_old_sign_ins_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var adminId = await Lock583.SeedAdminAsync(testDb, "boss");

        await using var older = testDb.CreateContext();
        var copy = await older.AdminUsers.SingleAsync(a => a.Id == adminId);   // read before the press, version 0

        await using (var db = testDb.CreateContext())
            Assert.Equal(1, await AdminSignIn.EndEverySignInAsync(db, adminId));

        // SuperAdmin's Edit, a sign-in's LastLoginAt, an accent colour: each saves the row it holds.
        copy.FullName = "Boss Renamed";
        copy.LastLoginAt = DateTime.UtcNow;
        new UnitOfWork(older).AdminUsers.Update(copy);
        await older.SaveChangesAsync();

        await using (var db = testDb.CreateContext())
        {
            var account = await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Id == adminId);
            Assert.Equal("Boss Renamed", account.FullName);
            Assert.Equal(1, account.SignInVersion);
        }
        // An account that is not there: nothing raised, nothing to report.
        await using (var db = testDb.CreateContext())
            Assert.Null(await AdminSignIn.EndEverySignInAsync(db, adminId + 1000));
    }

    [Fact]
    public async Task A_new_password_ends_the_old_sign_ins_and_an_admins_own_new_password_keeps_this_one()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var ownerId = await Lock583.SeedAdminAsync(testDb, "owner-admin");
        var staffId = await Lock583.SeedAdminAsync(testDb, "staff");
        var owner = await SignInAsync(testDb, "owner-admin");
        var ownerElsewhere = await SignInAsync(testDb, "owner-admin");
        var staff = await SignInAsync(testDb, "staff");

        // The owner gives the staff account a new password: the staff sign-in ends.
        await using (var db = testDb.CreateContext())
        {
            var controller = AdminUsersControllerFor(db, owner);
            var auth = Record(controller);
            await controller.ResetPassword(staffId, "a-new-password-1");
            Assert.Null(auth.SignedIn);   // the owner's own sign-in is not touched
            Assert.Equal("Password reset for staff. Any sign-in they had open has ended.", controller.TempData["Success"]);
        }
        await using (var db = testDb.CreateContext())
        {
            var staffAccount = await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Id == staffId);
            Assert.Equal(Verdict.Reject, AdminCookieRevalidator.EvaluateSignIn(staffAccount, staff.FindFirstValue(AdminSignIn.StampClaimType),
                AdminSignIn.SignedInAtOf(staff), DateTime.UtcNow));
        }

        // The owner gives their own account a new password: the sign-in elsewhere ends, this one stays,
        // re-made with the new stamp and its original start time.
        ClaimsPrincipal? remade;
        await using (var db = testDb.CreateContext())
        {
            var controller = AdminUsersControllerFor(db, owner);
            var auth = Record(controller);
            await controller.ResetPassword(ownerId, "another-password-2");
            remade = auth.SignedIn;
            Assert.Equal("Password reset for owner-admin. Every other sign-in of this account has ended; this one stays.", controller.TempData["Success"]);
        }
        Assert.NotNull(remade);
        Assert.Equal(AdminSignIn.SignedInAtOf(owner), AdminSignIn.SignedInAtOf(remade));
        await using (var db = testDb.CreateContext())
        {
            var ownerAccount = await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Id == ownerId);
            Assert.Equal(Verdict.Ok, AdminCookieRevalidator.EvaluateSignIn(ownerAccount, remade!.FindFirstValue(AdminSignIn.StampClaimType),
                AdminSignIn.SignedInAtOf(remade), DateTime.UtcNow));
            Assert.Equal(Verdict.Reject, AdminCookieRevalidator.EvaluateSignIn(ownerAccount, ownerElsewhere.FindFirstValue(AdminSignIn.StampClaimType),
                AdminSignIn.SignedInAtOf(ownerElsewhere), DateTime.UtcNow));
        }
    }

    [Fact]
    public void The_button_the_check_and_the_column_are_where_they_belong()
    {
        var layout = Lock583.Read(@"src\IPRO.Admin\Views\Shared\_Layout.cshtml");
        Assert.Contains("<form method=\"post\" action=\"/Admin/SignOutEverywhere\" class=\"m-0\">\n            @Html.AntiForgeryToken()", layout);
        Assert.Contains("<i class=\"fas fa-power-off\"></i>Sign out everywhere", layout);
        Assert.Contains("@if (TempData[\"SignedOut\"] is string signedOut)", Lock583.Read(@"src\IPRO.Admin\Views\Admin\Login.cshtml"));

        var revalidator = Lock583.Read(@"src\IPRO.Admin\Infrastructure\AdminCookieRevalidator.cs");
        Assert.Contains("if (verdict == PrincipalVerdict.Ok)\n            verdict = EvaluateSignIn(user!, context.Principal?.FindFirstValue(AdminSignIn.StampClaimType),", revalidator);

        var controller = Lock583.Read(@"src\IPRO.Admin\Controllers\AdminController.cs");
        Assert.Contains("IPRO.Admin.Infrastructure.AdminSignIn.Principal(user, signedInAtUtc)", controller);
        Assert.Contains("[Authorize]\n    [HttpPost, ValidateAntiForgeryToken]\n    public async Task<IActionResult> SignOutEverywhere()", controller);
        // Only the sign-in starts the 12 hours; a new accent colour carries the start over.
        Assert.Contains("ExpiresUtc = DateTimeOffset.UtcNow.AddHours(4) }, DateTime.UtcNow);", controller);
        Assert.Contains("IPRO.Admin.Infrastructure.AdminSignIn.SignedInAtOf(User) ?? DateTime.UtcNow);", controller);

        Assert.Contains("await StartupSchemaRepair.EnsureTableColumnAsync(db, \"AdminUsers\", \"SignInVersion\", \"ALTER TABLE `AdminUsers` ADD COLUMN `SignInVersion` int NOT NULL DEFAULT 0\");",
            Lock583.Read(@"src\IPRO.Admin\Program.cs"));
        Assert.Contains("e.Property(a => a.SignInVersion).Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);",
            Lock583.Read(@"src\IPRO.DataAccess\IPRODbContext.cs"));
    }

    // ---- harness ------------------------------------------------------------------------------

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    // The real sign-in action against the test database; returns the principal it signed in.
    private static async Task<ClaimsPrincipal> SignInAsync(TestDatabase testDb, string name)
    {
        await using var db = testDb.CreateContext();
        var controller = Lock583.Prepare(new AdminController(new UnitOfWork(db), new PasswordHasher<AdminUser>(),
            new AdminAuditLogService(new UnitOfWork(db)), new RecordingLogger().For<AdminController>()));
        var auth = Record(controller);
        Assert.IsType<RedirectToActionResult>(await controller.Login(name, Lock583.Password));
        Assert.NotNull(auth.SignedIn);
        return auth.SignedIn!;
    }

    private static AdminController AdminControllerFor(IPRODbContext db, ClaimsPrincipal user) =>
        Lock583.Prepare(new AdminController(new UnitOfWork(db), new PasswordHasher<AdminUser>(),
            new AdminAuditLogService(new UnitOfWork(db)), new RecordingLogger().For<AdminController>()), user);

    private static AdminUsersController AdminUsersControllerFor(IPRODbContext db, ClaimsPrincipal user) =>
        Lock583.Prepare(new AdminUsersController(new UnitOfWork(db), new PasswordHasher<AdminUser>(),
            new AdminAuditLogService(new UnitOfWork(db)), new RecordingLogger().For<AdminUsersController>()), user);

    private static RecordingAuthentication Record(Controller controller)
    {
        var auth = new RecordingAuthentication();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(auth);
        controller.HttpContext.RequestServices = services.BuildServiceProvider();
        return auth;
    }

    private sealed class RecordingAuthentication : IAuthenticationService
    {
        public ClaimsPrincipal? SignedIn { get; private set; }
        public int SignOuts { get; private set; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignedIn = principal;
            return Task.CompletedTask;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignOuts++;
            return Task.CompletedTask;
        }
    }
}
