namespace IPRO.Entities;

// An additional login (secretary, assistant, office staff) working INSIDE one agent's account.
// Deliberately not a second AgentUser: a team member has no package, no website, no billing of
// their own -- they authenticate with their own credentials but act as the owning agent
// (ClaimTypes.NameIdentifier is set to AgentUserId at sign-in, plus a TeamMemberId marker claim
// that the middleware uses to keep Billing and Team management owner-only). Owner decision
// 2026-08-12: access is everything-except-Billing; seats are limited per package tier via the
// team_members package feature.
public class TeamMember
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public AgentUser AgentUser { get; set; } = null!;
}
