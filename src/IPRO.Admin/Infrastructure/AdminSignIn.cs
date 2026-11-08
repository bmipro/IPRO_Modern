using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Infrastructure;

// 648 (found in the system check of 2026-11-07). A SuperAdmin sign-in used to end only when the
// account was deactivated, deleted or changed role: the cookie slides (4 hours, renewed by every
// request), so a Job Scheduler page left open on another computer reloaded itself once a minute and
// kept one sign-in alive from 2026-11-01 to 2026-11-08, and a new password would not have ended it.
// Every sign-in now carries
//   - a stamp of the account as it was when the sign-in began: the password hash and SignInVersion.
//     A new password, or "Sign out everywhere" (which raises SignInVersion), changes the stamp and
//     every sign-in made before ends at its next request (AdminCookieRevalidator);
//   - the moment it began. However often it is renewed, a sign-in ends MaxLifetime after it.
// A cookie made before 648 carries neither, so the release itself ends every SuperAdmin sign-in once.
public static class AdminSignIn
{
    public const string StampClaimType = "SignInStamp";
    public const string SignedInAtClaimType = "SignedInAt";
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(12);

    public static string Stamp(AdminUser user) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            user.PasswordHash + "|" + user.SignInVersion.ToString(CultureInfo.InvariantCulture))))[..32];

    public static string SignedInAt(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    // When the sign-in carried by this principal began; null when it carries no readable time.
    public static DateTime? SignedInAtOf(ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirstValue(SignedInAtClaimType);
        return DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
               && at.Kind == DateTimeKind.Utc
            ? at
            : null;
    }

    // signedInAtUtc: when the sign-in began. A cookie made again for the same sign-in (a new accent
    // colour, the account's own new password) passes the old time on, so it does not start the 12 hours again.
    public static ClaimsPrincipal Principal(AdminUser user, DateTime signedInAtUtc)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("Role", user.Role),
            new("FullName", user.FullName),
            new("PortalAccentColor", user.PortalAccentColor ?? ""),
            new(StampClaimType, Stamp(user)),
            new(SignedInAtClaimType, SignedInAt(signedInAtUtc))
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    // "Sign out everywhere": one statement, so no save of an older copy of the row can undo it
    // (IPRODbContext never writes SignInVersion from a save). Returns the account's new number.
    public static async Task<int?> EndEverySignInAsync(IPRODbContext db, int adminId)
    {
        await db.Database.ExecuteSqlRawAsync("UPDATE `AdminUsers` SET `SignInVersion` = `SignInVersion` + 1 WHERE `Id` = {0}", adminId);
        return await db.AdminUsers.AsNoTracking().Where(a => a.Id == adminId).Select(a => (int?)a.SignInVersion).FirstOrDefaultAsync();
    }
}
