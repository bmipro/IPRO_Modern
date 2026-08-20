using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Infrastructure;

// ADMIN-7 (2026-08-20). An admin's role and active flag used to live ONLY in the 4-hour auth
// cookie: demoting or deactivating an admin changed the database and nothing else, so the demoted
// cookie kept its full powers until it expired -- up to four hours of access the owner believed
// revoked. This event re-checks the database on every authenticated request. With a handful of
// admin accounts that is one primary-key lookup per request, which is nothing; correctness of
// revocation is worth far more here than a cached claim.
public class AdminCookieRevalidator : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<IPRODbContext>();

        var idClaim = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        AdminUser? user = null;
        if (int.TryParse(idClaim, out var adminId))
        {
            user = await db.AdminUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == adminId);
        }

        var verdict = Evaluate(user, context.Principal?.FindFirstValue("Role"));
        if (verdict == PrincipalVerdict.Reject)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    public enum PrincipalVerdict { Ok, Reject }

    // The decision itself, separated so a test can drive the whole matrix without cookies.
    // Reject when: the account no longer exists, is deactivated, or its ROLE in the database no
    // longer matches the role baked into the cookie (a demoted SuperAdmin must lose the
    // SuperAdmin policy now, not at cookie expiry -- and a promotion equally takes effect by
    // signing in again rather than silently upgrading an old cookie).
    public static PrincipalVerdict Evaluate(AdminUser? user, string? roleClaim)
    {
        if (user == null || !user.IsActive) return PrincipalVerdict.Reject;
        if (!string.Equals(user.Role, roleClaim, StringComparison.Ordinal)) return PrincipalVerdict.Reject;
        return PrincipalVerdict.Ok;
    }
}
