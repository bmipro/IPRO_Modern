using IPRO.Entities;

namespace IPRO.Utility;

// 595 (2026-10-22, www.411trades.com): when a domain's www name started waiting for its certificate. Not when the row was
// added: that one was added on 10-21, proven and connected on 10-22 at 14:14 UTC, and My Website said "Taking longer than
// usual ... We've been alerted" within minutes. The wait starts when the name was connected (BoundAt); for a row
// connected before 595, when it was proven, if that was after it was added. My Website's guide, the page's own checks and
// the overdue-certificate alert to IPRO (DomainAutomationJob) all count from here.
public static class CertificateClock
{
    // The latest of the three: a binding recorded before the present proof (a row proven again) does not count.
    public static DateTime StartOf(AgentDomain domain) =>
        new[] { domain.CreatedAt, domain.OwnershipVerifiedAt ?? DateTime.MinValue, domain.BoundAt ?? DateTime.MinValue }.Max();

    // Whether binding the name now is its first binding under its present proof: BoundAt is written once for that, so a
    // binding that drops to Failed and comes back moves neither the clock nor (DomainCheckService) the retry budget.
    public static bool FirstBindingFor(AgentDomain domain) =>
        domain.BoundAt == null || (domain.OwnershipVerifiedAt is { } proven && domain.BoundAt < proven);
}
