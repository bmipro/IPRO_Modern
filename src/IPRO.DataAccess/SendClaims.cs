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
// ON ClaimAttempts. It bounds retries, and nothing more -- an earlier version of this comment
// claimed it was also needed to force a non-empty SET, on the theory that MySqlConnector's
// UseAffectedRows defaults to true and a no-op SET would report 0 rows. That was checked against
// the pinned driver and it is FALSE in two independent ways: UseAffectedRows defaults to false (the
// driver requests CLIENT_FOUND_ROWS, so the server reports MATCHED rows and a no-op UPDATE still
// returns 1), and the SET here is never a no-op anyway, because a stale reclaim moves ClaimedAt
// from a value at least ClaimTimeout old to now. The in-repo counter-proof is
// DidYouKnowEmailDispatchJob, which has claimed with a single SetProperty since 2026-08-06.
//
// The increment is kept -- the retry budget is real. But do not reason from the old rationale, and
// do not write a test asserting a reclaim "still reports one affected row": it proves nothing.
//
// It is charged ONLY on the stale-reclaim arm. Charging one for the first Scheduled->Sending
// transition would retire a healthy large send after three ordinary passes.
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

    // Detach any already-tracked copy of a send row.
    //
    // This is not housekeeping, it is the other half of CLAIM FIRST, LOAD SECOND. Both callers of
    // every dispatcher -- the Hangfire job and the agent's own "send now" button -- have already
    // materialised the row into the SAME scoped DbContext before the dispatcher is entered. The
    // claim goes round the change tracker, so that copy still says Scheduled with no claim, and the
    // next SaveChangesAsync in the send loop would write it straight back over the claim.
    //
    // Call this immediately after a successful claim, before reading anything.
    public static void ForgetTracked<TEntity>(IPRODbContext db, int id) where TEntity : class
    {
        foreach (var entry in db.ChangeTracker.Entries<TEntity>().ToList())
        {
            if (entry.Property("Id").CurrentValue is int trackedId && trackedId == id)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

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
            s.ClaimAttempts < MaxAttempts &&
            ((s.Status == NewsLetterSendStatus.Scheduled && s.ScheduledAt <= now) ||
             (s.Status == NewsLetterSendStatus.Sending && s.ClaimedAt != null && s.ClaimedAt < stale)));
    }

    public static IQueryable<ECard> DueECards(IPRODbContext db, DateTime now)
    {
        var stale = now - ClaimTimeout;
        return db.ECards.Where(c =>
            c.ClaimAttempts < MaxAttempts &&
            ((c.Status == ECardStatuses.Scheduled && c.ScheduledAt <= now) ||
             (c.Status == ECardStatuses.Sending && c.ClaimedAt != null && c.ClaimedAt < stale)));
    }

    public static IQueryable<ELetter> DueELetters(IPRODbContext db, DateTime now)
    {
        var stale = now - ClaimTimeout;
        return db.ELetters.Where(l =>
            l.ClaimAttempts < MaxAttempts &&
            ((l.Status == ELetterStatuses.Scheduled && l.ScheduledAt <= now) ||
             (l.Status == ELetterStatuses.Sending && l.ClaimedAt != null && l.ClaimedAt < stale)));
    }

    public static IQueryable<PollSend> DuePollSends(IPRODbContext db, DateTime now)
    {
        var stale = now - ClaimTimeout;
        return db.PollSends.Where(s =>
            s.ClaimAttempts < MaxAttempts &&
            ((s.Status == PollSendStatus.Scheduled && s.ScheduledAt <= now) ||
             (s.Status == PollSendStatus.Sending && s.ClaimedAt != null && s.ClaimedAt < stale)));
    }

    // ---------------------------------------------------------------------------------------
    // The claim. Four bodies, the same shape against their own table. Deliberately not generalised
    // behind reflection or an expression builder -- the whole value of this file is that a reader
    // can see all four predicates agree.
    //
    // Returns the ClaimAttempts value this run now HOLDS, or null if the claim was refused. Callers
    // must keep that number and hand it to Heartbeat* and Release*: it is the ownership token. A run
    // whose claim is later stolen by the sweep will find its number stale, and can stop instead of
    // mailing the same people the new owner is already mailing.
    //
    // TWO ARMS, NOT ONE CLEVER STATEMENT. A first claim on a Scheduled row costs no retry attempt;
    // recovering a claim a dead runner abandoned costs one. Those are different transitions, so they
    // are different UPDATEs.
    //
    // The tempting version is a single UPDATE with a CASE that reads ClaimedAt to decide whether to
    // increment. It does not work, and it fails silently: SendClaimsTests caught a first claim
    // arriving with ClaimAttempts already at 1, because the assignment order EF emits is not the
    // order the increment needs. Rather than depend on how a provider sequences assignments within
    // one statement, each arm writes only what it means.
    //
    // Atomicity is unaffected -- each arm is itself a conditional UPDATE, and the race is settled the
    // same way: two runners both attempt the Scheduled arm, InnoDB serialises them on the row lock,
    // and the loser re-evaluates its WHERE against a row that is no longer Scheduled. The loser then
    // tries the stale arm and fails there too, because the winner's ClaimedAt is fresh.
    //
    // Cost is one extra round trip only on the recovery path, which by definition happens at most
    // MaxAttempts times per send.
    // ---------------------------------------------------------------------------------------

    public static async Task<int?> TryClaimNewsletterSendAsync(IPRODbContext db, int id, DateTime now, CancellationToken ct = default)
    {
        // Arm 1: nobody has run this yet.
        var fresh = await db.NewsLetterSends
            .Where(s => s.Id == id
                     && s.Status == NewsLetterSendStatus.Scheduled
                     && s.ClaimAttempts < MaxAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, NewsLetterSendStatus.Sending)
                .SetProperty(s => s.ClaimedAt, now), ct);
        if (fresh == 1) return await HeldAttemptsAsync(db.NewsLetterSends.Where(s => s.Id == id).Select(s => s.ClaimAttempts), ct);

        // Arm 2: recovering a run that died. This one costs an attempt.
        var stale = now - ClaimTimeout;
        var reclaimed = await db.NewsLetterSends
            .Where(s => s.Id == id
                     && s.Status == NewsLetterSendStatus.Sending
                     && s.ClaimedAt != null && s.ClaimedAt < stale
                     && s.ClaimAttempts < MaxAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.ClaimedAt, now)
                .SetProperty(s => s.ClaimAttempts, s => s.ClaimAttempts + 1), ct);
        if (reclaimed == 1) return await HeldAttemptsAsync(db.NewsLetterSends.Where(s => s.Id == id).Select(s => s.ClaimAttempts), ct);

        return null;
    }

    public static async Task<int?> TryClaimECardAsync(IPRODbContext db, int id, DateTime now, CancellationToken ct = default)
    {
        // Arm 1: nobody has run this yet.
        var fresh = await db.ECards
            .Where(c => c.Id == id
                     && c.Status == ECardStatuses.Scheduled
                     && c.ClaimAttempts < MaxAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, ECardStatuses.Sending)
                .SetProperty(c => c.ClaimedAt, now), ct);
        if (fresh == 1) return await HeldAttemptsAsync(db.ECards.Where(c => c.Id == id).Select(c => c.ClaimAttempts), ct);

        // Arm 2: recovering a run that died. This one costs an attempt.
        var stale = now - ClaimTimeout;
        var reclaimed = await db.ECards
            .Where(c => c.Id == id
                     && c.Status == ECardStatuses.Sending
                     && c.ClaimedAt != null && c.ClaimedAt < stale
                     && c.ClaimAttempts < MaxAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.ClaimedAt, now)
                .SetProperty(c => c.ClaimAttempts, c => c.ClaimAttempts + 1), ct);
        if (reclaimed == 1) return await HeldAttemptsAsync(db.ECards.Where(c => c.Id == id).Select(c => c.ClaimAttempts), ct);

        return null;
    }

    public static async Task<int?> TryClaimELetterAsync(IPRODbContext db, int id, DateTime now, CancellationToken ct = default)
    {
        // Arm 1: nobody has run this yet.
        var fresh = await db.ELetters
            .Where(l => l.Id == id
                     && l.Status == ELetterStatuses.Scheduled
                     && l.ClaimAttempts < MaxAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, ELetterStatuses.Sending)
                .SetProperty(l => l.ClaimedAt, now), ct);
        if (fresh == 1) return await HeldAttemptsAsync(db.ELetters.Where(l => l.Id == id).Select(l => l.ClaimAttempts), ct);

        // Arm 2: recovering a run that died. This one costs an attempt.
        var stale = now - ClaimTimeout;
        var reclaimed = await db.ELetters
            .Where(l => l.Id == id
                     && l.Status == ELetterStatuses.Sending
                     && l.ClaimedAt != null && l.ClaimedAt < stale
                     && l.ClaimAttempts < MaxAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.ClaimedAt, now)
                .SetProperty(l => l.ClaimAttempts, l => l.ClaimAttempts + 1), ct);
        if (reclaimed == 1) return await HeldAttemptsAsync(db.ELetters.Where(l => l.Id == id).Select(l => l.ClaimAttempts), ct);

        return null;
    }

    public static async Task<int?> TryClaimPollSendAsync(IPRODbContext db, int id, DateTime now, CancellationToken ct = default)
    {
        // Arm 1: nobody has run this yet.
        var fresh = await db.PollSends
            .Where(s => s.Id == id
                     && s.Status == PollSendStatus.Scheduled
                     && s.ClaimAttempts < MaxAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, PollSendStatus.Sending)
                .SetProperty(s => s.ClaimedAt, now), ct);
        if (fresh == 1) return await HeldAttemptsAsync(db.PollSends.Where(s => s.Id == id).Select(s => s.ClaimAttempts), ct);

        // Arm 2: recovering a run that died. This one costs an attempt.
        var stale = now - ClaimTimeout;
        var reclaimed = await db.PollSends
            .Where(s => s.Id == id
                     && s.Status == PollSendStatus.Sending
                     && s.ClaimedAt != null && s.ClaimedAt < stale
                     && s.ClaimAttempts < MaxAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.ClaimedAt, now)
                .SetProperty(s => s.ClaimAttempts, s => s.ClaimAttempts + 1), ct);
        if (reclaimed == 1) return await HeldAttemptsAsync(db.PollSends.Where(s => s.Id == id).Select(s => s.ClaimAttempts), ct);

        return null;
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

    // The ownership token the caller must carry. Read back rather than computed, so it is whatever
    // the database actually holds after the claim rather than what this process believes it wrote.
    private static async Task<int?> HeldAttemptsAsync(IQueryable<int> attempts, CancellationToken ct) =>
        await attempts.Select(a => (int?)a).FirstOrDefaultAsync(ct);

    // Time-based, not every-N-recipients. SendGridEmailService creates its client with no timeout
    // override, so one degraded call can run to the HttpClient default of about 100 seconds -- and
    // fifty of those is 83 minutes, five staleness windows. The interval has to be measured in time,
    // precisely because the case where a claim is at risk of going stale is the case where every
    // individual send is slow.
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);   // ClaimTimeout / 3

    // Returns FALSE when this run no longer owns the send -- the sweep handed it to someone else.
    //
    // The Status == Sending guard alone is not enough, and this is the subtle part: a theft does not
    // change Status, the row is Sending either way. Without the ClaimAttempts check a robbed run
    // keeps heartbeating successfully, keeps pushing ClaimedAt forward, and actively conceals the
    // fact that two runners are now mailing the same list. The caller MUST stop its loop on false.
    public static async Task<bool> HeartbeatNewsletterSendAsync(IPRODbContext db, int id, int heldAttempts, DateTime now, CancellationToken ct = default) =>
        await db.NewsLetterSends.Where(s => s.Id == id && s.Status == NewsLetterSendStatus.Sending && s.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClaimedAt, now), ct) == 1;

    public static async Task<bool> HeartbeatECardAsync(IPRODbContext db, int id, int heldAttempts, DateTime now, CancellationToken ct = default) =>
        await db.ECards.Where(c => c.Id == id && c.Status == ECardStatuses.Sending && c.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.ClaimedAt, now), ct) == 1;

    public static async Task<bool> HeartbeatELetterAsync(IPRODbContext db, int id, int heldAttempts, DateTime now, CancellationToken ct = default) =>
        await db.ELetters.Where(l => l.Id == id && l.Status == ELetterStatuses.Sending && l.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.ClaimedAt, now), ct) == 1;

    public static async Task<bool> HeartbeatPollSendAsync(IPRODbContext db, int id, int heldAttempts, DateTime now, CancellationToken ct = default) =>
        await db.PollSends.Where(s => s.Id == id && s.Status == PollSendStatus.Sending && s.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClaimedAt, now), ct) == 1;

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

        // Every UPDATE below repeats the FULL predicate rather than re-filtering on the ids it just
        // read. Between the SELECT and the UPDATE a dispatcher can legitimately re-claim one of
        // these rows; filtering on id alone would then stamp Failed on a send that is at that moment
        // mailing people, and clear the live owner's claim while doing it.

        var newsletters = await db.NewsLetterSends
            .Where(s => s.Status == NewsLetterSendStatus.Sending && s.ClaimedAt != null && s.ClaimedAt < stale && s.ClaimAttempts >= MaxAttempts)
            .Select(s => new { s.Id, s.ClaimAttempts }).ToListAsync(ct);
        foreach (var s in newsletters)
        {
            logger.LogError("Newsletter send {SendId} abandoned after {Attempts} claim attempts; marking Failed. " +
                            "Recipients already mailed keep their Sent status -- read the send's recipient list before resending it.",
                            s.Id, s.ClaimAttempts);
        }
        if (newsletters.Count > 0)
        {
            retired += await db.NewsLetterSends
                .Where(s => s.Status == NewsLetterSendStatus.Sending && s.ClaimedAt != null && s.ClaimedAt < stale && s.ClaimAttempts >= MaxAttempts)
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
                .Where(c => c.Status == ECardStatuses.Sending && c.ClaimedAt != null && c.ClaimedAt < stale && c.ClaimAttempts >= MaxAttempts)
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
                .Where(l => l.Status == ELetterStatuses.Sending && l.ClaimedAt != null && l.ClaimedAt < stale && l.ClaimAttempts >= MaxAttempts)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(l => l.Status, ELetterStatuses.Failed)
                    .SetProperty(l => l.ClaimedAt, (DateTime?)null), ct);
        }

        var polls = await db.PollSends
            .Where(s => s.Status == PollSendStatus.Sending && s.ClaimedAt != null && s.ClaimedAt < stale && s.ClaimAttempts >= MaxAttempts)
            .Select(s => new { s.Id, s.PollSurveyId, s.ClaimAttempts }).ToListAsync(ct);
        foreach (var s in polls)
        {
            logger.LogError("Poll send {PollSendId} abandoned after {Attempts} claim attempts; marking Failed.", s.Id, s.ClaimAttempts);
        }
        if (polls.Count > 0)
        {
            retired += await db.PollSends
                .Where(s => s.Status == PollSendStatus.Sending && s.ClaimedAt != null && s.ClaimedAt < stale && s.ClaimAttempts >= MaxAttempts)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Status, PollSendStatus.Failed)
                    .SetProperty(s => s.ClaimedAt, (DateTime?)null), ct);

            // Unwind the PARENT survey too. PollDispatcher moves PollSurvey.Status to Sending when it
            // starts, and PollsController gates Edit and AddQuestion on Draft -- so retiring only the
            // send would strand the survey on Sending forever and lock the agent out of their own
            // poll with no way back.
            //
            // PollSurveyStatus has no Failed member, and inventing one would be the wrong answer
            // anyway, because the two cases are genuinely different:
            //   - some recipients exist  -> mail went out and people can vote. The survey IS Sent.
            //     The agent stays blocked from editing it, and that is correct, not a lockout.
            //   - no recipients at all   -> it crashed before mailing anyone. Back to Draft, so the
            //     agent can fix whatever was wrong and send it again.
            //
            // Only for surveys with no OTHER live send: a survey can legitimately be sent twice.
            var surveyIds = polls.Select(p => p.PollSurveyId).Distinct().ToList();
            foreach (var surveyId in surveyIds)
            {
                var stillLive = await db.PollSends.AnyAsync(s =>
                    s.PollSurveyId == surveyId &&
                    (s.Status == PollSendStatus.Scheduled || s.Status == PollSendStatus.Sending), ct);
                if (stillLive) continue;

                var anyoneMailed = await db.PollRecipients.AnyAsync(r =>
                    r.PollSurveyId == surveyId && r.SentAt != null, ct);
                var unwound = anyoneMailed ? PollSurveyStatus.Sent : PollSurveyStatus.Draft;

                await db.PollSurveys.Where(v => v.Id == surveyId && v.Status == PollSurveyStatus.Sending)
                    .ExecuteUpdateAsync(u => u.SetProperty(v => v.Status, unwound), ct);

                logger.LogWarning("Poll survey {SurveyId} was left mid-send by a retired dispatch; set to {Status}.",
                    surveyId, unwound);
            }
        }

        return retired;
    }

    // ---------------------------------------------------------------------------------------
    // Release. THE RULE IS: every path that writes a TERMINAL STATUS also clears ClaimedAt. An
    // exception deliberately does NOT -- it leaves the claim set so the sweep can recover the send,
    // bounded by ClaimAttempts. That is the same reasoning DidYouKnowEmailDispatchJob states.
    //
    // An earlier version of this comment said "call it on every terminal path INCLUDING the
    // exception handler", which is precisely wrong and would have been followed: it produces
    // Status = Sending with ClaimedAt = NULL, which matches neither arm of the Due predicates nor
    // RetireExhaustedAsync. The row becomes permanently invisible -- never resumed, never retired,
    // never reported. For polls it drags the parent survey to Sending with it and locks the agent
    // out of editing their own poll. That is a worse failure than the one this file fixes.
    //
    // heldAttempts is the ownership guard. If the sweep handed this send to another runner while we
    // were mailing, our ClaimAttempts is stale and the WHERE matches nothing -- so a robbed runner
    // cannot clear the NEW owner's claim and re-arm the sweep against a live send. Returns whether
    // the release actually applied, so a caller can tell it was robbed.
    //
    // Compared on ClaimAttempts rather than on the ClaimedAt we wrote: .NET ticks are 100ns and
    // MySQL datetime(6) is microseconds, so a round-tripped timestamp never compares equal and the
    // guard would silently no-op every time.
    //
    // Deliberately does NOT touch Status: the caller has just decided what the outcome was, and this
    // must not second-guess it.
    // ---------------------------------------------------------------------------------------

    public static async Task<bool> ReleaseNewsletterSendAsync(IPRODbContext db, int id, int heldAttempts, CancellationToken ct = default) =>
        await db.NewsLetterSends.Where(s => s.Id == id && s.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClaimedAt, (DateTime?)null), ct) == 1;

    public static async Task<bool> ReleaseECardAsync(IPRODbContext db, int id, int heldAttempts, CancellationToken ct = default) =>
        await db.ECards.Where(c => c.Id == id && c.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.ClaimedAt, (DateTime?)null), ct) == 1;

    public static async Task<bool> ReleaseELetterAsync(IPRODbContext db, int id, int heldAttempts, CancellationToken ct = default) =>
        await db.ELetters.Where(l => l.Id == id && l.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.ClaimedAt, (DateTime?)null), ct) == 1;

    public static async Task<bool> ReleasePollSendAsync(IPRODbContext db, int id, int heldAttempts, CancellationToken ct = default) =>
        await db.PollSends.Where(s => s.Id == id && s.ClaimAttempts == heldAttempts)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClaimedAt, (DateTime?)null), ct) == 1;
}
