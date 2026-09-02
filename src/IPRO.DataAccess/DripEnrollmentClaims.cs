using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// Claims for drip enrollments (TODO 448, 2026-09-02) -- SendClaims' pattern, minus the status
// transition. Newsletter sends and cards flip Scheduled -> Sending to mark ownership; an enrollment
// stays Active for weeks across many steps, and every screen and sweep filters on Active, so a
// transient "Processing" status would leak into all of them. The claim is therefore ClaimedAt alone,
// with the same stale timeout and attempt cap SendClaims uses.
//
// Why there is a claim at all: the hourly DripCampaignJob had no guard against two runs overlapping,
// and "send step 1 immediately" now enqueues a one-off run at enrolment. Both must be able to look
// at the same enrollment and exactly one of them must mail it. A conditional UPDATE is the only
// thing MySQL guarantees is atomic here -- see the long comment in SendClaims for why the
// read-then-write alternative is not.
public static class DripEnrollmentClaims
{
    public static readonly TimeSpan ClaimTimeout = SendClaims.ClaimTimeout;   // 15 minutes
    public const int MaxAttempts = SendClaims.MaxAttempts;                    // 3

    // Rows an hourly run may take: due, active, campaign active, and either unclaimed or stale.
    public static IQueryable<DripCampaignEnrollment> Due(IPRODbContext db, DateTime now)
    {
        var stale = now - ClaimTimeout;
        return db.DripCampaignEnrollments.Where(e =>
            e.Status == DripCampaignEnrollmentStatus.Active
            && e.NextSendAt <= now
            && e.DripCampaign.IsActive
            && e.ClaimAttempts < MaxAttempts
            && (e.ClaimedAt == null || e.ClaimedAt < stale));
    }

    // Returns the ClaimAttempts value this run now HOLDS, or null if the claim was refused (not due,
    // not active, or somebody else owns it). Fresh claim first; a stale one is taken over with the
    // attempt counter bumped, exactly as SendClaims does.
    public static async Task<int?> TryClaimAsync(IPRODbContext db, int id, DateTime now, CancellationToken ct = default)
    {
        var fresh = await db.DripCampaignEnrollments
            .Where(e => e.Id == id
                     && e.Status == DripCampaignEnrollmentStatus.Active
                     && e.NextSendAt <= now
                     && e.ClaimedAt == null
                     && e.ClaimAttempts < MaxAttempts)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.ClaimedAt, now), ct);
        if (fresh == 1) return await HeldAsync(db, id, ct);

        var stale = now - ClaimTimeout;
        var reclaimed = await db.DripCampaignEnrollments
            .Where(e => e.Id == id
                     && e.Status == DripCampaignEnrollmentStatus.Active
                     && e.ClaimedAt != null && e.ClaimedAt < stale
                     && e.ClaimAttempts < MaxAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(e => e.ClaimedAt, now)
                .SetProperty(e => e.ClaimAttempts, e => e.ClaimAttempts + 1), ct);
        if (reclaimed == 1) return await HeldAsync(db, id, ct);

        return null;
    }

    // Drops the claim once an outcome has been PERSISTED (a send, or its failure bookkeeping).
    // Guarded on ClaimAttempts so a run that was robbed by a stale reclaim cannot release the new
    // owner's claim. A clean outcome also resets the attempt counter.
    public static Task<int> ReleaseAsync(IPRODbContext db, int id, int heldAttempts, bool resetAttempts, CancellationToken ct = default)
    {
        var rows = db.DripCampaignEnrollments.Where(e => e.Id == id && e.ClaimAttempts == heldAttempts);
        return resetAttempts
            ? rows.ExecuteUpdateAsync(u => u
                .SetProperty(e => e.ClaimedAt, (DateTime?)null)
                .SetProperty(e => e.ClaimAttempts, 0), ct)
            : rows.ExecuteUpdateAsync(u => u
                .SetProperty(e => e.ClaimedAt, (DateTime?)null), ct);
    }

    // An enrollment whose processing died MaxAttempts times in a row is not "due"; it is broken.
    // Name it so the campaign screen shows it and Resume can restart it, instead of it sitting
    // invisibly excluded from every run forever.
    public static Task<int> FailExhaustedAsync(IPRODbContext db, DateTime now, CancellationToken ct = default)
    {
        var stale = now - ClaimTimeout;
        return db.DripCampaignEnrollments
            .Where(e => e.Status == DripCampaignEnrollmentStatus.Active
                     && e.ClaimAttempts >= MaxAttempts
                     && e.ClaimedAt != null && e.ClaimedAt < stale)
            .ExecuteUpdateAsync(u => u
                .SetProperty(e => e.Status, DripCampaignEnrollmentStatus.Failed)
                .SetProperty(e => e.LastError, "Processing was interrupted 3 times in a row; enrollment stopped. Resume it to try again.")
                .SetProperty(e => e.ClaimedAt, (DateTime?)null), ct);
    }

    private static Task<int> HeldAsync(IPRODbContext db, int id, CancellationToken ct) =>
        db.DripCampaignEnrollments.Where(e => e.Id == id).Select(e => e.ClaimAttempts).SingleAsync(ct);
}
