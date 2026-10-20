using System.Security.Claims;
using IPRO.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace IPRO.Web.Infrastructure;

// The adviser portal's session cookie, made in one place: by the sign-in page, the forgot-password reset,
// registration, the second step of two-step sign-in (593), and again whenever the session's own account
// changes (profile, colour, password). Its claims were written out in AccountController until 593 needed
// them in TwoStepController too.
public static class PortalSignIn
{
    // 593 (review): a fingerprint of the password hash the session was made with. AgentCookieRevalidator
    // ends a session whose password has changed since: a new password (Forgot password, Change Password,
    // a temporary one from SuperAdmin or the account owner) signs out every other browser, as the lock and
    // two-step emails tell people to do when it was not them.
    public const string PasswordClaimType = "PasswordStamp";

    // "Remember me": a week that survives closing the browser; otherwise 8 hours.
    public static AuthenticationProperties Session(bool rememberMe) =>
        new() { IsPersistent = rememberMe, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(rememberMe ? 168 : 8) };

    // `twoStepStamp`: the account's two-step stamp when the session passed the second step (or came from a
    // session that had); null when two-step is off. AgentCookieRevalidator compares it on every request.
    public static Task AdviserAsync(HttpContext http, AgentUser user, AuthenticationProperties props, string? twoStepStamp)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Email, user.Email),
            new("FullName", $"{user.FirstName} {user.LastName}"),
            new("PackageId", user.PackageId.ToString()),
            new("MustChangePassword", user.MustChangePassword ? "true" : "false"),
            new("PortalAccentColor", user.PortalAccentColor ?? ""),
            new(PasswordClaimType, TwoStepGate.Fingerprint(user.PasswordHash))
        };
        if (!string.IsNullOrEmpty(twoStepStamp)) claims.Add(new Claim(TwoStepGate.ClaimType, twoStepStamp));
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), props);
    }

    // A team member authenticates with their OWN credentials but acts AS the owning agent:
    // ClaimTypes.NameIdentifier is the agent's id, so every controller in the portal works
    // unchanged. The TeamMemberId marker claim is what keeps Billing and Team management
    // owner-only (middleware in Program.cs) and routes ChangePassword at the member's own row.
    public static Task TeamMemberAsync(HttpContext http, TeamMember member, AgentUser owner, AuthenticationProperties props, string? twoStepStamp)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, owner.Id.ToString()),
            new(ClaimTypes.Name, owner.UserName),
            new(ClaimTypes.Email, member.Email),
            new("FullName", member.FullName),
            new("PackageId", owner.PackageId.ToString()),
            new("MustChangePassword", member.MustChangePassword ? "true" : "false"),
            new("PortalAccentColor", owner.PortalAccentColor ?? ""),
            new("TeamMemberId", member.Id.ToString()),
            new(PasswordClaimType, TwoStepGate.Fingerprint(member.PasswordHash))
        };
        if (!string.IsNullOrEmpty(twoStepStamp)) claims.Add(new Claim(TwoStepGate.ClaimType, twoStepStamp));
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), props);
    }
}
