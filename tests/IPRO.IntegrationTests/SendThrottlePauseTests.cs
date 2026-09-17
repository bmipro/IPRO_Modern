using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 491 (2026-09-16), the other half. When the provider answers "not right now" -- a throttle (429),
// a 5xx, a timeout -- the recipient in hand is not at fault and neither is the send. Before this the
// four blast loops wrote Failed on that recipient and kept going, so a newsletter that hit the
// sending cap failed everyone past it, permanently. Now the row stays Queued, the pass ends, and the
// send goes back to Scheduled with its claim released; the minutely job claims it again as a FRESH
// claim (no attempt spent) and resumes exactly the Queued rows. E-cards and newsletters are driven
// here against the real database; the two sibling loops are pinned by shape in EmailSendGateTests.
public class SendThrottlePauseTests
{
    [Fact]
    public async Task A_throttled_card_pauses_with_the_rest_still_queued_and_finishes_on_the_next_run()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var cardId = await SeedCardAsync(db, recipientCount: 6);

        // Two go, then the provider starts answering 429.
        var throttled = new ThrottlingEmailService { SucceedFirst = 2 };
        await NewCardDispatcher(db, throttled).DispatchAsync(cardId);

        db.ChangeTracker.Clear();
        var rows = await db.ECardRecipients.AsNoTracking().Where(r => r.ECardId == cardId).ToListAsync();
        Assert.Equal(2, rows.Count(r => r.Status == ECardRecipientStatuses.Sent));
        Assert.Equal(4, rows.Count(r => r.Status == ECardRecipientStatuses.Queued));
        Assert.Equal(0, rows.Count(r => r.Status == ECardRecipientStatuses.Failed));

        // Handed back to the schedule, claim released, no attempt spent.
        var card = await db.ECards.AsNoTracking().SingleAsync(c => c.Id == cardId);
        Assert.Equal(ECardStatuses.Scheduled, card.Status);
        Assert.Null(card.ClaimedAt);
        Assert.Equal(0, card.ClaimAttempts);
        // 493: a paused send carries its running total, so the activity screen reads "In progress, 2 sent"
        // rather than "Scheduled, 0 sent" -- which an adviser reads as "it never went out".
        Assert.Equal(2, card.TotalSent);
        // ...and the job's own due query would pick it up again right now.
        Assert.Contains(cardId, await SendClaims.DueECards(db, DateTime.UtcNow).Select(c => c.Id).ToListAsync());

        // The next run, with the provider behaving: the four Queued rows go, nobody twice.
        var working = new ThrottlingEmailService { SucceedFirst = int.MaxValue };
        await NewCardDispatcher(db, working).DispatchAsync(cardId);

        db.ChangeTracker.Clear();
        rows = await db.ECardRecipients.AsNoTracking().Where(r => r.ECardId == cardId).ToListAsync();
        Assert.Equal(6, rows.Count(r => r.Status == ECardRecipientStatuses.Sent));
        Assert.Equal(4, working.Sent.Count);
        Assert.Empty(throttled.Sent.Intersect(working.Sent));
        card = await db.ECards.AsNoTracking().SingleAsync(c => c.Id == cardId);
        Assert.Equal(ECardStatuses.Sent, card.Status);
        Assert.Equal(6, card.TotalSent);
        Assert.Null(card.ClaimedAt);
    }

    [Fact]
    public async Task A_throttled_newsletter_pauses_the_same_way()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var sendId = await SeedNewsletterSendAsync(db, subscriberCount: 5);

        var throttled = new ThrottlingEmailService { SucceedFirst = 3 };
        await NewNewsletterDispatcher(db, throttled).DispatchSendAsync(sendId);

        db.ChangeTracker.Clear();
        var rows = await db.NewsLetterRecipients.AsNoTracking().Where(r => r.NewsLetterSendId == sendId).ToListAsync();
        Assert.Equal(5, rows.Count);
        Assert.Equal(3, rows.Count(r => r.Status == NewsLetterRecipientStatus.Sent));
        Assert.Equal(2, rows.Count(r => r.Status == NewsLetterRecipientStatus.Queued));
        Assert.Equal(0, rows.Count(r => r.Status == NewsLetterRecipientStatus.Failed));

        var send = await db.NewsLetterSends.AsNoTracking().SingleAsync(s => s.Id == sendId);
        Assert.Equal(NewsLetterSendStatus.Scheduled, send.Status);
        Assert.Null(send.ClaimedAt);
        Assert.Equal(0, send.ClaimAttempts);
        Assert.Equal(3, send.TotalSent);   // 493

        var working = new ThrottlingEmailService { SucceedFirst = int.MaxValue };
        await NewNewsletterDispatcher(db, working).DispatchSendAsync(sendId);

        db.ChangeTracker.Clear();
        rows = await db.NewsLetterRecipients.AsNoTracking().Where(r => r.NewsLetterSendId == sendId).ToListAsync();
        Assert.Equal(5, rows.Count(r => r.Status == NewsLetterRecipientStatus.Sent));
        Assert.Equal(2, working.Sent.Count);
        send = await db.NewsLetterSends.AsNoTracking().SingleAsync(s => s.Id == sendId);
        Assert.Equal(NewsLetterSendStatus.Sent, send.Status);
        Assert.Equal(5, send.TotalSent);
    }

    [Fact]
    public async Task A_permanent_rejection_still_fails_that_recipient_and_the_run_carries_on()
    {
        // The other kind of "no" is unchanged: a bad address is that recipient's problem, the rest of
        // the list is still mailed, and the send finishes.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var cardId = await SeedCardAsync(db, recipientCount: 4);

        var rejecting = new ThrottlingEmailService { SucceedFirst = int.MaxValue, RejectPermanentlyAt = 2 };
        await NewCardDispatcher(db, rejecting).DispatchAsync(cardId);

        db.ChangeTracker.Clear();
        var rows = await db.ECardRecipients.AsNoTracking().Where(r => r.ECardId == cardId).ToListAsync();
        Assert.Equal(3, rows.Count(r => r.Status == ECardRecipientStatuses.Sent));
        Assert.Equal(1, rows.Count(r => r.Status == ECardRecipientStatuses.Failed));
        var card = await db.ECards.AsNoTracking().SingleAsync(c => c.Id == cardId);
        Assert.Equal(ECardStatuses.Sent, card.Status);
        Assert.Null(card.ClaimedAt);
    }

    // ---- harness ------------------------------------------------------------------------------

    // Sends succeed for the first N calls, then every call is a throttle (transient). Optionally one
    // call (1-based index) is a permanent rejection instead.
    private sealed class ThrottlingEmailService : IEmailService
    {
        public int SucceedFirst { get; set; }
        public int RejectPermanentlyAt { get; set; } = -1;
        public List<string> Sent { get; } = new();
        private int _calls;

        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody,
            string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null,
            string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            _calls++;
            if (_calls == RejectPermanentlyAt) return Task.FromResult(EmailSendResult.Failed("Azure email rejected the send. Status: 400. Invalid recipient."));
            if (_calls > SucceedFirst) return Task.FromResult(EmailSendResult.FailedTransient("Azure email rejected the send. Status: 429. Too many requests."));
            Sent.Add(toEmail);
            return Task.FromResult(EmailSendResult.Sent($"msg-{_calls}"));
        }

        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody,
            string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null,
            string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;

        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) =>
            throw new NotSupportedException();

        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) =>
            throw new NotSupportedException();
    }

    private static EmailConsentService NewConsent(IPRODbContext db) =>
        new(db, new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());

    private static ECardDispatcher NewCardDispatcher(IPRODbContext db, IEmailService email) =>
        new(db, email, NewConsent(db), new ConfigurationBuilder().AddInMemoryCollection().Build(), NullLogger<ECardDispatcher>.Instance);

    private static NewsLetterDispatcher NewNewsletterDispatcher(IPRODbContext db, IEmailService email) =>
        new(new UnitOfWork(db), db, email, new ConfigurationBuilder().AddInMemoryCollection().Build(), NullLogger<NewsLetterDispatcher>.Instance);

    private static async Task<AgentUser> SeedAgentAsync(IPRODbContext db, string prefix)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"{prefix}-{Guid.NewGuid():N}"[..20],
            Email = $"{prefix}-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Throttle", LastName = "Test",
            DomainName = $"{prefix}-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario",
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private static async Task<int> SeedCardAsync(IPRODbContext db, int recipientCount)
    {
        var agent = await SeedAgentAsync(db, "thr-card");
        var designKey = $"bday-{Guid.NewGuid():N}"[..16];
        db.Add(new ECardDesign { Key = designKey, Name = "Birthday", Occasion = "Birthday", IsActive = true });
        var card = new ECard
        {
            AgentUserId = agent.Id,
            Occasion = designKey,
            Subject = "Happy birthday",
            Message = "Many happy returns.",
            Status = ECardStatuses.Scheduled,
            ScheduledAt = DateTime.UtcNow.AddMinutes(-1)
        };
        db.Add(card);
        await db.SaveChangesAsync();

        for (var i = 0; i < recipientCount; i++)
        {
            var client = new Client
            {
                AgentUserId = agent.Id,
                FirstName = $"Client{i}", LastName = "Recipient",
                Email = $"c{i}.{Guid.NewGuid():N}"[..20] + "@example.test",
                IsNewsletterSubscribed = true
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            db.Add(new ECardRecipient
            {
                ECardId = card.Id, ClientId = client.Id, Email = client.Email,
                RecipientName = $"{client.FirstName} {client.LastName}", Status = ECardRecipientStatuses.Queued
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return card.Id;
    }

    private static async Task<int> SeedNewsletterSendAsync(IPRODbContext db, int subscriberCount)
    {
        var agent = await SeedAgentAsync(db, "thr-news");
        for (var i = 0; i < subscriberCount; i++)
        {
            db.Clients.Add(new Client
            {
                AgentUserId = agent.Id,
                FirstName = $"Reader{i}", LastName = "Subscriber",
                Email = $"r{i}.{Guid.NewGuid():N}"[..20] + "@example.test",
                IsNewsletterSubscribed = true
            });
        }
        await db.SaveChangesAsync();

        var newsletter = new NewsLetter { AgentUserId = agent.Id, Subject = "September", HtmlBody = "<p>Hello</p>" };
        db.NewsLetters.Add(newsletter);
        await db.SaveChangesAsync();
        var send = new NewsLetterSend
        {
            NewsLetterId = newsletter.Id,
            AgentUserId = agent.Id,
            AudienceType = NewsLetterAudienceType.AllSubscribers,
            Status = NewsLetterSendStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow.AddMinutes(-1)
        };
        db.NewsLetterSends.Add(send);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return send.Id;
    }
}
