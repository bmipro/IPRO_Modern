using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
        // 648: and the sign-in itself -- made before the password changed or "Sign out everywhere"
        // was pressed, older than 12 hours, or from before 648 (no stamp): it ends here.
        if (verdict == PrincipalVerdict.Ok)
            verdict = EvaluateSignIn(user!, context.Principal?.FindFirstValue(AdminSignIn.StampClaimType),
                AdminSignIn.SignedInAtOf(context.Principal), DateTime.UtcNow);
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

    // 648. Reject when the sign-in carries no stamp or one that is not the account's stamp now (a new
    // password, or "Sign out everywhere", since it began), or no start time, or a start more than
    // AdminSignIn.MaxLifetime ago (or more than five minutes ahead of this server's clock).
    public static PrincipalVerdict EvaluateSignIn(AdminUser user, string? stampClaim, DateTime? signedInAtUtc, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(stampClaim)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(AdminSignIn.Stamp(user)), Encoding.ASCII.GetBytes(stampClaim)))
            return PrincipalVerdict.Reject;
        if (signedInAtUtc is not { } at || at > nowUtc.AddMinutes(5) || nowUtc - at > AdminSignIn.MaxLifetime)
            return PrincipalVerdict.Reject;
        return PrincipalVerdict.Ok;
    }
}
