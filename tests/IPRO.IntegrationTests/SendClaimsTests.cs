using System;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// The atomic claim, against real MySQL.
//
// These assert PERSISTED ROW STATE after each transition, never the affected-row count of a single
// UPDATE. That distinction matters here: an earlier version of SendClaims justified itself with a
// claim about MySqlConnector's UseAffectedRows default that turned out to be backwards, and the test
// the plan originally called for ("a reclaim still reports one affected row") would have passed
// under both the true and the false reading. It proved nothing. What can only be checked against a
// real server is what the row actually looks like afterwards.
//
// One rule is pinned here that no amount of code reading can establish: MySQL evaluates the
// assignments in a single-table UPDATE left to right, which is what lets the claim decide whether to
// charge a retry attempt by reading ClaimedAt in the same statement that overwrites it.
public class SendClaimsTests
{
    [Fact]
    public async Task A_scheduled_send_is_claimed_and_costs_no_attempt()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var send = await SeedNewsletterSendAsync(db, NewsLetterSendStatus.Scheduled, now.AddMinutes(-1));

        var held = await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, now);

        Assert.Equal(0, held);   // a first claim is not a retry
        var row = await ReloadAsync(db, send.Id);
        Assert.Equal(NewsLetterSendStatus.Sending, row.Status);
        Assert.Equal(now, row.ClaimedAt);
        Assert.Equal(0, row.ClaimAttempts);
    }

    // THE RACE. Two runners, same row, no coordination but the database.
    [Fact]
    public async Task Only_one_of_two_racing_claims_wins()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var dbA = testDb.CreateContext();
        await using var dbB = testDb.CreateContext();

        var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var send = await SeedNewsletterSendAsync(dbA, NewsLetterSendStatus.Scheduled, now.AddMinutes(-1));

        // Separate contexts and separate connections -- the same shape as two Hangfire workers, or
        // the scheduler and the "send now" button landing together.
        var first = await SendClaims.TryClaimNewsletterSendAsync(dbA, send.Id, now);
        var second = await SendClaims.TryClaimNewsletterSendAsync(dbB, send.Id, now);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task A_fresh_claim_is_not_stealable_before_the_timeout()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var send = await SeedNewsletterSendAsync(db, NewsLetterSendStatus.Scheduled, now.AddMinutes(-1));
        await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, now);

        // One second short of the window.
        var justBefore = now + SendClaims.ClaimTimeout - TimeSpan.FromSeconds(1);
        Assert.Null(await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, justBefore));
        Assert.Empty(await SendClaims.DueNewsletterSends(db, justBefore).ToListAsync());
    }

    // The sweep and the retry budget in one walk. This is also the test that pins the SET ordering:
    // if ClaimAttempts were written after ClaimedAt, the first claim would already cost an attempt
    // and the counts below would all be one too high.
    [Fact]
    public async Task A_stale_claim_is_swept_and_each_sweep_costs_exactly_one_attempt()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var t0 = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var send = await SeedNewsletterSendAsync(db, NewsLetterSendStatus.Scheduled, t0.AddMinutes(-1));

        Assert.Equal(0, await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, t0));

        var t1 = t0 + SendClaims.ClaimTimeout + TimeSpan.FromMinutes(1);
        Assert.Contains(await SendClaims.DueNewsletterSends(db, t1).Select(s => s.Id).ToListAsync(), id => id == send.Id);
        Assert.Equal(1, await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, t1));

        var t2 = t1 + SendClaims.ClaimTimeout + TimeSpan.FromMinutes(1);
        Assert.Equal(2, await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, t2));

        var t3 = t2 + SendClaims.ClaimTimeout + TimeSpan.FromMinutes(1);
        Assert.Equal(3, await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, t3));

        // Budget spent. The row must now be invisible to BOTH the claim and the due query, or the
        // job spins on it forever, silently, once a minute.
        var t4 = t3 + SendClaims.ClaimTimeout + TimeSpan.FromMinutes(1);
        Assert.Null(await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, t4));
        Assert.Empty(await SendClaims.DueNewsletterSends(db, t4).ToListAsync());
    }

    // A Scheduled row that has burned its budget must not be selected either. The due predicate and
    // the claim used to disagree about this -- Due checked ClaimAttempts only on the Sending arm --
    // which made the job pick the row up every minute and the claim refuse it every minute.
    [Fact]
    public async Task Due_and_TryClaim_never_disagree_about_an_exhausted_row()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var send = await SeedNewsletterSendAsync(db, NewsLetterSendStatus.Scheduled, now.AddMinutes(-1));
        await db.NewsLetterSends.Where(s => s.Id == send.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClaimAttempts, SendClaims.MaxAttempts));

        Assert.Empty(await SendClaims.DueNewsletterSends(db, now).ToListAsync());
        Assert.Null(await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, now));
    }

    // The theft detector. Without the ownership guard the robbed runner keeps heartbeating happily,
    // keeps pushing ClaimedAt forward, and hides the fact that two runners are mailing one list.
    [Fact]
    public async Task A_robbed_run_learns_it_was_robbed_from_the_heartbeat()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var t0 = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var send = await SeedNewsletterSendAsync(db, NewsLetterSendStatus.Scheduled, t0.AddMinutes(-1));

        var runnerA = await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, t0);
        Assert.NotNull(runnerA);
        Assert.True(await SendClaims.HeartbeatNewsletterSendAsync(db, send.Id, runnerA!.Value, t0.AddMinutes(1)));

        // A dies without releasing. The sweep hands the send to B.
        var t1 = t0 + SendClaims.ClaimTimeout + TimeSpan.FromMinutes(2);
        var runnerB = await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, t1);
        Assert.NotNull(runnerB);
        Assert.NotEqual(runnerA.Value, runnerB!.Value);

        // A comes back to life mid-loop. It must be told to stop.
        Assert.False(await SendClaims.HeartbeatNewsletterSendAsync(db, send.Id, runnerA.Value, t1.AddMinutes(1)));
        Assert.True(await SendClaims.HeartbeatNewsletterSendAsync(db, send.Id, runnerB.Value, t1.AddMinutes(1)));

        // ...and it must not be able to release B's claim and re-arm the sweep against a live send.
        Assert.False(await SendClaims.ReleaseNewsletterSendAsync(db, send.Id, runnerA.Value));
        Assert.NotNull((await ReloadAsync(db, send.Id)).ClaimedAt);
    }

    [Fact]
    public async Task Releasing_clears_the_claim_so_a_finished_send_is_never_swept()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var t0 = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var send = await SeedNewsletterSendAsync(db, NewsLetterSendStatus.Scheduled, t0.AddMinutes(-1));
        var held = await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, t0);

        await db.NewsLetterSends.Where(s => s.Id == send.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, NewsLetterSendStatus.Sent));
        Assert.True(await SendClaims.ReleaseNewsletterSendAsync(db, send.Id, held!.Value));

        var muchLater = t0.AddDays(1);
        Assert.Empty(await SendClaims.DueNewsletterSends(db, muchLater).ToListAsync());
        Assert.Null((await ReloadAsync(db, send.Id)).ClaimedAt);
    }

    // The invariant that says the whole mechanism is intact. Sending + ClaimedAt NULL is a row no
    // query can ever see again: not the sweep, not the retirement pass, not a person looking at a
    // screen. It must be unreachable.
    [Fact]
    public async Task Retirement_reports_the_send_and_never_leaves_it_sending_with_no_claim()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var t0 = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var send = await SeedNewsletterSendAsync(db, NewsLetterSendStatus.Scheduled, t0.AddMinutes(-1));

        var t = t0;
        for (var i = 0; i <= SendClaims.MaxAttempts; i++)
        {
            await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, t);
            t = t + SendClaims.ClaimTimeout + TimeSpan.FromMinutes(1);
        }

        var retired = await SendClaims.RetireExhaustedAsync(db, t, NullLogger.Instance);
        Assert.Equal(1, retired);

        var row = await ReloadAsync(db, send.Id);
        Assert.Equal(NewsLetterSendStatus.Failed, row.Status);
        Assert.Null(row.ClaimedAt);

        Assert.Equal(0, await db.NewsLetterSends
            .CountAsync(s => s.Status == NewsLetterSendStatus.Sending && s.ClaimedAt == null));
    }

    // Retirement must not touch a send that a dispatcher legitimately re-claimed in the meantime.
    [Fact]
    public async Task Retirement_leaves_a_freshly_reclaimed_send_alone()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var t0 = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var send = await SeedNewsletterSendAsync(db, NewsLetterSendStatus.Scheduled, t0.AddMinutes(-1));
        await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, t0);

        // Stale by the clock, but only one attempt spent -- a live recovery, not an exhausted one.
        var t1 = t0 + SendClaims.ClaimTimeout + TimeSpan.FromMinutes(1);
        await SendClaims.TryClaimNewsletterSendAsync(db, send.Id, t1);

        Assert.Equal(0, await SendClaims.RetireExhaustedAsync(db, t1.AddSeconds(1), NullLogger.Instance));
        Assert.Equal(NewsLetterSendStatus.Sending, (await ReloadAsync(db, send.Id)).Status);
    }

    // Same walk for the other three senders, so a sender whose claim was wired up differently shows
    // up here rather than in production.
    [Fact]
    public async Task Every_sender_claims_sweeps_and_releases_the_same_way()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var t0 = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var agent = await SeedAgentAsync(db);

        var card = new ECard { AgentUserId = agent.Id, Occasion = "Birthday", Subject = "S", Status = ECardStatuses.Scheduled, ScheduledAt = t0.AddMinutes(-1) };
        var letter = new ELetter { AgentUserId = agent.Id, TemplateKey = "welcome", Subject = "S", Status = ELetterStatuses.Scheduled, ScheduledAt = t0.AddMinutes(-1) };
        var survey = new PollSurvey { AgentUserId = agent.Id, Title = "Q", Subject = "S" };
        db.AddRange(card, letter, survey);
        await db.SaveChangesAsync();

        var poll = new PollSend { PollSurveyId = survey.Id, AgentUserId = agent.Id, Status = PollSendStatus.Scheduled, ScheduledAt = t0.AddMinutes(-1) };
        db.Add(poll);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var t1 = t0 + SendClaims.ClaimTimeout + TimeSpan.FromMinutes(1);

        Assert.Equal(0, await SendClaims.TryClaimECardAsync(db, card.Id, t0));
        Assert.Null(await SendClaims.TryClaimECardAsync(db, card.Id, t0));
        Assert.Equal(1, await SendClaims.TryClaimECardAsync(db, card.Id, t1));
        Assert.True(await SendClaims.ReleaseECardAsync(db, card.Id, 1));

        Assert.Equal(0, await SendClaims.TryClaimELetterAsync(db, letter.Id, t0));
        Assert.Null(await SendClaims.TryClaimELetterAsync(db, letter.Id, t0));
        Assert.Equal(1, await SendClaims.TryClaimELetterAsync(db, letter.Id, t1));
        Assert.True(await SendClaims.ReleaseELetterAsync(db, letter.Id, 1));

        Assert.Equal(0, await SendClaims.TryClaimPollSendAsync(db, poll.Id, t0));
        Assert.Null(await SendClaims.TryClaimPollSendAsync(db, poll.Id, t0));
        Assert.Equal(1, await SendClaims.TryClaimPollSendAsync(db, poll.Id, t1));
        Assert.True(await SendClaims.ReleasePollSendAsync(db, poll.Id, 1));
    }

    // A retired poll send must not strand its survey on Sending -- PollsController gates Edit and
    // AddQuestion on Draft, so that would lock the agent out of their own poll permanently.
    [Fact]
    public async Task Retiring_a_poll_send_that_mailed_nobody_returns_the_survey_to_draft()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var t0 = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var agent = await SeedAgentAsync(db);
        var survey = new PollSurvey { AgentUserId = agent.Id, Title = "Q", Subject = "S", Status = PollSurveyStatus.Sending };
        db.Add(survey);
        await db.SaveChangesAsync();
        var send = new PollSend { PollSurveyId = survey.Id, AgentUserId = agent.Id, Status = PollSendStatus.Scheduled, ScheduledAt = t0.AddMinutes(-1) };
        db.Add(send);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var t = t0;
        for (var i = 0; i <= SendClaims.MaxAttempts; i++)
        {
            await SendClaims.TryClaimPollSendAsync(db, send.Id, t);
            t = t + SendClaims.ClaimTimeout + TimeSpan.FromMinutes(1);
        }

        await SendClaims.RetireExhaustedAsync(db, t, NullLogger.Instance);
        db.ChangeTracker.Clear();

        // Nobody was mailed, so the agent gets their poll back to edit and resend.
        Assert.Equal(PollSurveyStatus.Draft, (await db.PollSurveys.FirstAsync(s => s.Id == survey.Id)).Status);
    }

    private static async Task<NewsLetterSend> ReloadAsync(IPRODbContext db, int id)
    {
        db.ChangeTracker.Clear();
        return await db.NewsLetterSends.FirstAsync(s => s.Id == id);
    }

    private static async Task<AgentUser> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"claim-{Guid.NewGuid():N}"[..20],
            Email = "claim.test@example.com",
            FirstName = "Claim",
            LastName = "Test",
            DomainName = $"claim-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private static async Task<NewsLetterSend> SeedNewsletterSendAsync(IPRODbContext db, NewsLetterSendStatus status, DateTime scheduledAt)
    {
        var agent = await SeedAgentAsync(db);
        var newsletter = new NewsLetter { AgentUserId = agent.Id, Subject = "August", HtmlBody = "<p>x</p>" };
        db.Add(newsletter);
        await db.SaveChangesAsync();

        var send = new NewsLetterSend
        {
            NewsLetterId = newsletter.Id,
            AgentUserId = agent.Id,
            Status = status,
            ScheduledAt = scheduledAt
        };
        db.Add(send);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return send;
    }
}
