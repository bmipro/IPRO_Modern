using System.Security.Claims;
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
}
