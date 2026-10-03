using System;
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

// 544 (2026-10-03). The owner sent Bob Moore a birthday card at 3:40:48 p.m. and resubscribed him at
// 3:42:04. iPro held the card back, as it must -- but Email Activity read "Failed" for the card with
// Bob's row still "Queued", nothing failed and no reason: the contradiction 441 removed from the other
// path. The skip set the row Failed with its reason and then `continue`d past the only save in the
// loop, so the mark was lost whenever the skipped recipient was the last one. Cards and letters now
// save the mark where they make it, as polls always did.
public class SkippedRecipients544Tests
{
    private const string Reason = "Recipient has unsubscribed from these emails.";

    [Fact]
    public async Task A_card_whose_only_recipient_has_unsubscribed_says_so_on_the_recipient_row()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var clientId = await SeedUnsubscribedClientAsync(db, agentId);

        var design = new ECardDesign
        {
            Key = ($"t544-{Guid.NewGuid():N}")[..20], Occasion = "Birthday", Name = "Simple birthday",
            ImageUrl = "/images/ecard-art/simple-birthday.jpg", Width = 540, Height = 396, IsActive = true
        };
        db.Add(design);
        var card = new ECard
        {
            AgentUserId = agentId, Occasion = design.Key, Subject = "Happy BirthDay", Message = "Many happy returns.",
            Status = ECardStatuses.Scheduled, ScheduledAt = DateTime.UtcNow.AddMinutes(-1), TotalRecipients = 1
        };
        db.Add(card);
        await db.SaveChangesAsync();
        db.Add(new ECardRecipient { ECardId = card.Id, ClientId = clientId, Email = "bob544@example.test", RecipientName = "Bob Moore" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var transport = new RecordingEmailService();
        await new ECardDispatcher(db, transport, NewConsent(db), new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<ECardDispatcher>.Instance).DispatchAsync(card.Id);
        db.ChangeTracker.Clear();

        Assert.Empty(transport.Sent);                                            // held back: the consent rule did its job
        Assert.Equal(ECardStatuses.Failed, (await db.ECards.SingleAsync(c => c.Id == card.Id)).Status);
        var row = await db.ECardRecipients.SingleAsync(r => r.ECardId == card.Id);
        Assert.Equal(ECardRecipientStatuses.Failed, row.Status);                 // not Queued under a Failed card
        Assert.Equal(Reason, row.FailureReason);                                 // and the reason is where Email Activity reads it
    }

    [Fact]
    public async Task A_letter_whose_only_recipient_has_unsubscribed_says_so_on_the_recipient_row()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var clientId = await SeedUnsubscribedClientAsync(db, agentId);

        var letter = new ELetter
        {
            AgentUserId = agentId, Subject = "A note", Body = "<p>Hello</p>",
            Status = ELetterStatuses.Scheduled, ScheduledAt = DateTime.UtcNow.AddMinutes(-1), TotalRecipients = 1
        };
        db.Add(letter);
        await db.SaveChangesAsync();
        db.Add(new ELetterRecipient { ELetterId = letter.Id, ClientId = clientId, Email = "bob544@example.test", RecipientName = "Bob Moore" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var transport = new RecordingEmailService();
        await new ELetterDispatcher(db, transport, NewConsent(db), new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<ELetterDispatcher>.Instance).DispatchAsync(letter.Id);
        db.ChangeTracker.Clear();

        Assert.Empty(transport.Sent);
        Assert.Equal(ELetterStatuses.Failed, (await db.ELetters.SingleAsync(l => l.Id == letter.Id)).Status);
        var row = await db.ELetterRecipients.SingleAsync(r => r.ELetterId == letter.Id);
        Assert.Equal(ELetterRecipientStatuses.Failed, row.Status);
        Assert.Equal(Reason, row.FailureReason);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static EmailConsentService NewConsent(IPRODbContext db) =>
        new(db, new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T544-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t544-{Guid.NewGuid():N}")[..20], Email = "agent544@example.test",
            FirstName = "Held", LastName = "Back", CompanyName = "Global Business Solution",
            DomainName = ($"t544-{Guid.NewGuid():N}")[..24], PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    // Unsubscribed through a newsletter's link, as Bob was at 3:38 p.m.
    private static async Task<int> SeedUnsubscribedClientAsync(IPRODbContext db, int agentId)
    {
        var client = new Client
        {
            AgentUserId = agentId, FirstName = "Bob", LastName = "Moore", Email = "bob544@example.test",
            EmailOptOutAt = DateTime.UtcNow.AddMinutes(-2), EmailOptOutSource = "link:newsletter-footer-link"
        };
        db.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
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
}
