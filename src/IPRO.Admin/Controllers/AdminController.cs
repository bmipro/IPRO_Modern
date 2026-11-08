using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using EndpointRateLimits = IPRO.Admin.Infrastructure.EndpointRateLimits;

namespace IPRO.Admin.Controllers;

public class AdminController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IPasswordHasher<AdminUser> _hasher;
    private readonly IAdminAuditLogService _auditLog;
    private readonly ILogger<AdminController> _logger;

    // 583: the same sentence for every admin name and for a name that is no admin.
    public const string LockedMessage =
        "Sign-in with this name is locked for 15 minutes after 5 wrong passwords in a row. Another Super Admin can unlock it under Admin Users.";

    public AdminController(IUnitOfWork uow, IPasswordHasher<AdminUser> hasher, IAdminAuditLogService auditLog, ILogger<AdminController> logger)
    {
        _uow = uow;
        _hasher = hasher;
        _auditLog = auditLog;
        _logger = logger;
    }

    // 492 (owner): /Admin and /Admin/ used to answer 404 on the admin host. The root is the dashboard,
    // and the cookie's LoginPath takes a signed-out visitor to /Admin/Login from there.
    [HttpGet] public IActionResult Index() => Redirect("/");

    // 617: someone already signed in has no use for the sign-in page, and a form from it would be refused.
    [HttpGet] public IActionResult Login() => User?.Identity?.IsAuthenticated == true ? Redirect("/") : View();

    [EnableRateLimiting(EndpointRateLimits.SignIn)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string username, string password)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var normalizedUsername = (username ?? string.Empty).Trim();
        var user = await _uow.AdminUsers.FirstOrDefaultAsync(u => u.Username == normalizedUsername);
        if (user != null && !SignInLockout.SameName(user.Username, normalizedUsername)) user = null;   // 583: by its letters

        // 583: 5 wrong passwords in a row lock the name for 15 minutes (SignInLockout); a locked
        // account's password is not checked, and a name that is no admin counts the same way.
        var candidates = user != null && user.IsActive
            ? new[] { new SignInCandidate(new SignInAccount(SignInAccountKind.Admin, user.Id),
                () => _hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? string.Empty) == PasswordVerificationResult.Success) }
            : Array.Empty<SignInCandidate>();
        var attempt = await SignInLockout.TryAsync(_uow.Context, SignInDoor.Admin, normalizedUsername, candidates, DateTime.UtcNow);
        SignInLockout.LogLocks(_logger, attempt, SignInDoor.Admin, IPRO.Utility.RateLimitVisitor.Key(HttpContext));
        var verified = attempt.Succeeded;

        if (!verified)
        {
            var shownName = normalizedUsername.Length > 100 ? normalizedUsername[..100] : normalizedUsername;
            await _auditLog.LogAsync(user?.Id ?? 0, user?.Username ?? "unknown", "LoginFailed",
                $"Failed login attempt for username '{shownName}'.{(attempt.Locked ? " Sign-in for this name is locked." : string.Empty)}");
            if (attempt.LockedNow.Count > 0 || attempt.UnknownNameLockedNow)
                await _auditLog.LogAsync(user?.Id ?? 0, user?.Username ?? "unknown", "SignInLocked",
                    $"Sign-in for '{shownName}' locked for 15 minutes after 5 wrong passwords in a row.");
            // Every refusal answers 1.5 seconds after the request began, however little it took to decide.
            await SignInLockout.PadAsync(started);
            ModelState.AddModelError("", attempt.Locked ? LockedMessage : "Invalid credentials.");
            return View();
        }

        user!.LastLoginAt = DateTime.UtcNow;
        _uow.AdminUsers.Update(user);
        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(user.Id, user.Username, "LoginSucceeded", $"Successful login for '{user.Username}'.");

        await SignInAdminAsync(user, new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(4) }, DateTime.UtcNow);

        return RedirectToAction("Index", "AdminDashboard");
    }

    // 648: the claims, with the sign-in's stamp and start time, are AdminSignIn's (AdminUsersController
    // makes the same cookie again when an admin gives their own account a new password).
    private async Task SignInAdminAsync(AdminUser user, AuthenticationProperties props, DateTime signedInAtUtc)
    {
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            IPRO.Admin.Infrastructure.AdminSignIn.Principal(user, signedInAtUtc),
            props);
    }

    // 648: ends every sign-in of this account -- on every computer, this one included -- without
    // changing the password. Signing in again starts a new one.
    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SignOutEverywhere()
    {
        var username = User.Identity?.Name ?? "unknown";
        if (int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId)
            && await IPRO.Admin.Infrastructure.AdminSignIn.EndEverySignInAsync(_uow.Context, adminId) != null)
        {
            await _auditLog.LogAsync(adminId, username, "SignedOutEverywhere",
                $"'{username}' signed out everywhere: every sign-in of this account ended, on every computer.");
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        TempData["SignedOut"] = "You are signed out on every computer and phone. Sign in again here with your usual password.";
        return RedirectToAction(nameof(Login));
    }

    private static readonly HashSet<string> PortalAccentColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "#1457d9", "#d9541f", "#1f7a4d", "#4b5563", "#7a1f3d", "#5b2f9e"
    };

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPortalAccentColor(string? color, string? returnUrl)
    {
        var username = User.Identity?.Name;
        var user = string.IsNullOrWhiteSpace(username) ? null : await _uow.AdminUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return RedirectToAction(nameof(Login));

        if (!string.IsNullOrWhiteSpace(color) && PortalAccentColors.Contains(color))
        {
            user.PortalAccentColor = color.ToLowerInvariant();
            _uow.AdminUsers.Update(user);
            await _uow.SaveChangesAsync();
            await SignInAdminAsync(user, new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(4) },
                IPRO.Admin.Infrastructure.AdminSignIn.SignedInAtOf(User) ?? DateTime.UtcNow);
        }

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToAction("Index", "AdminDashboard");
    }

    public async Task<IActionResult> Logout()
    {
        var username = User.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(username))
        {
            var user = await _uow.AdminUsers.FirstOrDefaultAsync(u => u.Username == username);
            await _auditLog.LogAsync(user?.Id ?? 0, user?.Username ?? "unknown", "Logout", $"'{username}' signed out.");
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }
    public IActionResult AccessDenied() => View();
    public IActionResult Error()
    {
        if (User.HasClaim("Role", AdminRoles.SuperAdmin))
        {
            var exceptionFeature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
            ViewBag.ExceptionPath = exceptionFeature?.Path;
            ViewBag.ExceptionType = exceptionFeature?.Error.GetType().FullName;
            ViewBag.ExceptionMessage = exceptionFeature?.Error.Message;
        }

        return View();
    }
}
