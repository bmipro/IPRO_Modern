using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// The scenario the whole claim mechanism exists for, driven end to end through the REAL dispatcher
// against real MySQL, with a fake mail transport that records who was addressed:
//
//     claim -> mail some of the list -> the process dies -> 15 minutes pass -> the sweep re-claims
//
// The only outcome that matters is that NOBODY receives the email twice. Everything else in the
// retrofit -- the per-iteration save, the Queued-only filter, the derived counts -- exists to make
// that true, so this is the test that would catch any of them being undone.
//
// It uses e-cards because that path is the simplest of the four; the resume rule is the same in all
// of them, and the three sibling dispatchers are covered by the shape assertions in SendClaimsTests.
public class DispatcherResumeTests
{
    [Fact]
    public async Task A_resumed_send_never_mails_anyone_twice()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedCardAsync(db, recipientCount: 10);
        var alreadyMailed = await SimulateCrashAfterAsync(db, seed.CardId, mailed: 4);

        // The sweep re-claims it. From here on this is the real dispatcher against the real row.
        var transport = new RecordingEmailService();
        await NewDispatcher(db, transport).DispatchAsync(seed.CardId);

        // THE ASSERTION. Only the six who had not been reached.
        Assert.Equal(6, transport.Sent.Count);
        Assert.Empty(alreadyMailed.Intersect(transport.Sent));

        var everyone = alreadyMailed.Concat(transport.Sent).ToList();
        Assert.Equal(10, everyone.Count);
        Assert.Equal(10, everyone.Distinct().Count());

        db.ChangeTracker.Clear();
        var card = await db.ECards.AsNoTracking().FirstAsync(c => c.Id == seed.CardId);
        Assert.Equal(ECardStatuses.Sent, card.Status);

        // Counted from the recipient rows, not from the resuming run's local counter -- otherwise
        // this would read 6 for a card that reached 10 people.
        Assert.Equal(10, card.TotalSent);

        // The claim was consumed by the reclaim, and released on completion. A finished send must
        // never look like a live one.
        Assert.Equal(1, card.ClaimAttempts);
        Assert.Null(card.ClaimedAt);
        Assert.Equal(0, await db.ECards.CountAsync(c => c.Status == ECardStatuses.Sending && c.ClaimedAt == null));
    }

    // Consent is re-read at send time, so somebody who unsubscribes between the crash and the resume
    // is not mailed by the resume. Without this the resume path would quietly reopen the hole the
    // consent work just closed.
    [Fact]
    public async Task Someone_who_unsubscribes_between_the_crash_and_the_resume_is_not_mailed()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedCardAsync(db, recipientCount: 6);
        await SimulateCrashAfterAsync(db, seed.CardId, mailed: 2);

        var untouched = await db.ECardRecipients
            .Where(r => r.ECardId == seed.CardId && r.Status == ECardRecipientStatuses.Queued)
            .OrderBy(r => r.Id)
            .FirstAsync();
        var quitter = await db.Clients.FirstAsync(c => c.Id == untouched.ClientId);
        var quitterEmail = quitter.Email;
        await NewConsent(db).SuppressAllAsync(quitter, "test");
        db.ChangeTracker.Clear();

        var transport = new RecordingEmailService();
        await NewDispatcher(db, transport).DispatchAsync(seed.CardId);

        Assert.DoesNotContain(quitterEmail, transport.Sent);
        Assert.Equal(3, transport.Sent.Count);   // 6 total, 2 already mailed, 1 unsubscribed since
    }

    // A card whose every remaining recipient fails must not be reported Sent -- but nor may it be
    // reported Failed when an earlier pass already delivered mail. The status comes from the whole
    // recipient list, so a bad tail cannot erase a good head.
    [Fact]
    public async Task A_resume_whose_recipients_all_fail_does_not_erase_the_earlier_successes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedCardAsync(db, recipientCount: 5);
        await SimulateCrashAfterAsync(db, seed.CardId, mailed: 3);

        var transport = new RecordingEmailService { FailEverySend = true };
        await NewDispatcher(db, transport).DispatchAsync(seed.CardId);

        db.ChangeTracker.Clear();
        var card = await db.ECards.AsNoTracking().FirstAsync(c => c.Id == seed.CardId);

        // The old code wrote this run's counter: TotalSent = 0 and Status = Failed, erasing three
        // delivered emails from the record.
        Assert.Equal(ECardStatuses.Sent, card.Status);
        Assert.Equal(3, card.TotalSent);
    }

    // Reproduces the database state a process death leaves behind: the first `mailed` recipients
    // recorded Sent, the rest still Queued, and the card Sending with a claim old enough for the
    // sweep. That state is only reachable because the dispatcher saves after EVERY recipient -- with
    // the original single trailing save, all ten rows would still be Queued and the resume would
    // mail the entire list a second time.
    private static async Task<List<string>> SimulateCrashAfterAsync(IPRODbContext db, int cardId, int mailed)
    {
        db.ChangeTracker.Clear();
        var recipients = await db.ECardRecipients
            .Where(r => r.ECardId == cardId).OrderBy(r => r.Id).ToListAsync();

        var sent = new List<string>();
        foreach (var recipient in recipients.Take(mailed))
        {
            recipient.Status = ECardRecipientStatuses.Sent;
            recipient.SentAt = DateTime.UtcNow;
            sent.Add(recipient.Email);
        }
        await db.SaveChangesAsync();

        // Hoisted: EF inlines TimeSpan arithmetic written inside the lambda as a SQL literal and
        // mis-formats it. A local makes it a parameter.
        var staleClaim = DateTime.UtcNow - SendClaims.ClaimTimeout - TimeSpan.FromMinutes(1);
        await db.ECards.Where(c => c.Id == cardId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, ECardStatuses.Sending)
                .SetProperty(c => c.ClaimedAt, staleClaim));

        db.ChangeTracker.Clear();
        return sent;
    }

    private static EmailConsentService NewConsent(IPRODbContext db) =>
        new(db, new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());

    private static ECardDispatcher NewDispatcher(IPRODbContext db, IEmailService email) =>
        new(db, email, NewConsent(db),
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<ECardDispatcher>.Instance);

    // NOT COVERED HERE: a run whose claim is stolen mid-send. The heartbeat is time-based, so
    // provoking it would mean either a five-minute test or a test-only hook in production code.
    // SendClaimsTests covers the mechanism -- the heartbeat returns false to a stale holder -- and
    // the dispatcher's reaction to that is a logged early return.

    private sealed record Seed(int AgentId, int CardId);

    private static async Task<Seed> SeedCardAsync(IPRODbContext db, int recipientCount)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"resume-{Guid.NewGuid():N}"[..20],
            Email = "resume.agent@example.com",
            FirstName = "Resume",
            LastName = "Test",
            DomainName = $"resume-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        db.Add(new ECardDesign
        {
            Key = "birthday-test",
            Name = "Birthday",
            Occasion = "Birthday",
            IsActive = true
        });

        var card = new ECard
        {
            AgentUserId = agent.Id,
            Occasion = "birthday-test",
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
                FirstName = $"Client{i}",
                LastName = "Recipient",
                Email = $"client{i}.{Guid.NewGuid():N}"[..24] + "@example.com",
                IsNewsletterSubscribed = true
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();

            db.Add(new ECardRecipient
            {
                ECardId = card.Id,
                ClientId = client.Id,
                Email = client.Email,
                RecipientName = $"{client.FirstName} {client.LastName}",
                Status = ECardRecipientStatuses.Queued
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new Seed(agent.Id, card.Id);
    }

    // Records every address it was asked to mail, and can be told to die partway through.
    private sealed class RecordingEmailService : IEmailService
    {
        public List<string> Sent { get; } = new();

        // Makes every send report failure, without throwing -- the shape of a SendGrid rejection.
        public bool FailEverySend { get; set; }

        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody,
            string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null,
            string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            if (FailEverySend) return Task.FromResult(EmailSendResult.Failed("Simulated provider rejection."));

            Sent.Add(toEmail);
            return Task.FromResult(EmailSendResult.Sent($"msg-{Sent.Count}"));
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
}
