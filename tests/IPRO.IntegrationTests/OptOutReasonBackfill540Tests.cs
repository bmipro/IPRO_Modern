using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 540 (2026-10-02). 538 keeps the reason for every suppression made from now on. The ones made before
// it have none, so they read "Unsubscribed ... only they can" -- the owner's own two test clients, an
// hour after 538 went live: he changed their addresses and nothing happened. For a real client whose
// address bounced last month that is 538's defect, left in place: an adviser who cannot unblock them.
// His word on a one-time backfill from the email history: "go".
//
// The rule is cautious on purpose. A reason is written only where a record of the email itself shows
// it -- a bounce, or a spam complaint as the last word on that email -- within minutes of the
// suppression. Anything else stays "Unsubscribed", which is the reading only the client can undo.
public class OptOutReasonBackfill540Tests
{
    private static readonly DateTime T = new(2026, 9, 20, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_bounce_before_538_gets_its_reason_and_can_be_corrected()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var consent = NewConsent(db);
        var typo = await SeedLegacyAsync(db, agentId, "dana@exmaple.test", T);
        await SeedInvoiceEmailAsync(db, agentId, typo.Id, "dana@exmaple.test", ClientInvoiceEmailStatus.Bounced, bouncedAt: T.AddSeconds(-4));
        db.ChangeTracker.Clear();

        var result = await EmailOptOutBackfill.RunAsync(db, consent, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(new EmailOptOutBackfill.Result(Looked: 1, Bounced: 1, Complaints: 0, Lifted: 0), result);
        var row = await db.Clients.SingleAsync(c => c.Id == typo.Id);
        Assert.Equal("backfill:bounced:invoice", row.EmailOptOutSource);
        Assert.Equal(EmailOptOutReason.Bounced, EmailOptOut.ReasonOf(row));
        // Still on hold: the address on file is the one that bounced. Correcting it is what ends it (538).
        Assert.NotNull(row.EmailOptOutAt);
        Assert.True(consent.LiftBounceSuppression(row));

        // Once is enough: a second run finds nothing left to say.
        db.ChangeTracker.Clear();
        Assert.Equal(new EmailOptOutBackfill.Result(0, 0, 0, 0), await EmailOptOutBackfill.RunAsync(db, consent, NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task An_address_the_adviser_already_corrected_is_switched_back_on()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var consent = NewConsent(db);
        // The invoice bounced off a typo; the adviser fixed the address and resent it, and it arrived.
        // The client stayed blocked for everything else, with nothing left for the adviser to correct.
        var corrected = await SeedLegacyAsync(db, agentId, "dana@example.test", T);
        await SeedInvoiceEmailAsync(db, agentId, corrected.Id, "dana@exmaple.test", ClientInvoiceEmailStatus.Bounced, bouncedAt: T.AddSeconds(-4));
        await SeedInvoiceEmailAsync(db, agentId, corrected.Id, "dana@example.test", ClientInvoiceEmailStatus.Delivered);
        // The owner's test client: the address was replaced, and the replacement bounced as well.
        var stillBad = await SeedLegacyAsync(db, agentId, "bounce+two@simulator.example.test", T);
        await SeedInvoiceEmailAsync(db, agentId, stillBad.Id, "bounce@simulator.example.test", ClientInvoiceEmailStatus.Bounced, bouncedAt: T.AddSeconds(-2));
        await SeedInvoiceEmailAsync(db, agentId, stillBad.Id, "bounce+two@simulator.example.test", ClientInvoiceEmailStatus.Bounced, bouncedAt: T.AddHours(3));
        db.ChangeTracker.Clear();

        var result = await EmailOptOutBackfill.RunAsync(db, consent, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(new EmailOptOutBackfill.Result(Looked: 2, Bounced: 2, Complaints: 0, Lifted: 1), result);
        var on = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == corrected.Id);
        Assert.Null(on.EmailOptOutAt);
        Assert.Equal(string.Empty, on.EmailOptOutSource);
        Assert.False(on.IsNewsletterSubscribed);   // the newsletter is the adviser's tick box, as with any lift
        var held = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == stillBad.Id);
        Assert.NotNull(held.EmailOptOutAt);
        Assert.Equal("backfill:bounced:invoice", held.EmailOptOutSource);
    }

    [Fact]
    public async Task A_complaint_before_538_is_named_and_stays_and_outranks_a_bounce()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var consent = NewConsent(db);
        var complained = await SeedLegacyAsync(db, agentId, "omar@example.test", T);
        await SeedInvoiceEmailAsync(db, agentId, complained.Id, "omar@example.test", ClientInvoiceEmailStatus.Failed, lastEvent: "spamreport", failedAt: T.AddSeconds(-1));
        // Both on file within the window: the person's own instruction is the reason.
        var both = await SeedLegacyAsync(db, agentId, "lee@example.test", T);
        await SeedInvoiceEmailAsync(db, agentId, both.Id, "lee@old.example.test", ClientInvoiceEmailStatus.Bounced, bouncedAt: T.AddMinutes(-5));
        await SeedInvoiceEmailAsync(db, agentId, both.Id, "lee@example.test", ClientInvoiceEmailStatus.Failed, lastEvent: "spamreport", failedAt: T.AddSeconds(-1));
        db.ChangeTracker.Clear();

        var result = await EmailOptOutBackfill.RunAsync(db, consent, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(new EmailOptOutBackfill.Result(Looked: 2, Bounced: 0, Complaints: 2, Lifted: 0), result);
        foreach (var id in new[] { complained.Id, both.Id })
        {
            var row = await db.Clients.SingleAsync(c => c.Id == id);
            Assert.Equal("backfill:spamreport:invoice", row.EmailOptOutSource);
            Assert.Equal(EmailOptOutReason.Complaint, EmailOptOut.ReasonOf(row));
            Assert.NotNull(row.EmailOptOutAt);
            Assert.False(consent.LiftBounceSuppression(row));
        }
    }

    [Fact]
    public async Task What_the_record_cannot_show_stays_unsubscribed()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var consent = NewConsent(db);
        // No email on file at all: they unsubscribed from a link.
        var plain = await SeedLegacyAsync(db, agentId, "plain@example.test", T);
        // A bounce three days before they unsubscribed (a soft one; it suppressed nobody).
        var earlier = await SeedLegacyAsync(db, agentId, "earlier@example.test", T);
        await SeedInvoiceEmailAsync(db, agentId, earlier.Id, "earlier@example.test", ClientInvoiceEmailStatus.Bounced, bouncedAt: T.AddDays(-3));
        // They unsubscribed, and two hours later an invoice to them bounced.
        var later = await SeedLegacyAsync(db, agentId, "later@example.test", T);
        await SeedInvoiceEmailAsync(db, agentId, later.Id, "later@example.test", ClientInvoiceEmailStatus.Bounced, bouncedAt: T.AddHours(2));
        // A complaint that was not the last word on its email (opened afterwards), so not proof of anything.
        var opened = await SeedLegacyAsync(db, agentId, "opened@example.test", T);
        await SeedInvoiceEmailAsync(db, agentId, opened.Id, "opened@example.test", ClientInvoiceEmailStatus.Failed, lastEvent: "open", failedAt: T);
        // A reason already on the row is never rewritten, whatever else is on file.
        var known = await SeedLegacyAsync(db, agentId, "known@example.test", T, source: "one-click");
        await SeedInvoiceEmailAsync(db, agentId, known.Id, "known@example.test", ClientInvoiceEmailStatus.Bounced, bouncedAt: T.AddSeconds(-4));
        // And a client who is not suppressed is not looked at, bounced invoice or not.
        var free = await SeedLegacyAsync(db, agentId, "free@example.test", optOutAt: null);
        await SeedInvoiceEmailAsync(db, agentId, free.Id, "free@example.test", ClientInvoiceEmailStatus.Bounced, bouncedAt: T);
        db.ChangeTracker.Clear();

        var result = await EmailOptOutBackfill.RunAsync(db, consent, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(new EmailOptOutBackfill.Result(Looked: 4, Bounced: 0, Complaints: 0, Lifted: 0), result);
        var rows = await db.Clients.AsNoTracking().Where(c => c.AgentUserId == agentId).ToListAsync();
        foreach (var id in new[] { plain.Id, earlier.Id, later.Id, opened.Id })
        {
            var row = rows.Single(r => r.Id == id);
            Assert.Equal(string.Empty, row.EmailOptOutSource);
            Assert.NotNull(row.EmailOptOutAt);
            Assert.Equal(EmailOptOutReason.Unsubscribed, EmailOptOut.ReasonOf(row));
        }
        Assert.Equal("one-click", rows.Single(r => r.Id == known.Id).EmailOptOutSource);
        Assert.Null(rows.Single(r => r.Id == free.Id).EmailOptOutAt);
        Assert.Equal(string.Empty, rows.Single(r => r.Id == free.Id).EmailOptOutSource);
    }

    [Fact]
    public async Task Every_kind_of_email_is_read_for_its_bounce_and_its_complaint()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var consent = NewConsent(db);

        var ecard = new ECard { AgentUserId = agentId, Occasion = "Birthday", Subject = "Happy birthday" };
        var eletter = new ELetter { AgentUserId = agentId, TemplateKey = "welcome", Subject = "Welcome" };
        var poll = new PollSurvey { AgentUserId = agentId, Title = "Q3", Subject = "One question" };
        var article = new Article { AgentUserId = agentId, Title = "Did you know", Content = "<p>x</p>" };
        var newsletter = new NewsLetter { AgentUserId = agentId, Subject = "August", HtmlBody = "<p>x</p>" };
        var campaign = new DripCampaign { AgentUserId = agentId, Name = "Onboarding" };
        db.AddRange(ecard, eletter, poll, article, newsletter, campaign);
        await db.SaveChangesAsync();
        var step = new DripCampaignStep { DripCampaignId = campaign.Id, Subject = "Step 1", HtmlBody = "<p>x</p>" };
        db.Add(step);
        await db.SaveChangesAsync();

        async Task<Client> Legacy(string name) => await SeedLegacyAsync(db, agentId, $"{name}@example.test", T);
        var at = T.AddSeconds(-3);

        // A bounce, on each of the six kinds besides invoices.
        var byNewsletter = await Legacy("newsletter");
        var byCard = await Legacy("card");
        var byLetter = await Legacy("letter");
        var byPoll = await Legacy("poll");
        var byArticle = await Legacy("article");
        var byDrip = await Legacy("drip");
        var enrollment = new DripCampaignEnrollment { AgentUserId = agentId, DripCampaignId = campaign.Id, ClientId = byDrip.Id, Status = DripCampaignEnrollmentStatus.Cancelled };
        db.Add(enrollment);
        await db.SaveChangesAsync();
        db.AddRange(
            new NewsLetterRecipient { NewsLetterId = newsletter.Id, ClientId = byNewsletter.Id, Email = byNewsletter.Email, Status = NewsLetterRecipientStatus.Bounced, LastEvent = "bounce", BouncedAt = at },
            new ECardRecipient { ECardId = ecard.Id, ClientId = byCard.Id, Email = byCard.Email, Status = ECardRecipientStatuses.Failed, LastEvent = "bounce", BouncedAt = at },
            new ELetterRecipient { ELetterId = eletter.Id, ClientId = byLetter.Id, Email = byLetter.Email, Status = ELetterRecipientStatuses.Failed, LastEvent = "bounce", BouncedAt = at },
            new PollRecipient { PollSurveyId = poll.Id, ClientId = byPoll.Id, Email = byPoll.Email, Status = PollRecipientStatus.Failed, LastEvent = "bounce", BouncedAt = at },
            new DidYouKnowEmailQueueItem { ArticleId = article.Id, ClientId = byArticle.Id, ScheduledForUtc = T.AddDays(-1), Status = DidYouKnowQueueStatuses.Failed, LastEvent = "bounce", BouncedAt = at },
            new DripCampaignStepSend { DripCampaignEnrollmentId = enrollment.Id, DripCampaignStepId = step.Id, Email = byDrip.Email, Status = NewsLetterRecipientStatus.Bounced, BouncedAt = at });

        // A spam complaint as the last word, on the four kinds that keep the last event and when it came.
        var newsComplaint = await Legacy("news-complaint");
        var cardComplaint = await Legacy("card-complaint");
        var letterComplaint = await Legacy("letter-complaint");
        var pollComplaint = await Legacy("poll-complaint");
        db.AddRange(
            new NewsLetterRecipient { NewsLetterId = newsletter.Id, ClientId = newsComplaint.Id, Email = newsComplaint.Email, Status = NewsLetterRecipientStatus.Unsubscribed, LastEvent = "spamreport", FailureReason = "complaint: abuse", UpdatedAt = T },
            new ECardRecipient { ECardId = ecard.Id, ClientId = cardComplaint.Id, Email = cardComplaint.Email, Status = ECardRecipientStatuses.Failed, LastEvent = "spamreport", UpdatedAt = T },
            new ELetterRecipient { ELetterId = eletter.Id, ClientId = letterComplaint.Id, Email = letterComplaint.Email, Status = ELetterRecipientStatuses.Failed, LastEvent = "spamreport", UpdatedAt = T },
            new PollRecipient { PollSurveyId = poll.Id, ClientId = pollComplaint.Id, Email = pollComplaint.Email, Status = PollRecipientStatus.Failed, LastEvent = "spamreport", FailedAt = T.AddSeconds(-1) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await EmailOptOutBackfill.RunAsync(db, consent, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(new EmailOptOutBackfill.Result(Looked: 10, Bounced: 6, Complaints: 4, Lifted: 0), result);
        var source = (await db.Clients.AsNoTracking().Where(c => c.AgentUserId == agentId).ToListAsync()).ToDictionary(c => c.Id, c => c.EmailOptOutSource);
        Assert.Equal("backfill:bounced:newsletter", source[byNewsletter.Id]);
        Assert.Equal("backfill:bounced:ecard", source[byCard.Id]);
        Assert.Equal("backfill:bounced:eletter", source[byLetter.Id]);
        Assert.Equal("backfill:bounced:poll", source[byPoll.Id]);
        Assert.Equal("backfill:bounced:didyouknow", source[byArticle.Id]);
        Assert.Equal("backfill:bounced:dripcampaign", source[byDrip.Id]);
        Assert.Equal("backfill:spamreport:newsletter", source[newsComplaint.Id]);
        Assert.Equal("backfill:spamreport:ecard", source[cardComplaint.Id]);
        Assert.Equal("backfill:spamreport:eletter", source[letterComplaint.Id]);
        Assert.Equal("backfill:spamreport:poll", source[pollComplaint.Id]);
        Assert.All(source.Values, s => Assert.True(s.Length <= EmailOptOut.SourceMaxLength));
    }

    [Fact]
    public void It_runs_once_after_the_web_app_starts_and_never_blocks_the_start()
    {
        var program = Read(@"src\IPRO.Web\Program.cs");
        var guarded = program.IndexOf("if (!recurringJobsDisabled)", StringComparison.Ordinal);
        var registered = program.IndexOf("AddHostedService<IPRO.Web.Infrastructure.EmailOptOutBackfillService>()", StringComparison.Ordinal);
        Assert.True(guarded > 0 && registered > guarded, "registered with the other background work, never on a bystander instance");
        Assert.True(registered < program.IndexOf("var app = builder.Build();", StringComparison.Ordinal));

        var service = Read(@"src\IPRO.Web\Infrastructure\EmailOptOutBackfillService.cs");
        Assert.Contains("Task.Delay(TimeSpan.FromMinutes(3), stoppingToken)", service);   // after the schema repair that adds the column
        Assert.Contains("EmailOptOutBackfill.RunAsync(", service);
        Assert.Contains("catch (Exception ex)", service);                                 // a failed backfill is logged, never fatal

        // The consent fields have one home (INVARIANTS rule 1): the backfill sits beside the consent
        // service, writes the reason only, and lifts through LiftBounceSuppression like anyone else.
        var backfill = Read(@"src\IPRO.Business\Services\EmailOptOutBackfill.cs");
        Assert.Contains("consent.LiftBounceSuppression(", backfill);
        Assert.DoesNotContain("EmailOptOutAt = ", backfill);
        Assert.DoesNotContain("IsNewsletterSubscribed = ", backfill);
    }

    // ---- harness -----------------------------------------------------------------------------------

    private static EmailConsentService NewConsent(IPRODbContext db) =>
        new(db, new ConfigurationBuilder().Build(), NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var agent = new AgentUser
        {
            UserName = $"t540-{Guid.NewGuid():N}"[..20], Email = $"{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Legacy", LastName = "Adviser", CompanyName = "Legacy Co", DomainName = $"t540-{Guid.NewGuid():N}"[..24]
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    // A suppression as it was written before 538: the time, and no reason. The newsletter flag is off,
    // as SuppressAllAsync leaves it.
    private static async Task<Client> SeedLegacyAsync(IPRODbContext db, int agentId, string email, DateTime? optOutAt, string source = "")
    {
        var client = new Client
        {
            AgentUserId = agentId, FirstName = "Legacy", LastName = "Client", Email = email,
            IsNewsletterSubscribed = false, EmailOptOutAt = optOutAt, EmailOptOutSource = source
        };
        db.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task SeedInvoiceEmailAsync(IPRODbContext db, int agentId, int clientId, string toEmail, ClientInvoiceEmailStatus status,
        DateTime? bouncedAt = null, string lastEvent = "", DateTime? failedAt = null)
    {
        var invoice = new ClientInvoice
        {
            AgentUserId = agentId, ClientId = clientId, DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent,
            DocumentNumber = $"INV-{Guid.NewGuid():N}"[..16], Total = 100m, Currency = "CAD", ViewToken = Guid.NewGuid().ToString("N"),
            DueDate = T.Date.AddDays(15), SentAt = T.AddMinutes(-1)
        };
        db.Add(invoice);
        await db.SaveChangesAsync();
        db.Add(new ClientInvoiceEmail
        {
            ClientInvoiceId = invoice.Id, AgentUserId = agentId, ClientId = clientId, Kind = ClientInvoiceEmailKind.Send, ToEmail = toEmail,
            Subject = "Invoice", ProviderMessageId = Guid.NewGuid().ToString("N"), Status = status,
            LastEvent = lastEvent.Length > 0 ? lastEvent : status == ClientInvoiceEmailStatus.Bounced ? "bounce" : status == ClientInvoiceEmailStatus.Delivered ? "delivered" : string.Empty,
            SentAt = T.AddMinutes(-1), BouncedAt = bouncedAt, FailedAt = failedAt, DeliveredAt = status == ClientInvoiceEmailStatus.Delivered ? T.AddMinutes(-1) : null,
            UpdatedAt = failedAt ?? bouncedAt ?? T
        });
        await db.SaveChangesAsync();
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
