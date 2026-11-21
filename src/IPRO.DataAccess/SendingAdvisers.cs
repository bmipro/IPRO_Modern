using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// 641 (the fresh low-severity sweep, 2026-11-20): the advisers whose scheduled mail may go out now. Before, a suspended
// adviser (SuperAdmin -> Suspend, which says "sign-in blocked") or one whose trial had lapsed unpaid ("everything else
// is paused") kept sending scheduled newsletters, e-cards, e-letters, polls, campaign steps and Did You Know emails
// under their business name, and could not sign in to stop them. The rule is the portal's own billing lock
// (PackageEntitlementService.IsAccessGatedAsync: an Active billing, a cancelled or expired one still paid through, or
// a trial inside its grace days) plus IsActive. Mail held this way is not cancelled: it stays scheduled and goes out
// if the adviser is reinstated or subscribes. It is a query, not a list, so each job filters inside its own SQL and a
// held adviser's backlog never crowds out everyone else's (DripCampaignJob takes 100 at a time).
public static class SendingAdvisers
{
    public static async Task<IQueryable<int>> WhoMaySendAsync(IPRODbContext db, DateTime now, CancellationToken ct = default)
    {
        var graceDays = await db.TrialSettings.AsNoTracking().Where(t => t.Id == 1).Select(t => (int?)t.GracePeriodDays).FirstOrDefaultAsync(ct) ?? 1;
        var trialStillOpenFrom = now.AddDays(-graceDays);
        return db.AgentUsers
            .Where(a => a.IsActive && (
                db.Billings.Any(b => b.AgentUserId == a.Id && b.Status == BillingStatus.Active) ||
                db.Billings.Any(b => b.AgentUserId == a.Id
                                     && (b.Status == BillingStatus.Cancelled || b.Status == BillingStatus.Expired)
                                     && b.PaidThroughAt != null && b.PaidThroughAt > now) ||
                (a.TrialEndsAt != null && a.TrialEndsAt >= trialStillOpenFrom)))
            .Select(a => a.Id);
    }
}
