using System.Security.Claims;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Web.Controllers;

// Owner-only management of team-member logins (#379). The middleware already bounces team-member
// sessions off /Team entirely; the OwnerOnly() check below is defence in depth for the same rule.
// Seats come from the team_members package feature (Silver 1 / Gold 2 / Platinum 5 by default,
// SuperAdmin-adjustable per package like every other entitlement).
[Authorize]
public class TeamController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IPasswordHasher<TeamMember> _hasher;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsTeamMemberSession => User.FindFirst("TeamMemberId") != null;

    public TeamController(IUnitOfWork uow, IPasswordHasher<TeamMember> hasher)
    {
        _uow = uow;
        _hasher = hasher;
    }

    public async Task<IActionResult> Index()
    {
        if (IsTeamMemberSession) return Redirect("/portal/Dashboard");

        var members = (await _uow.TeamMembers.FindAsync(t => t.AgentUserId == AgentId))
            .OrderBy(t => t.CreatedAt)
            .ToList();
        ViewBag.SeatLimit = await ResolveSeatLimitAsync();
        ViewBag.ActiveCount = members.Count(m => m.IsActive);
        ViewBag.AgentTimeZone = (await _uow.AgentUsers.GetByIdAsync(AgentId))?.TimeZone;
        return View(members);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(string fullName, string email)
    {
        if (IsTeamMemberSession) return Redirect("/portal/Dashboard");

        fullName = (fullName ?? "").Trim();
        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            TempData["Error"] = "A name and a valid email address are both required.";
            return RedirectToAction(nameof(Index));
        }

        var seatLimit = await ResolveSeatLimitAsync();
        var activeCount = (await _uow.TeamMembers.FindAsync(t => t.AgentUserId == AgentId && t.IsActive)).Count();
        if (activeCount >= seatLimit)
        {
            TempData["Error"] = seatLimit == 0
                ? "Your package does not include team member logins. Upgrade to add staff."
                : $"Your package includes {seatLimit} team member seat{(seatLimit == 1 ? "" : "s")} and all are in use. Deactivate a member or upgrade your package.";
            return RedirectToAction(nameof(Index));
        }

        // The email must be unique across BOTH login tables: at sign-in agents are tried first, so
        // a team member sharing an agent's email could never authenticate.
        var takenByAgent = await _uow.AgentUsers.FirstOrDefaultAsync(a => a.Email == email);
        var takenByMember = await _uow.TeamMembers.FirstOrDefaultAsync(t => t.Email == email);
        if (takenByAgent != null || takenByMember != null)
        {
            TempData["Error"] = "That email address is already used by another login.";
            return RedirectToAction(nameof(Index));
        }

        var temporaryPassword = EncryptionService.GenerateToken(12);
        var member = new TeamMember
        {
            AgentUserId = AgentId,
            FullName = fullName,
            Email = email,
            MustChangePassword = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        member.PasswordHash = _hasher.HashPassword(member, temporaryPassword);
        await _uow.TeamMembers.AddAsync(member);
        await _uow.SaveChangesAsync();

        // Shown ONCE, never emailed (C-1: credentials do not travel by email). The owner hands it
        // to their staff member; first login forces a password change.
        TempData["TeamTempPassword"] = temporaryPassword;
        TempData["TeamTempPasswordFor"] = $"{fullName} ({email})";
        TempData["Success"] = $"{fullName} can now sign in at the regular login page with their email address.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(int id)
    {
        if (IsTeamMemberSession) return Redirect("/portal/Dashboard");
        var member = await GetOwnedMemberAsync(id);
        if (member == null) return NotFound();

        var temporaryPassword = EncryptionService.GenerateToken(12);
        member.PasswordHash = _hasher.HashPassword(member, temporaryPassword);
        member.MustChangePassword = true;
        _uow.TeamMembers.Update(member);
        await _uow.SaveChangesAsync();

        TempData["TeamTempPassword"] = temporaryPassword;
        TempData["TeamTempPasswordFor"] = $"{member.FullName} ({member.Email})";
        TempData["Success"] = $"Password reset for {member.FullName}. They must change it at first login.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        if (IsTeamMemberSession) return Redirect("/portal/Dashboard");
        var member = await GetOwnedMemberAsync(id);
        if (member == null) return NotFound();

        if (!member.IsActive)
        {
            var seatLimit = await ResolveSeatLimitAsync();
            var activeCount = (await _uow.TeamMembers.FindAsync(t => t.AgentUserId == AgentId && t.IsActive)).Count();
            if (activeCount >= seatLimit)
            {
                TempData["Error"] = $"All {seatLimit} seat{(seatLimit == 1 ? " is" : "s are")} in use — deactivate another member first.";
                return RedirectToAction(nameof(Index));
            }
        }

        member.IsActive = !member.IsActive;
        _uow.TeamMembers.Update(member);
        await _uow.SaveChangesAsync();
        TempData["Success"] = member.IsActive
            ? $"{member.FullName} can sign in again."
            : $"{member.FullName} can no longer sign in. Their existing session ends within 8 hours; reset their password to be certain.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (IsTeamMemberSession) return Redirect("/portal/Dashboard");
        var member = await GetOwnedMemberAsync(id);
        if (member == null) return NotFound();

        _uow.TeamMembers.Remove(member);
        await _uow.SaveChangesAsync();
        TempData["Success"] = $"{member.FullName} removed.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<TeamMember?> GetOwnedMemberAsync(int id)
    {
        var member = await _uow.TeamMembers.GetByIdAsync(id);
        return member != null && member.AgentUserId == AgentId ? member : null;
    }

    private async Task<int> ResolveSeatLimitAsync()
    {
        // Same resolution the Profile page uses: the ACTIVE billing row is the authority on the
        // agent's current package, with the signup-time PackageId as the fallback.
        var activeBilling = await _uow.Billings.FirstOrDefaultAsync(b =>
            b.AgentUserId == AgentId && b.Status == BillingStatus.Active);
        var agent = await _uow.AgentUsers.GetByIdAsync(AgentId);
        var billingRuleId = activeBilling?.BillingRuleId ?? agent?.PackageId ?? 0;
        if (billingRuleId == 0) return 0;

        var feature = await _uow.PackageFeatures.FirstOrDefaultAsync(f =>
            f.BillingRuleId == billingRuleId && f.FeatureCode == PackageFeatureCodes.TeamMembers);
        return feature is { IsIncluded: true } ? feature.LimitValue ?? 0 : 0;
    }
}
