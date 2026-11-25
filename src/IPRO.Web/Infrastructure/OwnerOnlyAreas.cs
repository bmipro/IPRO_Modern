using System.Security.Claims;

namespace IPRO.Web.Infrastructure;

// 678 (audit 2026-11-24 A-1, the owner's decision 2026-11-25 "Team members' reach YES"): the parts of the portal a
// team member's session never reaches. Team members (#379) act as their adviser everywhere else. Billing (money) and
// Team (staffing) were owner-only since 2026-08-12; the areas built after that decision were not, so an assistant
// could put their own e-Transfer email or PayPal.me name on every invoice the adviser had sent, connect their own
// Stripe or Google account, point the adviser's iPro address somewhere else, or move the adviser's domains.
//
// One list, read by the middleware in Program.cs (before any controller runs) and by each controller again, the way
// TeamController always did.
public static class OwnerOnlyAreas
{
    public const string Message = "Only the account owner can change this.";

    public static bool IsTeamMember(ClaimsPrincipal user) => user.FindFirst("TeamMemberId") != null;

    // Whole controllers: everything under these paths.
    private static readonly string[] Prefixes =
    {
        "/Billing",
        "/Team",
        "/Payments",
        "/GoogleCalendar",
    };

    // Single actions of a controller a team member otherwise uses (My Website).
    private static readonly string[] Actions =
    {
        "/Website/KeepOwnWebsite",
        "/Website/SaveOwnWebsite",
        "/Website/UseIproWebsite",
        "/Website/WebsiteSetupFeeReturn",
        "/Website/WebsiteSetupFeeCancel",
        "/Website/Domains",
        "/Website/DomainDisplay",
        "/Website/AddDomain",
        "/Website/RemoveDomain",
        "/Website/RetryDomain",
        "/Website/DomainGuide",
        "/Website/DomainHelp",
        "/Website/SetPrimaryDomain",
    };

    public static bool IsOwnerOnly(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var prefix in Prefixes)
        {
            if (Matches(path, prefix)) return true;
        }
        foreach (var action in Actions)
        {
            if (Matches(path, action)) return true;
        }
        return false;
    }

    // "/Payments" and "/Payments/Save" match "/Payments"; "/PaymentsReport" does not.
    private static bool Matches(string path, string prefix) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.Length > prefix.Length && path[prefix.Length] == '/');
}
