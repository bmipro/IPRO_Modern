using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.DataAccess;

// Who owns this send right now, and what happens when nobody does.
//
// THE PROBLEM. Every dispatcher used to do this:
//
//     var send = await GetByIdAsync(id);
//     if (send.Status != Scheduled) return;      // read
//     send.Status = Sending;                     // ...write, later
//     await SaveChangesAsync();
//
// Two runs of the same Hangfire job -- a retry overlapping the original, two instances during a
// deploy, a manual "send now" landing on top of the scheduler -- can both pass that check and both
// mail the entire list. Nothing in the code stopped it; the only thing that ever stopped it was
// timing. That is A5-H6.
//
// The other half is A5-H7/JOBS-3: a process that dies mid-send leaves the row saying Sending
// forever. No job ever selects Sending, so nothing retries it and nothing reports it. It is
// indistinguishable from a send in progress, and stays that way until a person notices the mail
// never arrived.
//
// THE FIX, and why it is one mechanism rather than two: the claim predicate IS the sweep. A row is
// claimable if it is Scheduled, OR if it is Sending with a claim older than ClaimTimeout. The same
// WHERE that wins the race also picks up the abandoned work.
//
// WHY THIS IS CORRECT ON MySQL. ExecuteUpdateAsync emits one UPDATE ... WHERE outside the change
// tracker, in its own autocommit statement. InnoDB takes an exclusive row lock; the second runner's
// UPDATE blocks on it, and when the first commits, the second re-evaluates its WHERE against the
// now-current row, sees Sending with a fresh ClaimedAt, matches nothing, and returns 0. The database
// settles the race -- not application timing, not a distributed lock, not luck.
//
// WHY ClaimAttempts + 1 IS LOAD-BEARING, beyond bounding retries. Pomelo 8.0.2 pins MySqlConnector
// 2.x, where UseAffectedRows defaults to TRUE: the server reports CHANGED rows, not MATCHED rows. A
// conditional UPDATE whose SET happens to write the values already present returns 0 and the claim
// silently fails. That is exactly the stale-reclaim case, where Status is already Sending. Bumping
// an integer guarantees at least one column genuinely changes on every successful claim. Do not
// remove it, and do not "optimise" the SET list.
//
// ORDERING RULE FOR CALLERS: CLAIM FIRST, LOAD SECOND. The UnitOfWork and the DbContext are the same
// scoped instance, so an ExecuteUpdate against a row that has already been materialised leaves a
// stale tracked copy whose old Status will be written back by the next SaveChanges. Claim before any
// GetByIdAsync/FirstOrDefaultAsync on the send row, then load.
//
// The four senders live side by side in this one file on purpose: a missing one is visible on
// screen. Adding a fifth sender means adding its four members here, not inventing a local variant.
public static class SendClaims
{
    // Matches DidYouKnowEmailDispatchJob, which has used this window since the Did You Know queue
    // shipped. Long enough that an ordinary send is never stolen from itself; short enough that a
    // crashed send is retried within the hour.
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(15);

    // Three crashes on the same send is a bad send, not bad luck. After that it is retired to Failed
    // and logged, rather than being re-claimed forever.
    public const int MaxAttempts = 3;

    // ---------------------------------------------------------------------------------------
    // Due predicates. The jobs MUST select through these rather than writing their own Where, so
    // "what the job picked up" and "what the claim will accept" cannot drift apart. A job whose
    // selection is wider than the claim just wastes a round trip; a job whose selection is narrower
    // silently stops sweeping, which is the failure this file exists to end.
    // ---------------------------------------------------------------------------------------

    public static IQueryable<NewsLetterSend> DueNewsletterSends(IPRODbContext db, DateTime now)
    {
        var stale = now - ClaimTimeout;
        return db.NewsLetterSends.Where(s =>
            (s.Status == NewsLetterSendStatus.Scheduled && s.ScheduledAt <= now) ||
            (s.Status == NewsLetterSendStatus.Sending && s.ClaimedAt != null && s.ClaimedAt < stale && s.ClaimAttempts < MaxAttempts));
    }

    public static IQueryable<ECard> DueECards(IPRODbContext db, DateTime now)
    {
        var stale = now - ClaimTimeout;
        return db.ECards.Where(c =>
            (c.Status == ECardStatuses.Scheduled && c.ScheduledAt <= now) ||
            (c.Status == ECardStatuses.Sending && c.ClaimedAt != null && c.ClaimedAt < stale && c.ClaimAttempts < MaxAttempts));
    }

    public static IQueryable<ELetter> DueELetters(IPRODbContext db, DateTime now)
    {
        var stale = now - ClaimTimeout;
        return db.ELetters.Where(l =>
            (l.Status == ELetterStatuses.Scheduled && l.ScheduledAt <= now) ||
            (l.Status == ELetterStatuses.Sending && l.ClaimedAt != null && l.ClaimedAt < stale && l.ClaimAttempts < MaxAttempts));
    }

    public static IQueryable<PollSend> DuePollSends(IPRODbContext db, DateTime now)
    {
        var stale = now - ClaimTimeout;
        return db.PollSends.Where(s =>
            (s.Status == PollSendStatus.Scheduled && s.ScheduledAt <= now) ||
            (s.Status == PollSendStatus.Sending && s.ClaimedAt != null && s.ClaimedAt < stale && s.ClaimAttempts < MaxAttempts));
    }

    // ---------------------------------------------------------------------------------------
    // The claim. Four bodies, identical eight lines against their own table. Deliberately not
    // generalised behind reflection or an expression builder -- the whole value of this file is
    // that a reader can see all four predicates agree.
    // ---------------------------------------------------------------------------------------

    public static async Task<bool> TryClaimNewsletterSendAsync(IPRODbContext db, int id, DateTime now, CancellationToken ct = default)
    {
        var stale = now - ClaimTimeout;
        var claimed = await db.NewsLetterSends
            .Where(s => s.Id == id
                     && s.ClaimAttempts < MaxAttempts
                     && (s.Status == NewsLetterSendStatus.Scheduled
                         || (s.Status == NewsLetterSendStatus.Sending && s.ClaimedAt != null && s.ClaimedAt < stale)))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, NewsLetterSendStatus.Sending)
                .SetProperty(s => s.ClaimedAt, now)
                .SetProperty(s => s.ClaimAttempts, s => s.ClaimAttempts + 1), ct);
        return claimed == 1;
    }

    public static async Task<bool> TryClaimECardAsync(IPRODbContext db, int id, DateTime now, CancellationToken ct = default)
    {
        var stale = now - ClaimTimeout;
        var claimed = await db.ECards
            .Where(c => c.Id == id
                     && c.ClaimAttempts < MaxAttempts
                     && (c.Status == ECardStatuses.Scheduled
                         || (c.Status == ECardStatuses.Sending && c.ClaimedAt != null && c.ClaimedAt < stale)))
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, ECardStatuses.Sending)
                .SetProperty(c => c.ClaimedAt, now)
                .SetProperty(c => c.ClaimAttempts, c => c.ClaimAttempts + 1), ct);
        return claimed == 1;
    }

    public static async Task<bool> TryClaimELetterAsync(IPRODbContext db, int id, DateTime now, CancellationToken ct = default)
    {
        var stale = now - ClaimTimeout;
        var claimed = await db.ELetters
            .Where(l => l.Id == id
                     && l.ClaimAttempts < MaxAttempts
                     && (l.Status == ELetterStatuses.Scheduled
                         || (l.Status == ELetterStatuses.Sending && l.ClaimedAt != null && l.ClaimedAt < stale)))
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, ELetterStatuses.Sending)
                .SetProperty(l => l.ClaimedAt, now)
                .SetProperty(l => l.ClaimAttempts, l => l.ClaimAttempts + 1), ct);
        return claimed == 1;
    }

    public static async Task<bool> TryClaimPollSendAsync(IPRODbContext db, int id, DateTime now, CancellationToken ct = default)
    {
        var stale = now - ClaimTimeout;
        var claimed = await db.PollSends
            .Where(s => s.Id == id
                     && s.ClaimAttempts < MaxAttempts
                     && (s.Status == PollSendStatus.Scheduled
                         || (s.Status == PollSendStatus.Sending && s.ClaimedAt != null && s.ClaimedAt < stale)))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, PollSendStatus.Sending)
                .SetProperty(s => s.ClaimedAt, now)
                .SetProperty(s => s.ClaimAttempts, s => s.ClaimAttempts + 1), ct);
        return claimed == 1;
    }

    // ---------------------------------------------------------------------------------------
    // Heartbeat. A 5,000-recipient newsletter at one SendGrid round-trip each can easily outlive a
    // 15-minute window, and would then be "stale" while actively running -- the sweep would steal it
    // and mail everyone a second time. Pushing ClaimedAt forward every HeartbeatEvery recipients
    // keeps the claim fresh for as long as work is genuinely happening.
    //
    // Guarded on Status == Sending so a heartbeat can never resurrect a send that was cancelled or
    // finished while the loop was in flight.
    // ---------------------------------------------------------------------------------------

    public const int HeartbeatEvery = 50;

    public static Task HeartbeatNewsletterSendAsync(IPRODbContext db, int id, DateTime now, CancellationToken ct = default) =>
        db.NewsLetterSends.Where(s => s.Id == id && s.Status == NewsLetterSendStatus.Sending)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClaimedAt, now), ct);

    public static Task HeartbeatECardAsync(IPRODbContext db, int id, DateTime now, CancellationToken ct = default) =>
        db.ECards.Where(c => c.Id == id && c.Status == ECardStatuses.Sending)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.ClaimedAt, now), ct);

    public static Task HeartbeatELetterAsync(IPRODbContext db, int id, DateTime now, CancellationToken ct = default) =>
        db.ELetters.Where(l => l.Id == id && l.Status == ELetterStatuses.Sending)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.ClaimedAt, now), ct);

    public static Task HeartbeatPollSendAsync(IPRODbContext db, int id, DateTime now, CancellationToken ct = default) =>
        db.PollSends.Where(s => s.Id == id && s.Status == PollSendStatus.Sending)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClaimedAt, now), ct);

    // ---------------------------------------------------------------------------------------
    // Retirement. The "and somebody is told" half of JOBS-3.
    //
    // A send that has been claimed MaxAttempts times and is still stale has crashed the process
    // three times. Re-claiming it forever would be an infinite loop that also blocks the queue, and
    // leaving it Sending is the silent-stuck state this whole mechanism exists to end. So it is
    // flipped to Failed and logged at Error -- one line per send, naming it, so the log says which
    // one and how many attempts rather than just "something is wrong".
    //
    // Runs once per job pass, before the due query, so a retired row is not immediately re-selected.
    // ---------------------------------------------------------------------------------------
    public static async Task<int> RetireExhaustedAsync(IPRODbContext db, DateTime now, ILogger logger, CancellationToken ct = default)
    {
        var stale = now - ClaimTimeout;
        var retired = 0;

        var newsletters = await db.NewsLetterSends
            .Where(s => s.Status == NewsLetterSendStatus.Sending && s.ClaimedAt != null && s.ClaimedAt < stale && s.ClaimAttempts >= MaxAttempts)
            .Select(s => new { s.Id, s.ClaimAttempts }).ToListAsync(ct);
        foreach (var s in newsletters)
        {
            logger.LogError("Newsletter send {SendId} abandoned after {Attempts} claim attempts; marking Failed. " +
                            "Recipients already mailed keep their Sent status -- check the send's recipient list before resending.",
                            s.Id, s.ClaimAttempts);
        }
        if (newsletters.Count > 0)
        {
            retired += await db.NewsLetterSends
                .Where(s => newsletters.Select(n => n.Id).Contains(s.Id))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Status, NewsLetterSendStatus.Failed)
                    .SetProperty(s => s.ClaimedAt, (DateTime?)null), ct);
        }

        var ecards = await db.ECards
            .Where(c => c.Status == ECardStatuses.Sending && c.ClaimedAt != null && c.ClaimedAt < stale && c.ClaimAttempts >= MaxAttempts)
            .Select(c => new { c.Id, c.ClaimAttempts }).ToListAsync(ct);
        foreach (var c in ecards)
        {
            logger.LogError("E-card send {ECardId} abandoned after {Attempts} claim attempts; marking Failed.", c.Id, c.ClaimAttempts);
        }
        if (ecards.Count > 0)
        {
            retired += await db.ECards
                .Where(c => ecards.Select(e => e.Id).Contains(c.Id))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(c => c.Status, ECardStatuses.Failed)
                    .SetProperty(c => c.ClaimedAt, (DateTime?)null), ct);
        }

        var eletters = await db.ELetters
            .Where(l => l.Status == ELetterStatuses.Sending && l.ClaimedAt != null && l.ClaimedAt < stale && l.ClaimAttempts >= MaxAttempts)
            .Select(l => new { l.Id, l.ClaimAttempts }).ToListAsync(ct);
        foreach (var l in eletters)
        {
            logger.LogError("E-letter send {ELetterId} abandoned after {Attempts} claim attempts; marking Failed.", l.Id, l.ClaimAttempts);
        }
        if (eletters.Count > 0)
        {
            retired += await db.ELetters
                .Where(l => eletters.Select(e => e.Id).Contains(l.Id))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(l => l.Status, ELetterStatuses.Failed)
                    .SetProperty(l => l.ClaimedAt, (DateTime?)null), ct);
        }

        var polls = await db.PollSends
            .Where(s => s.Status == PollSendStatus.Sending && s.ClaimedAt != null && s.ClaimedAt < stale && s.ClaimAttempts >= MaxAttempts)
            .Select(s => new { s.Id, s.ClaimAttempts }).ToListAsync(ct);
        foreach (var s in polls)
        {
            logger.LogError("Poll send {PollSendId} abandoned after {Attempts} claim attempts; marking Failed.", s.Id, s.ClaimAttempts);
        }
        if (polls.Count > 0)
        {
            retired += await db.PollSends
                .Where(s => polls.Select(p => p.Id).Contains(s.Id))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Status, PollSendStatus.Failed)
                    .SetProperty(s => s.ClaimedAt, (DateTime?)null), ct);
        }

        return retired;
    }

    // ---------------------------------------------------------------------------------------
    // Release. Called on EVERY terminal path -- success, audience failure, unknown template, and
    // the exception handler. A completed or abandoned row with ClaimedAt still set looks exactly
    // like a live claim to the sweep, which is how a finished send gets mailed twice.
    //
    // Deliberately does NOT touch Status: the caller has just decided what the outcome was, and this
    // must not second-guess it.
    // ---------------------------------------------------------------------------------------

    public static Task ReleaseNewsletterSendAsync(IPRODbContext db, int id, CancellationToken ct = default) =>
        db.NewsLetterSends.Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClaimedAt, (DateTime?)null), ct);

    public static Task ReleaseECardAsync(IPRODbContext db, int id, CancellationToken ct = default) =>
        db.ECards.Where(c => c.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.ClaimedAt, (DateTime?)null), ct);

    public static Task ReleaseELetterAsync(IPRODbContext db, int id, CancellationToken ct = default) =>
        db.ELetters.Where(l => l.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.ClaimedAt, (DateTime?)null), ct);

    public static Task ReleasePollSendAsync(IPRODbContext db, int id, CancellationToken ct = default) =>
        db.PollSends.Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClaimedAt, (DateTime?)null), ct);
}
