using System.Security.Claims;
using IPRO.DataAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// Pre-launch audit, 2026-08-30. The third and last instance of one defect: an 8-hour SLIDING
// cookie that nothing ever re-checked against the database. Admin got ValidatePrincipal on
// 2026-08-20 (ADMIN-7), the agent portal on 2026-08-27 (M8, AgentCookieRevalidator) -- the CLIENT
// portal never did, so "Revoke portal access" nulled the password hash, told the agent "Portal
// access revoked", and changed nothing for a client already signed in.
//
// What that client kept: every document under /ClientPortalDocuments -- INCLUDING documents the
// agent uploaded after the revocation -- invoices, appointment requests, messages into the agent's
// inbox, and writes to their own contact record. Because the session slides, anyone who touched a
// portal page inside each 8-hour window held it indefinitely, and the agent had no way to end it.
//
// One primary-key lookup per authenticated request, plus one for the owning agent. Same trade the
// other two schemes made: correctness of revocation is worth more than a cached claim.
public class ClientPortalCookieRevalidator : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<IPRODbContext>();

        var clientIdClaim = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var agentIdClaim = context.Principal?.FindFirstValue("AgentUserId");

        if (await EvaluateAsync(db, clientIdClaim, agentIdClaim) == Verdict.Reject)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync("ClientPortal");
        }
    }

    public enum Verdict { Ok, Reject }

    // The decision itself, separated so tests drive the whole matrix without cookies -- the shape
    // AdminCookieRevalidator and AgentCookieRevalidator established.
    //
    // Rejected when:
    //   - the client row is gone (deleted, or erased with their agent)
    //   - portal access was revoked: RevokePortal nulls PortalPasswordHash, which is precisely
    //     "this person may no longer sign in", so it must also mean "and may no longer stay in"
    //   - the owning agent is gone or deactivated -- a client's access is their agent's, the same
    //     rule team members already live under
    //   - the cookie's AgentUserId is not the client's real owner (defensive: a claim must never
    //     be usable to ride another agent's tenancy)
    public static async Task<Verdict> EvaluateAsync(IPRODbContext db, string? clientIdClaim, string? agentIdClaim)
    {
        if (!int.TryParse(clientIdClaim, out var clientId)) return Verdict.Reject;
        if (!int.TryParse(agentIdClaim, out var agentId)) return Verdict.Reject;

        var client = await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId)
            .Select(c => new { c.Id, c.AgentUserId, c.PortalPasswordHash })
            .FirstOrDefaultAsync();
        if (client is null) return Verdict.Reject;
        if (string.IsNullOrEmpty(client.PortalPasswordHash)) return Verdict.Reject;
        if (client.AgentUserId != agentId) return Verdict.Reject;

        var agent = await db.AgentUsers.AsNoTracking()
            .Where(a => a.Id == client.AgentUserId)
            .Select(a => new { a.Id, a.IsActive })
            .FirstOrDefaultAsync();
        if (agent is null || !agent.IsActive) return Verdict.Reject;

        return Verdict.Ok;
    }
}
