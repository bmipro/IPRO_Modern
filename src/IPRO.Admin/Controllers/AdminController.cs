using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

public class AdminController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IPasswordHasher<AdminUser> _hasher;
    private readonly IAdminAuditLogService _auditLog;

    public AdminController(IUnitOfWork uow, IPasswordHasher<AdminUser> hasher, IAdminAuditLogService auditLog)
    {
        _uow = uow;
        _hasher = hasher;
        _auditLog = auditLog;
    }

    [HttpGet] public IActionResult Login() => View();

    [HttpPost]
    public async Task<IActionResult> Login(string username, string password)
    {
        var normalizedUsername = (username ?? string.Empty).Trim();
        var user = await _uow.AdminUsers.FirstOrDefaultAsync(u => u.Username == normalizedUsername);

        var verified = user != null && user.IsActive &&
            _hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? string.Empty) == PasswordVerificationResult.Success;

        if (!verified)
        {
            await _auditLog.LogAsync(user?.Id ?? 0, user?.Username ?? "unknown", "LoginFailed", $"Failed login attempt for username '{normalizedUsername}'.");
            await Task.Delay(1500); // Slow brute force
            ModelState.AddModelError("", "Invalid credentials.");
            return View();
        }

        user!.LastLoginAt = DateTime.UtcNow;
        _uow.AdminUsers.Update(user);
        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(user.Id, user.Username, "LoginSucceeded", $"Successful login for '{user.Username}'.");

        await SignInAdminAsync(user, new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(4) });

        return RedirectToAction("Index", "AdminDashboard");
    }

    private async Task SignInAdminAsync(AdminUser user, AuthenticationProperties props)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("Role", user.Role),
            new("FullName", user.FullName),
            new("PortalAccentColor", user.PortalAccentColor ?? "")
        };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            props);
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
            await SignInAdminAsync(user, new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(4) });
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
