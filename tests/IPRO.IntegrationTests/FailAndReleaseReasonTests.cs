using System;
using System.Collections.Generic;
using System.IO;
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

// TODO 441 (2026-08-31). A send that cannot proceed at all -- the agent record is gone, the card
// references a design that no longer exists -- goes through FailAndReleaseAsync, which stamped the
// parent Failed, cleared the claim, and wrote the REASON only to the application log. The
// recipient rows were never touched. On Email Activity that produced four contradictory signals at
// once: card Failed, recipient still Queued, Issue column blank (FailureReason empty), Failed count
// 0 (no row had Status=Failed). The owner spent real time on a 4:29pm card that could not be
// explained from anything the portal showed; the only copy of the reason was the log stream.
//
// The two per-recipient paths already did this right: the consent check and the per-recipient
// catch both set Status=Failed WITH a FailureReason. FailAndReleaseAsync now fans the reason out to
// every still-Queued recipient row the same way, so the count, the pill and the Issue column all
// derive from the same rows and agree. No schema change -- FailureReason already exists on all
// four recipient tables.
public class FailAndReleaseReasonTests
{
    [Fact]
    public async Task A_card_that_cannot_send_tells_every_recipient_row_why()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var cardId = await SeedCardWithNoDesignAsync(db, recipientCount: 2);
        var transport = new RecordingEmailService();

        await NewDispatcher(db, transport).DispatchAsync(cardId);
        db.ChangeTracker.Clear();

        // Nothing was mailed: the failure is before the recipient loop.
        Assert.Empty(transport.Sent);

        var card = await db.ECards.SingleAsync(c => c.Id == cardId);
        Assert.Equal(ECardStatuses.Failed, card.Status);
        Assert.Null(card.ClaimedAt);

        var recipients = await db.ECardRecipients.Where(r => r.ECardId == cardId).ToListAsync();
        Assert.Equal(2, recipients.Count);
        Assert.All(recipients, r =>
        {
            // Terminal, not Queued: a Queued row under a Failed parent is the contradiction.
            Assert.Equal(ECardRecipientStatuses.Failed, r.Status);
            // And the reason is on the row, where Email Activity's Issue column reads it.
            Assert.Contains("no-such-design", r.FailureReason);
        });
    }

    // ---- the three siblings share the shape, so they share the fix ----------------------------
    //
    // The e-card path is driven end to end above; ELetter, Poll and Newsletter have the identical
    // FailAndReleaseAsync and are pinned by shape, the same division DispatcherResumeTests uses.

    public static readonly (string File, string RecipientSet)[] Siblings =
    {
        (@"src\IPRO.Email\ECardDispatcher.cs",      "ECardRecipients"),
        (@"src\IPRO.Email\ELetterDispatcher.cs",    "ELetterRecipients"),
        (@"src\IPRO.Email\PollDispatcher.cs",       "PollRecipients"),
        (@"src\IPRO.Email\NewsLetterDispatcher.cs", "NewsLetterRecipients"),
    };

    public static TheoryData<string, string> SiblingCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var (f, s) in Siblings) data.Add(f, s);
        return data;
    }

    [Theory]
    [MemberData(nameof(SiblingCases))]
    public void Every_dispatcher_fans_the_reason_out_to_its_recipient_rows(string file, string recipientSet)
    {
        var body = FailAndReleaseBody(File.ReadAllText(FindRepoFile(file)));
        Assert.Contains(recipientSet, body);
        Assert.Contains("FailureReason", body);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static string FailAndReleaseBody(string src)
    {
        var start = src.IndexOf("private async Task FailAndReleaseAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "FailAndReleaseAsync moved; this pin needs updating");
        // The method's own closing brace is the first one at four-space indent after the signature;
        // everything inside it is indented deeper.
        var end = src.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of FailAndReleaseAsync");
        return src[start..end];
    }

    private static EmailConsentService NewConsent(IPRODbContext db) =>
        new(db, new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());

    private static ECardDispatcher NewDispatcher(IPRODbContext db, IEmailService email) =>
        new(db, email, NewConsent(db),
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<ECardDispatcher>.Instance);

    // A claimable card whose Occasion matches NO ECardDesign row -- the "unknown design" path. The
    // sibling "agent record no longer exists" path reaches the same method; both are covered by
    // the one fan-out.
    private static async Task<int> SeedCardWithNoDesignAsync(IPRODbContext db, int recipientCount)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"reason-{Guid.NewGuid():N}"[..20],
            Email = "reason.agent@example.com",
            FirstName = "Reason",
            LastName = "Test",
            DomainName = $"reason-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var card = new ECard
        {
            AgentUserId = agent.Id,
            Occasion = "no-such-design",
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

        return card.Id;
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public List<string> Sent { get; } = new();

        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody,
            string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null,
            string? replyToName = null, string? listUnsubscribeUrl = null)
        {
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

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
