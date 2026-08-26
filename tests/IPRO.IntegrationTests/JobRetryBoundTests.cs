using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Scheduler;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// H13 / H14 (billing wave 2026-08-25): the two email jobs whose retry loops were unbounded or
// self-destructive. Each test here failed against the pre-fix code.
public class JobRetryBoundTests
{
    // ------------------------------------------------------------------- H13: drip campaigns --

    [Fact]
    public void H13_last_error_never_exceeds_the_columns_1000_chars()
    {
        // ex.Message is unbounded; LastError is varchar(1000). Pre-fix the overlong write made
        // the CATCH's own SaveChangesAsync throw "Data too long" -- job aborts, Hangfire replays
        // the batch, same row first, forever.
        var enrollment = new DripCampaignEnrollment { SendAttempts = 0 };
        DripCampaignJob.HandleSendFailure(enrollment, transient: true, new string('x', 5000));
        Assert.True(enrollment.LastError.Length <= 1000, $"LastError is {enrollment.LastError.Length} chars");

        // The give-up message is composed THEN truncated -- composing after truncation would
        // overflow again by exactly the length of the prefix.
        var exhausted = new DripCampaignEnrollment { SendAttempts = 4 };
        DripCampaignJob.HandleSendFailure(exhausted, transient: true, new string('y', 5000));
        Assert.Equal(DripCampaignEnrollmentStatus.Failed, exhausted.Status);
        Assert.True(exhausted.LastError.Length <= 1000, $"LastError is {exhausted.LastError.Length} chars");
    }

    [Fact]
    public void H13_a_transient_failure_backs_off_instead_of_hogging_the_batch_head()
    {
        // The batch is ordered by NextSendAt; pre-fix a failing row stayed due and sat at
        // position 1 of every hourly run until its cap engaged.
        var enrollment = new DripCampaignEnrollment
        {
            SendAttempts = 0,
            NextSendAt = DateTime.UtcNow.AddMinutes(-30)
        };
        DripCampaignJob.HandleSendFailure(enrollment, transient: true, "timeout");

        Assert.Equal(DripCampaignEnrollmentStatus.Active, enrollment.Status);
        Assert.True(enrollment.NextSendAt > DateTime.UtcNow.AddMinutes(30),
            $"NextSendAt {enrollment.NextSendAt:O} must move behind healthy rows");
    }

    // --------------------------------------------------------------------- H14: Did You Know --

    [Fact]
    public async Task H14_transient_failures_retire_the_item_after_the_cap_instead_of_retrying_forever()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var agent = new AgentUser
        {
            UserName = $"dyk-{Guid.NewGuid():N}"[..20],
            Email = $"dyk-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Dyk", LastName = "Agent",
            DomainName = $"dyk-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario"
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var article = new Article
        {
            AgentUserId = agent.Id,
            Title = "H14 Article",
            Content = "<p>content</p>",
            IsPublished = true
        };
        db.Add(article);
        var client = new Client
        {
            AgentUserId = agent.Id,
            FirstName = "H14", LastName = "Client",
            Email = $"h14-{Guid.NewGuid():N}"[..12] + "@example.test"
        };
        db.Add(client);
        await db.SaveChangesAsync();
        var item = new DidYouKnowEmailQueueItem
        {
            ArticleId = article.Id,
            ClientId = client.Id,
            ScheduledForUtc = DateTime.UtcNow.AddMinutes(-5)
        };
        db.Add(item);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var job = new DidYouKnowEmailDispatchJob(
            db, new TransientFailEmailService(), new StubConsent(), NullLogger<DidYouKnowEmailDispatchJob>.Instance);

        // Five rounds; between rounds, age the claim past the 15-minute stale window exactly the
        // way production time does. Pre-fix there was no counter: round 5 looked like round 1 and
        // round 500 would have too -- the item cycled every 15 minutes indefinitely.
        for (var round = 1; round <= 5; round++)
        {
            await job.RunAsync();
            var mid = await db.DidYouKnowEmailQueueItems.AsNoTracking().SingleAsync(q => q.Id == item.Id);
            if (round < 5)
            {
                Assert.Null(mid.SentAtUtc);   // still recoverable
                await db.DidYouKnowEmailQueueItems
                    .Where(q => q.Id == item.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(q => q.ClaimedAtUtc, DateTime.UtcNow.AddMinutes(-16)));
            }
        }

        var final = await db.DidYouKnowEmailQueueItems.AsNoTracking().SingleAsync(q => q.Id == item.Id);
        Assert.Equal(5, final.SendAttempts);
        Assert.Equal(DidYouKnowQueueStatuses.Failed, final.Status);
        Assert.NotNull(final.SentAtUtc);      // retired: the dispatch query never selects it again
        Assert.Contains("Gave up after 5 attempts", final.FailureReason);
    }

    private sealed class TransientFailEmailService : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(false);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null)
            => Task.FromResult(IPRO.Email.EmailSendResult.FailedTransient("simulated 429"));
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
    }

    private sealed class StubConsent : IPRO.Business.Services.IEmailConsentService
    {
        public bool IsSuppressed(Client client, IPRO.Business.Services.EmailChannel channel, bool designSurvivesOptOut = false) => false;
        public Task<IPRO.Business.Services.SuppressionResult> SuppressAllAsync(Client client, string source) => throw new NotSupportedException();
        public Task ResubscribeAsync(Client client) => throw new NotSupportedException();
        public Task<int> CancelSuppressedDripEnrollmentsAsync(int batchLimit = 500) => Task.FromResult(0);
        public Task<string> GetOrCreateTokenAsync(Client client) => Task.FromResult("tok");
        public string BuildPreferencesUrl(string token) => $"https://example.test/prefs/{token}";
    }
}
