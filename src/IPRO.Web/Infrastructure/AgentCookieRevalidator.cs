using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using IPRO.DataAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// M8 (2026-08-27). ADMIN-7 gave the Admin app a ValidatePrincipal on 2026-08-20 so a demoted or
// deactivated ADMIN lost their powers immediately instead of at cookie expiry. The agent portal
// never got the same treatment -- the audit's words were "fixed in Admin only" -- so an agent's
// 8-hour SLIDING cookie kept full portal access no matter what the database said. Deactivating an
// agent changed a row and nothing else; and because the session slides, an agent who kept using
// the portal could hold that access indefinitely.
//
// The case that makes it urgent: an agent DELETED through the eraser. Their rows are shredded,
// their site serves the not-published page -- and their cookie is still walking around the portal
// until it expires, against an account that no longer exists.
//
// One primary-key lookup per authenticated request (two when a team member is signed in). The
// same trade Admin made and for the same reason: correctness of revocation is worth far more than
// a cached claim.
public class AgentCookieRevalidator : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<IPRODbContext>();

        var agentIdClaim = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var teamMemberClaim = context.Principal?.FindFirstValue("TeamMemberId");

        if (await EvaluateAsync(db, agentIdClaim, teamMemberClaim) == Verdict.Reject)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        // 593: two-step sign-in. A session made before it was turned on (a "Remember me" week included),
        // or before it was turned off or reset, or before the password last changed, ends here; an account
        // required to have it, without it, may only set it up (Program.cs sends every other page there).
        switch (await EvaluateSignInAsync(db, agentIdClaim, teamMemberClaim, context.Principal?.FindFirstValue(TwoStepGate.ClaimType),
                    context.Principal?.FindFirstValue(PortalSignIn.PasswordClaimType)))
        {
            case TwoStepVerdict.Reject:
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                break;
            case TwoStepVerdict.SetupRequired:
                context.HttpContext.Items[TwoStepGate.SetupRequiredItem] = true;
                break;
        }
    }

    public enum Verdict { Ok, Reject }

    // The decision itself, separated so tests can drive the whole matrix without cookies -- the
    // shape AdminCookieRevalidator.Evaluate established.
    //
    // A team member signs in with their own email and acts AS the agent: NameIdentifier holds the
    // AGENT's id, with a TeamMemberId marker claim alongside. So a session can be revoked from
    // either end, and both are checked:
    //   - the agent must exist and be active (covers deletion, deactivation, and takes the whole
    //     team down with the account, which is correct -- the team's access is the agent's)
    //   - when the marker claim is present, that team member must also exist, be active, and
    //     still belong to THIS agent (the last guard is defensive: a marker claim must never be
    //     usable to ride another agent's session)
    public static async Task<Verdict> EvaluateAsync(IPRODbContext db, string? agentIdClaim, string? teamMemberIdClaim)
    {
        if (!int.TryParse(agentIdClaim, out var agentId)) return Verdict.Reject;

        var agent = await db.AgentUsers.AsNoTracking()
            .Where(a => a.Id == agentId)
            .Select(a => new { a.Id, a.IsActive })
            .FirstOrDefaultAsync();
        if (agent is null || !agent.IsActive) return Verdict.Reject;

        if (string.IsNullOrEmpty(teamMemberIdClaim)) return Verdict.Ok;

        if (!int.TryParse(teamMemberIdClaim, out var teamMemberId)) return Verdict.Reject;

        var member = await db.TeamMembers.AsNoTracking()
            .Where(m => m.Id == teamMemberId)
            .Select(m => new { m.Id, m.IsActive, m.AgentUserId })
            .FirstOrDefaultAsync();
        if (member is null || !member.IsActive || member.AgentUserId != agentId) return Verdict.Reject;

        return Verdict.Ok;
    }

    public enum TwoStepVerdict { Ok, Reject, SetupRequired }

    // 593. The session against the person (the adviser, or the team member signed in as themself):
    //   - the password: the session must carry the fingerprint of the password hash as it is now -- a new
    //     password since the session began ends it (PortalSignIn.PasswordClaimType);
    //   - two-step on: the session must carry the current stamp -- it passed the second step since the
    //     last time two-step was turned on (or its recovery codes were made new);
    //   - two-step off: a session carrying a stamp is from before it was turned off or reset, and ends;
    //   - off but required for the account (SuperAdmin): the session may only set it up.
    // One round trip (indexed lookups as subqueries of the agent's row), on every request.
    public static async Task<TwoStepVerdict> EvaluateSignInAsync(IPRODbContext db, string? agentIdClaim, string? teamMemberIdClaim,
        string? twoStepClaim, string? passwordClaim)
    {
        if (!int.TryParse(agentIdClaim, out var agentId)) return TwoStepVerdict.Reject;
        var memberId = 0;
        if (!string.IsNullOrEmpty(teamMemberIdClaim) && !int.TryParse(teamMemberIdClaim, out memberId)) return TwoStepVerdict.Reject;

        var state = await db.AgentUsers.AsNoTracking()
            .Where(a => a.Id == agentId)
            .Select(a => new
            {
                AdviserHash = a.PasswordHash,
                MemberHash = db.TeamMembers.Where(m => m.Id == memberId && m.AgentUserId == agentId).Select(m => m.PasswordHash).FirstOrDefault(),
                Stamp = db.TwoStepSignIns.Where(t => t.AgentUserId == agentId && t.TeamMemberId == memberId).Select(t => t.Stamp).FirstOrDefault(),
                Required = db.TwoStepRequirements.Any(r => r.AgentUserId == agentId)
            })
            .FirstOrDefaultAsync();
        if (state == null) return TwoStepVerdict.Reject;
        var passwordHash = memberId == 0 ? state.AdviserHash : state.MemberHash;
        if (passwordHash == null || string.IsNullOrEmpty(passwordClaim)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(TwoStepGate.Fingerprint(passwordHash)), Encoding.ASCII.GetBytes(passwordClaim)))
            return TwoStepVerdict.Reject;
        if (state.Stamp != null)
            return string.Equals(state.Stamp, twoStepClaim, StringComparison.Ordinal) ? TwoStepVerdict.Ok : TwoStepVerdict.Reject;
        if (!string.IsNullOrEmpty(twoStepClaim)) return TwoStepVerdict.Reject;
        return state.Required ? TwoStepVerdict.SetupRequired : TwoStepVerdict.Ok;
    }
}
