using System.Net;
using System.Security.Cryptography;
using System.Text;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// 679 (audit 2026-11-24 A-3): a team member's way back in that a stranger cannot shut. Sign-in locks for 15 minutes
// after 5 wrong passwords, so anyone who knows a team member's email can keep them locked out from one address. An
// adviser gets in with Forgot password and a client with a new invitation; a team member had neither (the owner's
// password reset clears the lock, but the next sign-in goes through the same form). Now the owner can email them a
// one-time link from My Team: it sets a new password and signs them in without the sign-in form, so the lock does not
// stand in the way. Two-step sign-in still follows, as after Forgot password: the link proves the inbox, not the phone.
public static class TeamSignInLinks
{
    public const int ValidMinutes = 60;

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    // A new link for the member (any earlier one stops working); the token goes in the email only.
    public static string Issue(TeamMember member, DateTime nowUtc)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        member.SignInLinkHash = Hash(token);
        member.SignInLinkExpiresAt = nowUtc.AddMinutes(ValidMinutes);
        return token;
    }

    // The member the link is for, while it is unused and in time, and they and their owner are active.
    public static async Task<TeamMember?> FindAsync(IPRODbContext db, string? token, DateTime nowUtc)
    {
        var trimmed = token?.Trim() ?? string.Empty;
        if (trimmed.Length < 20 || trimmed.Length > 100) return null;
        var hash = Hash(trimmed);
        return await db.TeamMembers.Include(t => t.AgentUser)
            .FirstOrDefaultAsync(t => t.SignInLinkHash == hash && t.SignInLinkExpiresAt > nowUtc && t.IsActive && t.AgentUser.IsActive);
    }

    public static string Url(string portalBaseUrl, string token) =>
        $"{portalBaseUrl.TrimEnd('/')}/Account/TeamSignIn?t={Uri.EscapeDataString(token)}";

    public static string Subject(AgentUser owner) => $"Your sign-in link for {AdviserSender.BusinessName(owner)} on iPro";

    public static string Html(TeamMember member, AgentUser owner, string url)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var ownerName = $"{owner.FirstName} {owner.LastName}".Trim();
        return $@"<div style=""font-family:Arial,Helvetica,sans-serif;max-width:560px;margin:auto;color:#17223a;font-size:15px;line-height:1.55"">
<p>Hello {E(member.FullName)},</p>
<p>{E(ownerName.Length == 0 ? AdviserSender.BusinessName(owner) : ownerName)} sent you this link so you can choose a new password and sign in to iPro.</p>
<p style=""margin:22px 0""><a href=""{E(url)}"" style=""display:inline-block;padding:12px 22px;background:#1457d9;color:#ffffff;text-decoration:none;border-radius:6px;font-weight:bold"">Choose a password and sign in</a></p>
<p>The link works once, within {ValidMinutes} minutes. If you did not expect it, ignore this email and tell {E(ownerName.Length == 0 ? "the account owner" : ownerName)}.</p>
</div>";
    }
}
