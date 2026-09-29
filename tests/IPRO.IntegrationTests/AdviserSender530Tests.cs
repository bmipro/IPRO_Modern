using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 530 (2026-09-29), step one. The owner: clients of an adviser get mail from "IPRO Advisers", "which
// they will have no idea as who iproadvisers is". Until each adviser can have their own sender name,
// every email to an adviser's client names the adviser's business in its subject and sends replies
// to the adviser -- never to iPro's support mailbox. One class, AdviserSender, decides both.
public class AdviserSender530Tests
{
    // Every file that emails an adviser's CLIENT, and how many such sends it makes. Adviser-facing
    // mail (lead notices, test sends, iPro's own notices) is not listed and not affected.
    private static readonly (string File, int Sends)[] ClientSenders =
    {
        (@"src\IPRO.Web\Controllers\ClientInvoicesController.cs", 2),      // send, remind by hand
        (@"src\IPRO.Scheduler\OverdueInvoiceReminderJob.cs", 1),          // the reminder schedule
        (@"src\IPRO.Email\NewsLetterDispatcher.cs", 3),                   // newsletters, two drip paths
        (@"src\IPRO.Email\ECardDispatcher.cs", 1),
        (@"src\IPRO.Email\ELetterDispatcher.cs", 1),
        (@"src\IPRO.Email\PollDispatcher.cs", 1),
        (@"src\IPRO.Scheduler\DidYouKnowEmailDispatchJob.cs", 1),
        (@"src\IPRO.Web\Controllers\ClientsController.cs", 1),            // the client-portal invite
        (@"src\IPRO.Web\Controllers\PortalRequestsController.cs", 2),     // appointment scheduled, declined
        (@"src\IPRO.Web\Controllers\TestimonialsController.cs", 1),
    };

    [Fact]
    public void Every_email_to_a_client_replies_to_the_adviser_and_names_the_business()
    {
        Assert.Contains("public static class AdviserSender", Read(@"src\IPRO.Entities\AdviserSender.cs"));

        foreach (var (file, expected) in ClientSenders)
        {
            var sends = SendCalls(Read(file));
            Assert.True(sends.Count == expected, $"{file}: expected {expected} client send(s), found {sends.Count}");
            foreach (var call in sends)
            {
                Assert.True(call.Contains("replyToEmail:", StringComparison.Ordinal), $"{file}: a send to a client has no reply-to:\n{call}");
                Assert.True(call.Contains("AdviserSender.", StringComparison.Ordinal), $"{file}: a send to a client does not take its reply-to from AdviserSender:\n{call}");
            }
            Assert.Contains("AdviserSender.", Read(file));
        }

        // Adviser-written subjects are led by the business, in every sender that carries one.
        foreach (var file in new[] { @"src\IPRO.Email\NewsLetterDispatcher.cs", @"src\IPRO.Email\ECardDispatcher.cs", @"src\IPRO.Email\ELetterDispatcher.cs", @"src\IPRO.Email\PollDispatcher.cs", @"src\IPRO.Scheduler\DidYouKnowEmailDispatchJob.cs" })
            Assert.Contains("AdviserSender.Subject(", Read(file));

        // The worded subjects name the business too.
        Assert.Contains("AdviserSender.BusinessName(", Read(@"src\IPRO.Web\Controllers\ClientInvoicesController.cs"));
        Assert.Contains("AdviserSender.BusinessName(", Read(@"src\IPRO.Scheduler\ClientInvoiceReminderEmail.cs"));
        Assert.Contains("AdviserSender.BusinessName(", Read(@"src\IPRO.Web\Controllers\PortalRequestsController.cs"));
    }

    // -- the rules, without a database ----------------------------------------------------------------

    [Fact]
    public void The_business_leads_the_subject_once_and_replies_go_to_the_adviser()
    {
        var business = new AgentUser { CompanyName = " Global Business Solution ", FirstName = "Bahman", LastName = "Motamed", Email = " bm@example.test " };
        var person = new AgentUser { CompanyName = "  ", FirstName = "Bahman", LastName = "Motamed", Email = "bm@example.test" };
        var nobody = new AgentUser { CompanyName = "", FirstName = "", LastName = "", Email = "" };

        Assert.Equal("Global Business Solution", AdviserSender.BusinessName(business));
        Assert.Equal("Bahman Motamed", AdviserSender.BusinessName(person));
        Assert.Equal(string.Empty, AdviserSender.BusinessName(nobody));
        Assert.Equal(string.Empty, AdviserSender.BusinessName(null));

        Assert.Equal("Global Business Solution: October market update", AdviserSender.Subject(business, " October market update "));
        Assert.Equal("Bahman Motamed: Happy birthday, Ann!", AdviserSender.Subject(person, "Happy birthday, Ann!"));
        // Named already, in any case: left alone, never doubled.
        Assert.Equal("News from GLOBAL business solution", AdviserSender.Subject(business, "News from GLOBAL business solution"));
        // No name to give: the adviser's words exactly.
        Assert.Equal("October market update", AdviserSender.Subject(nobody, "October market update"));
        Assert.Equal("October market update", AdviserSender.Subject(null, "October market update"));
        Assert.Equal("Global Business Solution", AdviserSender.Subject(business, "  "));

        Assert.Equal("bm@example.test", AdviserSender.ReplyToEmail(business));
        Assert.Equal("Global Business Solution", AdviserSender.ReplyToName(business));
        Assert.Equal("Bahman Motamed", AdviserSender.ReplyToName(person));
        Assert.Null(AdviserSender.ReplyToEmail(nobody));
        Assert.Null(AdviserSender.ReplyToName(nobody));
        Assert.Null(AdviserSender.ReplyToEmail(null));
    }

    [Fact]
    public void Reminder_subjects_say_whose_invoice_it_is()
    {
        Assert.Equal("Invoice INV-7 from Acme Planning is due in 3 days", ClientInvoiceReminderSchedule.SubjectFor("before", "INV-7", -3, "Acme Planning"));
        Assert.Equal("Invoice INV-7 from Acme Planning is due in 1 day", ClientInvoiceReminderSchedule.SubjectFor("before", "INV-7", -1, "Acme Planning"));
        Assert.Equal("Invoice INV-7 from Acme Planning is due today", ClientInvoiceReminderSchedule.SubjectFor("due", "INV-7", 0, " Acme Planning "));
        Assert.Equal("Reminder: Invoice INV-7 from Acme Planning is overdue", ClientInvoiceReminderSchedule.SubjectFor("overdue7", "INV-7", 9, "Acme Planning"));
        // Without a name, exactly the wording before 530.
        Assert.Equal("Invoice INV-7 is due today", ClientInvoiceReminderSchedule.SubjectFor("due", "INV-7", 0, ""));
        Assert.Equal("Reminder: Invoice INV-7 is overdue", ClientInvoiceReminderSchedule.SubjectFor("overdue14", "INV-7", 20));
    }

    // -- invoices, against MySQL: the send, the reminder by hand, the reminder schedule ------------------

    [Fact]
    public async Task An_invoice_and_its_reminders_name_the_business_and_reply_to_the_adviser()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "Acme Planning", "advisor@acme.test");

        // The send.
        var draftId = await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Draft, DateTime.UtcNow.Date.AddDays(14));
        var email = new RecordingEmail();
        await NewInvoicesController(db, email, agentId).Send(draftId);
        var sent = Assert.Single(email.Sent);
        var number = await db.ClientInvoices.AsNoTracking().Where(i => i.Id == draftId).Select(i => i.DocumentNumber).SingleAsync();
        Assert.Equal($"Invoice {number} from Acme Planning", sent.Subject);
        Assert.Equal("advisor@acme.test", sent.ReplyTo);
        Assert.Equal("Acme Planning", sent.ReplyToName);

        // The reminder by hand, on an overdue invoice.
        var overdueId = await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Sent, DateTime.UtcNow.Date.AddDays(-10));
        email = new RecordingEmail();
        await NewInvoicesController(db, email, agentId).Remind(overdueId, null);
        var reminder = Assert.Single(email.Sent);
        Assert.Contains(" from Acme Planning is overdue", reminder.Subject);
        Assert.Equal("advisor@acme.test", reminder.ReplyTo);
        Assert.Equal("Acme Planning", reminder.ReplyToName);

        // The schedule, on another invoice eight days overdue (the 7-day stage, on by default; the one
        // reminded by hand above is inside its five-day gap and stays quiet).
        await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Sent, DateTime.UtcNow.Date.AddDays(-8));
        email = new RecordingEmail();
        await NewReminderJob(db, email).RunAsync();
        var scheduled = Assert.Single(email.Sent);
        Assert.StartsWith("Reminder: Invoice ", scheduled.Subject);
        Assert.EndsWith(" from Acme Planning is overdue", scheduled.Subject);
        Assert.Equal("advisor@acme.test", scheduled.ReplyTo);
        Assert.Equal("Acme Planning", scheduled.ReplyToName);
    }

    // -- helpers -------------------------------------------------------------------------------------

    private static IPRO.Scheduler.OverdueInvoiceReminderJob NewReminderJob(IPRODbContext db, IEmailService email) =>
        new(db, new GrantAll(), email, new ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IPRO.Scheduler.OverdueInvoiceReminderJob>.Instance);

    private static IPRO.Web.Controllers.ClientInvoicesController NewInvoicesController(IPRODbContext db, IEmailService email, int agentId)
    {
        var controller = new IPRO.Web.Controllers.ClientInvoicesController(
            db, new GrantAll(), new IPRO.Business.Services.ClientInvoiceService(new IPRO.DataAccess.Repositories.UnitOfWork(db)), email,
            new ConfigurationBuilder().Build());
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string company, string emailAddress)
    {
        var rule = new BillingRule { PackageName = ($"T530-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t530-{Guid.NewGuid():N}")[..20],
            Email = emailAddress,
            FirstName = "Sender", LastName = "Agent", CompanyName = company,
            DomainName = ($"t530-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> SeedInvoiceAsync(IPRODbContext db, int agentId, ClientInvoiceStatus status, DateTime dueDate)
    {
        var client = new Client { AgentUserId = agentId, FirstName = "Cli", LastName = "Ent", Email = $"{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var invoice = new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id,
            DocumentType = ClientInvoiceDocumentType.Invoice, Status = status,
            DocumentNumber = ($"A-{Guid.NewGuid():N}")[..20], Total = 250m, Currency = "CAD",
            ViewToken = Guid.NewGuid().ToString("N"),
            IssueDate = dueDate.AddDays(-14),
            DueDate = dueDate,
            SentAt = status == ClientInvoiceStatus.Draft ? null : DateTime.UtcNow.AddDays(-15)
        };
        db.ClientInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.Id;
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, string Subject, string? ReplyTo, string? ReplyToName)> Sent { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            Sent.Add((toEmail, subject, replyToEmail, replyToName));
            return Task.FromResult(EmailSendResult.Sent($"acs-{Sent.Count}"));
        }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class GrantAll : IPRO.Business.Interfaces.IPackageEntitlementService
    {
        public Task<IPRO.Business.Interfaces.PackageFeatureAccess> GetAccessAsync(int agentId, string featureCode) =>
            Task.FromResult(new IPRO.Business.Interfaces.PackageFeatureAccess { FeatureCode = featureCode, IsIncluded = true });
        public Task<bool> HasAccessAsync(int agentId, string featureCode) => Task.FromResult(true);
        public Task<Dictionary<int, bool>> HasAccessBulkAsync(IEnumerable<int> agentIds, string featureCode) =>
            Task.FromResult(agentIds.Distinct().ToDictionary(a => a, _ => true));
        public Task<bool> IsAccessGatedAsync(int agentId) => Task.FromResult(false);
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    // Each SendDetailedAsync / SendAsync call, from the call to the end of its statement.
    private static List<string> SendCalls(string source)
    {
        var calls = new List<string>();
        var index = 0;
        while (true)
        {
            var detailed = source.IndexOf(".SendDetailedAsync(", index, StringComparison.Ordinal);
            var plain = source.IndexOf("_email.SendAsync(", index, StringComparison.Ordinal);
            var start = detailed < 0 ? plain : plain < 0 ? detailed : Math.Min(detailed, plain);
            if (start < 0) return calls;
            var end = source.IndexOf(");", start, StringComparison.Ordinal);
            if (end < 0) return calls;
            calls.Add(source[start..(end + 2)]);
            index = end + 2;
        }
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, relative);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }
}
