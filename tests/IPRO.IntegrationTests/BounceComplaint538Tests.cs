using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 538 (2026-10-02). The owner's pilot of Amazon SES on his own account: an estimate to Amazon's
// bounce mailbox, an invoice to its complaint mailbox. Amazon did its part and so did iPro -- both
// clients were suppressed -- but what iPro SAID was wrong three times over:
//   * the complaint's invoice read "could not be sent (complaint: abuse)" and "Send failed", when
//     it had been delivered and then reported as spam (he resent it nine minutes later);
//   * the notice to the adviser said both clients "unsubscribed from your emails";
//   * a bounced client read "Unsubscribed ... only they can" turn it back on -- about an address
//     that does not exist, with no way for the adviser to end it by correcting the address.
// And, found reading the code beside the test: Amazon's "delivery delay" event marked an email
// failed for good, so one that arrived ten minutes later would still read "could not be sent".
public class BounceComplaint538Tests
{
    // ---- why a client is suppressed --------------------------------------------------------------

    [Theory]
    [InlineData("ses:bounced:invoice", EmailOptOutReason.Bounced)]
    [InlineData("acs:bounced:newsletter", EmailOptOutReason.Bounced)]
    [InlineData("sendgrid:spamreport:invoice", EmailOptOutReason.Complaint)]
    [InlineData("sendgrid:spamreport:newsletter", EmailOptOutReason.Complaint)]
    [InlineData("sendgrid:unsubscribe:ecard", EmailOptOutReason.Unsubscribed)]
    [InlineData("sendgrid:group_unsubscribe:poll", EmailOptOutReason.Unsubscribed)]
    [InlineData("one-click", EmailOptOutReason.Unsubscribed)]
    [InlineData("link", EmailOptOutReason.Unsubscribed)]
    [InlineData("newsletter-footer-link", EmailOptOutReason.Unsubscribed)]
    [InlineData("", EmailOptOutReason.Unsubscribed)]       // recorded before 538: the cautious reading
    [InlineData(null, EmailOptOutReason.Unsubscribed)]
    public void The_source_says_why(string? source, EmailOptOutReason reason) =>
        Assert.Equal(reason, EmailOptOut.ReasonOf(source));

    [Fact]
    public async Task A_suppression_remembers_its_source_and_a_resubscribe_forgets_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var consent = NewConsent(db);
        var bounced = await SeedClientAsync(db, agentId, "typo@exmaple.test", subscribed: true);
        // The same person under a second row (443): suppressed with it, for the same reason.
        var sibling = await SeedClientAsync(db, agentId, "TYPO@exmaple.test", subscribed: true);

        await consent.SuppressAllAsync(bounced, "ses:bounced:invoice");
        db.ChangeTracker.Clear();

        var rows = await db.Clients.AsNoTracking().Where(c => c.Id == bounced.Id || c.Id == sibling.Id).ToListAsync();
        Assert.All(rows, r => Assert.NotNull(r.EmailOptOutAt));
        Assert.All(rows, r => Assert.Equal("ses:bounced:invoice", r.EmailOptOutSource));
        Assert.All(rows, r => Assert.Equal(EmailOptOutReason.Bounced, EmailOptOut.ReasonOf(r)));

        // A second bounce reported about the same client changes nothing, the reason included.
        var again = await db.Clients.SingleAsync(c => c.Id == bounced.Id);
        Assert.True((await consent.SuppressAllAsync(again, "acs:bounced:newsletter")).WasAlreadySuppressed);
        Assert.Equal("ses:bounced:invoice", (await db.Clients.AsNoTracking().SingleAsync(c => c.Id == bounced.Id)).EmailOptOutSource);

        // But the person's own instruction outranks an address problem: a spam complaint about mail
        // delivered before the bounce (the reports can lag by days) must survive a corrected address.
        Assert.True((await consent.SuppressAllAsync(again, "sendgrid:spamreport:newsletter")).WasAlreadySuppressed);
        Assert.Equal("sendgrid:spamreport:newsletter", (await db.Clients.AsNoTracking().SingleAsync(c => c.Id == bounced.Id)).EmailOptOutSource);
        Assert.False(consent.LiftBounceSuppression(again));
        // Never the other way round: a bounce reported about someone who unsubscribed stays an unsubscribe.
        var left = await SeedSuppressedAsync(db, consent, agentId, "left@example.test", "one-click");
        await consent.SuppressAllAsync(left, "ses:bounced:invoice");
        Assert.Equal("one-click", (await db.Clients.AsNoTracking().SingleAsync(c => c.Id == left.Id)).EmailOptOutSource);

        // A source longer than the column is clipped, never an error: suppression has legal weight.
        var wordy = await SeedClientAsync(db, agentId, "wordy@example.test", subscribed: true);
        await consent.SuppressAllAsync(wordy, "sendgrid:spamreport:" + new string('x', 200));
        var kept = (await db.Clients.AsNoTracking().SingleAsync(c => c.Id == wordy.Id)).EmailOptOutSource;
        Assert.Equal(EmailOptOut.SourceMaxLength, kept.Length);
        Assert.Equal(EmailOptOutReason.Complaint, EmailOptOut.ReasonOf(kept));

        await consent.ResubscribeAsync(again);
        var back = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == bounced.Id);
        Assert.Null(back.EmailOptOutAt);
        Assert.Equal(string.Empty, back.EmailOptOutSource);
    }

    // ---- a bounce ends when the address is replaced; nothing else does ---------------------------

    [Fact]
    public async Task Only_a_bounce_is_lifted_and_only_by_the_consent_service()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var consent = NewConsent(db);
        var bounced = await SeedSuppressedAsync(db, consent, agentId, "b@example.test", "ses:bounced:invoice");
        var complained = await SeedSuppressedAsync(db, consent, agentId, "c@example.test", "sendgrid:spamreport:invoice");
        var unsubscribed = await SeedSuppressedAsync(db, consent, agentId, "u@example.test", "one-click");
        var legacy = await SeedSuppressedAsync(db, consent, agentId, "l@example.test", "");
        var free = await SeedClientAsync(db, agentId, "f@example.test", subscribed: true);

        Assert.True(consent.LiftBounceSuppression(bounced));
        Assert.Null(bounced.EmailOptOutAt);
        Assert.Equal(string.Empty, bounced.EmailOptOutSource);
        Assert.False(bounced.IsNewsletterSubscribed);   // the newsletter is the adviser's tick box, not ours to restore

        foreach (var other in new[] { complained, unsubscribed, legacy })
        {
            var before = other.EmailOptOutSource;
            Assert.False(consent.LiftBounceSuppression(other));
            Assert.NotNull(other.EmailOptOutAt);
            Assert.Equal(before, other.EmailOptOutSource);
        }
        Assert.False(consent.LiftBounceSuppression(free));
        Assert.Null(free.EmailOptOutAt);

        // It did not save: the caller writes the new address in the same unit of work.
        Assert.NotNull((await db.Clients.AsNoTracking().SingleAsync(c => c.Id == bounced.Id)).EmailOptOutAt);
    }

    [Fact]
    public async Task Correcting_a_bounced_address_on_the_client_record_switches_email_back_on()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var consent = NewConsent(db);
        var typo = await SeedSuppressedAsync(db, consent, agentId, "dana@exmaple.test", "ses:bounced:invoice");
        var untouched = await SeedSuppressedAsync(db, consent, agentId, "sam@exmaple.test", "ses:bounced:estimate");
        var complained = await SeedSuppressedAsync(db, consent, agentId, "omar@example.test", "sendgrid:spamreport:invoice");
        db.ChangeTracker.Clear();

        // The typo corrected, and the newsletter ticked again in the same form.
        var controller = NewClientsController(db, consent, agentId);
        Assert.IsType<RedirectToActionResult>(await controller.Edit(Form(typo, "dana@example.test", newsletter: true), Array.Empty<int>()));
        Assert.Contains("switched back on", (string)controller.TempData["Success"]!);
        db.ChangeTracker.Clear();
        var fixedRow = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == typo.Id);
        Assert.Equal("dana@example.test", fixedRow.Email);
        Assert.Null(fixedRow.EmailOptOutAt);
        Assert.Equal(string.Empty, fixedRow.EmailOptOutSource);
        Assert.True(fixedRow.IsNewsletterSubscribed);
        Assert.False(consent.IsSuppressed(fixedRow, EmailChannel.Newsletter));

        // The same address saved again (a phone number changed): the bounce stands.
        controller = NewClientsController(db, consent, agentId);
        var same = Form(untouched, "sam@exmaple.test", newsletter: true);
        same.Phone = "416-555-0100";
        Assert.IsType<RedirectToActionResult>(await controller.Edit(same, Array.Empty<int>()));
        Assert.Equal("Client updated.", (string)controller.TempData["Success"]!);
        db.ChangeTracker.Clear();
        var still = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == untouched.Id);
        Assert.NotNull(still.EmailOptOutAt);
        Assert.False(still.IsNewsletterSubscribed);

        // A complaint is the person's own instruction: a new address does not undo it.
        controller = NewClientsController(db, consent, agentId);
        Assert.IsType<RedirectToActionResult>(await controller.Edit(Form(complained, "omar.new@example.test", newsletter: true), Array.Empty<int>()));
        db.ChangeTracker.Clear();
        var kept = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == complained.Id);
        Assert.Equal("omar.new@example.test", kept.Email);
        Assert.NotNull(kept.EmailOptOutAt);
        Assert.Equal("sendgrid:spamreport:invoice", kept.EmailOptOutSource);
        Assert.False(kept.IsNewsletterSubscribed);
    }

    [Fact]
    public async Task The_client_changing_their_own_address_in_the_portal_ends_a_bounce_too()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var consent = NewConsent(db);
        var bounced = await SeedSuppressedAsync(db, consent, agentId, "old@example.test", "acs:bounced:newsletter");
        var unsubscribed = await SeedSuppressedAsync(db, consent, agentId, "gone@example.test", "link");
        db.ChangeTracker.Clear();

        await NewProfileController(db, consent, bounced.Id).Index(new PortalProfileViewModel { FirstName = "B", LastName = "Client", Email = "New@Example.test" });
        await NewProfileController(db, consent, unsubscribed.Id).Index(new PortalProfileViewModel { FirstName = "U", LastName = "Client", Email = "elsewhere@example.test" });
        db.ChangeTracker.Clear();

        var lifted = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == bounced.Id);
        Assert.Equal("new@example.test", lifted.Email);
        Assert.Null(lifted.EmailOptOutAt);
        Assert.Equal(string.Empty, lifted.EmailOptOutSource);
        Assert.NotNull((await db.Clients.AsNoTracking().SingleAsync(c => c.Id == unsubscribed.Id)).EmailOptOutAt);
    }

    // ---- what the adviser is told ----------------------------------------------------------------

    [Fact]
    public async Task The_notice_to_the_adviser_says_what_happened()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var mail = new RecordingEmail();
        var consent = new EmailConsentService(db, new ConfigurationBuilder().Build(), NullLogger<EmailConsentService>.Instance,
            new IUnsubscribeNotifier[] { new UnsubscribeNotifier(db, mail) });

        var bounced = await SeedClientAsync(db, agentId, "typo@exmaple.test", subscribed: true, first: "Dana", last: "Whitcombe");
        await consent.SuppressAllAsync(bounced, "ses:bounced:invoice");
        var bounce = Assert.Single(mail.Sent);
        Assert.Equal("An email to Dana Whitcombe bounced", bounce.Subject);
        Assert.Contains("typo@exmaple.test", bounce.Html);
        Assert.Contains("does not exist or cannot receive mail", bounce.Html);
        Assert.Contains("correct it", bounce.Html);
        Assert.DoesNotContain("unsubscribed", bounce.Html);

        var complained = await SeedClientAsync(db, agentId, "omar@example.test", subscribed: true, first: "Omar", last: "Haddad");
        await consent.SuppressAllAsync(complained, "sendgrid:spamreport:invoice");
        var complaint = mail.Sent[1];
        Assert.Equal("Omar Haddad reported one of your emails as spam", complaint.Subject);
        Assert.Contains("marked one of your emails as spam", complaint.Html);
        Assert.DoesNotContain("has unsubscribed", complaint.Html);

        // The person's own unsubscribe reads exactly as it always did.
        var left = await SeedClientAsync(db, agentId, "lee@example.test", subscribed: true, first: "Lee", last: "Park");
        await consent.SuppressAllAsync(left, "one-click");
        var unsubscribe = mail.Sent[2];
        Assert.Equal("Lee Park unsubscribed from your emails", unsubscribe.Subject);
        Assert.Contains("Lee Park has unsubscribed from your emails.", unsubscribe.Html);
        Assert.Equal(3, mail.Sent.Count);
        Assert.All(mail.Sent, m => Assert.EndsWith("@example.test", m.To));   // the adviser, never the client's bad address
    }

    // ---- Amazon's reports on an invoice email ----------------------------------------------------

    [Fact]
    public async Task A_complaint_about_an_invoice_is_a_complaint_not_a_failed_send()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (rowId, clientId) = await SeedInvoiceEmailAsync(db, agentId, "ses-inv-complaint");

        Assert.IsType<OkResult>(await PostSesAsync(db, SesEvent("Delivery", "ses-inv-complaint", "\"delivery\":{\"timestamp\":\"2026-10-02T18:32:05.000Z\",\"recipients\":[\"complaint@simulator.amazonses.com\"]}")));
        Assert.IsType<OkResult>(await PostSesAsync(db, SesEvent("Complaint", "ses-inv-complaint", "\"complaint\":{\"complainedRecipients\":[{\"emailAddress\":\"complaint@simulator.amazonses.com\"}],\"complaintFeedbackType\":\"abuse\",\"timestamp\":\"2026-10-02T18:32:09.000Z\"}")));
        db.ChangeTracker.Clear();

        // The record: it WAS delivered, and the last word on it is the complaint.
        var row = await db.ClientInvoiceEmails.AsNoTracking().SingleAsync(e => e.Id == rowId);
        Assert.NotNull(row.DeliveredAt);
        Assert.Equal("spamreport", row.LastEvent);
        Assert.Equal("complaint: abuse", row.FailureReason);
        var client = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId);
        Assert.NotNull(client.EmailOptOutAt);
        Assert.Equal(EmailOptOutReason.Complaint, EmailOptOut.ReasonOf(client));

        // The screen: the invoice says so, in both places, instead of "could not be sent" / "Send failed".
        var document = Read(@"src\IPRO.Web\Views\ClientInvoices\_ClientInvoiceDocument.cshtml");
        var complaintBranch = document.IndexOf("e.LastEvent == \"spamreport\"", StringComparison.Ordinal);
        var failedBranch = document.IndexOf("could not be sent@", StringComparison.Ordinal);
        Assert.True(complaintBranch > 0 && complaintBranch < failedBranch, "the complaint is tested for before the plain failure");
        Assert.Contains("reported as spam by the recipient", document);
        var list = Read(@"src\IPRO.Web\Views\ClientInvoices\Index.cshtml");
        Assert.Contains("latest.LastEvent == \"spamreport\"", list);
        Assert.Contains(">Spam complaint</span>", list);
        Assert.Contains(">Send failed</span>", list);   // still what a refused send reads
        // And Email Activity, for every kind of email.
        var activity = Read(@"src\IPRO.Web\Views\EmailActivity\Details.cshtml");
        Assert.Contains("(\"Reported spam\", \"bg-danger-subtle text-danger\")", activity);
    }

    [Fact]
    public async Task A_bounce_on_an_invoice_is_remembered_as_a_bounce()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (rowId, clientId) = await SeedInvoiceEmailAsync(db, agentId, "ses-inv-bounce");

        Assert.IsType<OkResult>(await PostSesAsync(db, SesEvent("Bounce", "ses-inv-bounce", "\"bounce\":{\"bounceType\":\"Permanent\",\"bounceSubType\":\"General\",\"bouncedRecipients\":[{\"emailAddress\":\"bounce@simulator.amazonses.com\",\"diagnosticCode\":\"smtp; 550 5.1.1 user unknown\"}],\"timestamp\":\"2026-10-02T18:31:06.000Z\"}")));
        db.ChangeTracker.Clear();

        Assert.Equal(ClientInvoiceEmailStatus.Bounced, (await db.ClientInvoiceEmails.AsNoTracking().SingleAsync(e => e.Id == rowId)).Status);
        var client = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId);
        Assert.Equal("ses:bounced:invoice", client.EmailOptOutSource);
        Assert.Equal(EmailOptOutReason.Bounced, EmailOptOut.ReasonOf(client));
    }

    [Fact]
    public async Task A_delay_at_the_recipients_mail_server_is_not_the_end_of_the_story()
    {
        // Amazon keeps trying after a delay and reports the outcome separately: a Delivery, or a Bounce.
        Assert.Null(SesEmailEventsController.MapEvent("DeliveryDelay", null));

        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (arrives, _) = await SeedInvoiceEmailAsync(db, agentId, "ses-inv-delayed");
        var (givenUp, _) = await SeedInvoiceEmailAsync(db, agentId, "ses-inv-given-up");
        const string delay = "\"deliveryDelay\":{\"delayType\":\"MailboxFull\",\"timestamp\":\"2026-10-02T18:40:00.000Z\",\"expirationTime\":\"2026-10-03T08:40:00.000Z\",\"delayedRecipients\":[{\"emailAddress\":\"x@example.test\"}]}";

        Assert.IsType<OkResult>(await PostSesAsync(db, SesEvent("DeliveryDelay", "ses-inv-delayed", delay)));
        Assert.IsType<OkResult>(await PostSesAsync(db, SesEvent("DeliveryDelay", "ses-inv-given-up", delay)));
        db.ChangeTracker.Clear();
        // While Amazon is still trying, the email is what it was: accepted, report pending.
        Assert.All(await db.ClientInvoiceEmails.AsNoTracking().Where(e => e.Id == arrives || e.Id == givenUp).ToListAsync(),
            e => Assert.Equal(ClientInvoiceEmailStatus.Sent, e.Status));

        Assert.IsType<OkResult>(await PostSesAsync(db, SesEvent("Delivery", "ses-inv-delayed", "\"delivery\":{\"timestamp\":\"2026-10-02T18:50:00.000Z\",\"recipients\":[\"x@example.test\"]}")));
        Assert.IsType<OkResult>(await PostSesAsync(db, SesEvent("Bounce", "ses-inv-given-up", "\"bounce\":{\"bounceType\":\"Transient\",\"bounceSubType\":\"MailboxFull\",\"bouncedRecipients\":[{\"emailAddress\":\"x@example.test\",\"diagnosticCode\":\"smtp; 452 4.2.2 mailbox full\"}],\"timestamp\":\"2026-10-03T08:40:00.000Z\"}")));
        db.ChangeTracker.Clear();

        var delivered = await db.ClientInvoiceEmails.AsNoTracking().SingleAsync(e => e.Id == arrives);
        Assert.Equal(ClientInvoiceEmailStatus.Delivered, delivered.Status);
        Assert.Equal(string.Empty, delivered.FailureReason);
        // And when Amazon gives up, THAT is the failure, with the mail server's own words.
        var failed = await db.ClientInvoiceEmails.AsNoTracking().SingleAsync(e => e.Id == givenUp);
        Assert.Equal(ClientInvoiceEmailStatus.Failed, failed.Status);
        Assert.Contains("mailbox full", failed.FailureReason);
        // A full mailbox is not a dead address: nobody is suppressed for it.
        Assert.False(await db.Clients.AsNoTracking().AnyAsync(c => c.AgentUserId == agentId && c.EmailOptOutAt != null));
    }

    // ---- the screens, the guides, and production's column ----------------------------------------

    [Fact]
    public void The_client_record_names_the_reason_and_production_gets_the_column()
    {
        var details = Read(@"src\IPRO.Web\Views\Clients\Details.cshtml");
        Assert.Contains("EmailOptOut.ReasonOf(Model)", details);
        Assert.Contains("Email bounced", details);
        Assert.Contains("Reported spam", details);
        Assert.Contains("Unsubscribed", details);

        var edit = Read(@"src\IPRO.Web\Views\Clients\Edit.cshtml");
        Assert.Contains("EmailOptOut.ReasonOf(Model)", edit);
        Assert.Contains("Correct the address above and save", edit);
        // 492's sentence stays for the two reasons it is true of.
        Assert.Contains("only they can, from the preferences link in any message or from their portal", edit);

        // Both apps repair the schema from this one list at startup (INVARIANTS rule 4).
        var schema = Read(@"src\IPRO.DataAccess\EmailDeliverySchema.cs");
        Assert.Contains("(\"Clients\", \"EmailOptOutSource\",", schema);
        Assert.Contains("varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''", schema);
        Assert.Equal(100, EmailOptOut.SourceMaxLength);

        // The guides say what the screens say.
        var invoicing = Read(@"DOCS\10_CLIENT_INVOICING.md");
        Assert.Contains("reported as spam", invoicing);
        Assert.DoesNotContain("until you resubscribe them from their profile", invoicing);   // it was never possible
        Assert.Contains("Email bounced", Read(@"DOCS\02_CLIENTS_AND_FOLLOWUPS.md"));
        Assert.Contains("Reported spam", Read(@"DOCS\23_EMAIL_ACTIVITY.md"));
    }

    // ---- harness -----------------------------------------------------------------------------------

    private const string TopicArn = "arn:aws:sns:ca-central-1:354245663230:ipro-ses-events";

    private static EmailConsentService NewConsent(IPRODbContext db) =>
        new(db, new ConfigurationBuilder().Build(), NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());

    private static ClientsController NewClientsController(IPRODbContext db, IEmailConsentService consent, int agentId)
    {
        // Edit touches the database, the model state and TempData; the services it never reaches are left out.
        var controller = new ClientsController(null!, null!, new IPRO.DataAccess.Repositories.UnitOfWork(db), db, null!, null!, null!, null!,
            new EphemeralDataProtectionProvider(), new ConfigurationBuilder().Build(), consent);
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test")) };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static ClientPortalProfileController NewProfileController(IPRODbContext db, IEmailConsentService consent, int clientId)
    {
        var controller = new ClientPortalProfileController(db, consent);
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, clientId.ToString()) }, "ClientPortal")) };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    // What the edit form posts back: the record as it is, with a new address and the newsletter box.
    private static Client Form(Client current, string email, bool newsletter) => new()
    {
        Id = current.Id, FirstName = current.FirstName, LastName = current.LastName, Email = email,
        IsNewsletterSubscribed = newsletter, Country = "Canada"
    };

    private static async Task<IActionResult> PostSesAsync(IPRODbContext db, string message)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Type"] = "Notification", ["MessageId"] = "m-1", ["TopicArn"] = TopicArn, ["Message"] = message,
            ["Timestamp"] = "2026-10-02T18:31:00.000Z", ["SignatureVersion"] = "2", ["Signature"] = "c2ln",
            ["SigningCertURL"] = "https://sns.ca-central-1.amazonaws.com/SimpleNotificationService-abc.pem",
        });
        var consent = NewConsent(db);
        var settings = new EmailSettings { Ses = new SesSettings { EventSecret = "s3cret", EventTopicArn = TopicArn } };
        var controller = new SesEmailEventsController(db, new NewsLetterService(new IPRO.DataAccess.Repositories.UnitOfWork(db), consent, db),
            new EmailDeliveryTracker(db, NullLogger<EmailDeliveryTracker>.Instance, consent), consent,
            Options.Create(settings), new TrustAll(), NullLogger<SesEmailEventsController>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?secret=s3cret");
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return await controller.Index();
    }

    private static string SesEvent(string eventType, string messageId, string detail) =>
        "{\"eventType\":\"" + eventType + "\",\"mail\":{\"timestamp\":\"2026-10-02T18:31:00.000Z\",\"messageId\":\"" + messageId +
        "\",\"tags\":{\"ipro_stream\":[\"notify\"]}}," + detail + "}";

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var agent = new AgentUser
        {
            UserName = $"t538-{Guid.NewGuid():N}"[..20], Email = $"{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Pilot", LastName = "Adviser", CompanyName = "Global Business Solution", DomainName = $"t538-{Guid.NewGuid():N}"[..24]
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<Client> SeedClientAsync(IPRODbContext db, int agentId, string email, bool subscribed, string first = "Test", string last = "Client")
    {
        var client = new Client { AgentUserId = agentId, FirstName = first, LastName = last, Email = email, IsNewsletterSubscribed = subscribed };
        db.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    // Suppressed the way production does it: through the consent service, with the source a writer passes.
    private static async Task<Client> SeedSuppressedAsync(IPRODbContext db, EmailConsentService consent, int agentId, string email, string source)
    {
        var client = await SeedClientAsync(db, agentId, email, subscribed: true);
        await consent.SuppressAllAsync(client, source);
        return client;
    }

    private static async Task<(int RowId, int ClientId)> SeedInvoiceEmailAsync(IPRODbContext db, int agentId, string providerMessageId)
    {
        var client = await SeedClientAsync(db, agentId, $"{providerMessageId}@example.test", subscribed: false);
        var invoice = new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id, DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent,
            DocumentNumber = ("INV-" + providerMessageId)[..Math.Min(20, providerMessageId.Length + 4)], Total = 100m, Currency = "CAD",
            ViewToken = Guid.NewGuid().ToString("N"), DueDate = DateTime.UtcNow.Date.AddDays(15), SentAt = DateTime.UtcNow.AddMinutes(-1)
        };
        db.Add(invoice);
        await db.SaveChangesAsync();
        var row = new ClientInvoiceEmail
        {
            ClientInvoiceId = invoice.Id, AgentUserId = agentId, ClientId = client.Id, Kind = ClientInvoiceEmailKind.Send,
            ToEmail = client.Email, Subject = "Invoice from Global Business Solution", ProviderMessageId = providerMessageId,
            Status = ClientInvoiceEmailStatus.Sent, SentAt = DateTime.UtcNow.AddMinutes(-1)
        };
        db.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (row.Id, client.Id);
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }

    private sealed class TrustAll : ISnsTrust
    {
        public Task<bool> VerifyAsync(SnsEnvelope envelope) => Task.FromResult(true);
        public Task<bool> ConfirmSubscriptionAsync(string subscribeUrl) => Task.FromResult(true);
    }

    private sealed record SentMail(string To, string Subject, string Html);

    private sealed class RecordingEmail : IEmailService
    {
        public List<SentMail> Sent { get; } = new();

        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            Sent.Add(new SentMail(toEmail, subject, htmlBody));
            return Task.FromResult(EmailSendResult.Sent($"msg-{Sent.Count}"));
        }

        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => throw new NotSupportedException();
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => throw new NotSupportedException();
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
