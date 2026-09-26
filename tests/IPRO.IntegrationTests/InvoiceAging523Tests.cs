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

// 523 (2026-09-26), slice 2: an aging view per client (current, 1-30, 31-60, 61-90, over 90 days
// past due) with a one-click reminder, an "overdue" filter on the invoices list, and the nightly
// job's reminder email shared with the button so the client reads the same words either way.
// The buckets come from a pure calculator, pinned here without a clock; the page, the button and
// the filter are then driven for real against MySQL.
public class InvoiceAging523Tests
{
    private static readonly DateTime Today = new(2026, 9, 26);

    private static ClientInvoice Doc(int clientId, string number, decimal total, int? daysPastDue,
        ClientInvoiceStatus status = ClientInvoiceStatus.Sent, ClientInvoiceDocumentType type = ClientInvoiceDocumentType.Invoice) => new()
    {
        ClientId = clientId,
        Client = new Client { Id = clientId, FirstName = "Client", LastName = clientId.ToString(), Email = $"c{clientId}@example.test" },
        DocumentType = type, Status = status, DocumentNumber = number, Total = total, Currency = "CAD",
        DueDate = daysPastDue.HasValue ? Today.AddDays(-daysPastDue.Value) : null
    };

    // ---- the calculator ------------------------------------------------------------------------------

    [Fact]
    public void Buckets_follow_the_days_past_due()
    {
        Assert.Equal(0, ClientInvoiceAging.Bucket(0));
        Assert.Equal(1, ClientInvoiceAging.Bucket(1));
        Assert.Equal(1, ClientInvoiceAging.Bucket(30));
        Assert.Equal(2, ClientInvoiceAging.Bucket(31));
        Assert.Equal(2, ClientInvoiceAging.Bucket(60));
        Assert.Equal(3, ClientInvoiceAging.Bucket(61));
        Assert.Equal(3, ClientInvoiceAging.Bucket(90));
        Assert.Equal(4, ClientInvoiceAging.Bucket(91));

        var aging = ClientInvoiceAging.From(new[]
        {
            Doc(1, "A", 100m, -5),      // due in five days: current
            Doc(1, "B", 200m, 1),       // yesterday
            Doc(1, "C", 300m, 30),
            Doc(1, "D", 400m, 31),
            Doc(1, "E", 500m, 90),
            Doc(1, "F", 600m, 91),
            Doc(1, "G", 700m, null)     // no due date: current, never overdue
        }, Today);

        var row = Assert.Single(aging.Rows);
        Assert.Equal(800m, row.Current);
        Assert.Equal(500m, row.Days1To30);
        Assert.Equal(400m, row.Days31To60);
        Assert.Equal(500m, row.Days61To90);
        Assert.Equal(600m, row.Over90);
        Assert.Equal(2800m, row.Total);
        Assert.Equal(2000m, row.PastDue);
        Assert.Equal(5, row.OverdueCount);
        Assert.Equal(row.Current, aging.Current);
        Assert.Equal(row.Over90, aging.Over90);
        Assert.Equal(2800m, aging.Total);
        Assert.Equal(7, aging.InvoiceCount);
        Assert.Equal("Client 1", row.ClientName);
        Assert.Equal("c1@example.test", row.ClientEmail);

        var first = row.Invoices.First();                   // the most overdue first
        Assert.Equal("F", first.DocumentNumber);
        Assert.Equal(91, first.DaysPastDue);
        Assert.True(first.IsOverdue);
        Assert.False(row.Invoices.Single(i => i.DocumentNumber == "G").IsOverdue);
    }

    [Fact]
    public void Only_unpaid_invoices_are_owed()
    {
        var aging = ClientInvoiceAging.From(new[]
        {
            Doc(1, "D", 100m, 10, ClientInvoiceStatus.Draft),
            Doc(1, "P", 100m, 10, ClientInvoiceStatus.Paid),
            Doc(1, "V", 100m, 10, ClientInvoiceStatus.Void),
            Doc(1, "X", 100m, 10, ClientInvoiceStatus.Declined),
            Doc(1, "E", 100m, 10, ClientInvoiceStatus.Sent, ClientInvoiceDocumentType.Estimate),
            Doc(2, "OK", 250m, 10, ClientInvoiceStatus.Approved)      // approved and unpaid: owed
        }, Today);

        var row = Assert.Single(aging.Rows);
        Assert.Equal(2, row.ClientId);
        Assert.Equal(250m, aging.Total);
        Assert.Equal(1, aging.InvoiceCount);
    }

    [Fact]
    public void Clients_are_grouped_with_the_longest_owed_money_first()
    {
        var aging = ClientInvoiceAging.From(new[]
        {
            Doc(1, "A1", 5000m, -3),    // a lot, but not yet due
            Doc(2, "B1", 100m, 5),
            Doc(2, "B2", 50m, 45),
            Doc(3, "C1", 10m, 100)
        }, Today);

        Assert.Equal(new[] { 2, 3, 1 }, aging.Rows.Select(r => r.ClientId).ToArray());
        Assert.Equal(150m, aging.Rows[0].Total);
        Assert.Equal(150m, aging.Rows[0].PastDue);
        Assert.Equal(0m, aging.Rows[2].PastDue);
        Assert.Equal(5160m, aging.Total);
    }

    // ---- the pages and the job carry it -------------------------------------------------------------

    [Fact]
    public void The_aging_page_the_overdue_filter_and_the_shared_reminder_are_wired()
    {
        var controller = Read(@"src\IPRO.Web\Controllers\ClientInvoicesController.cs");
        Assert.Contains("ClientInvoiceAging.From(", controller);
        Assert.Contains("public async Task<IActionResult> Remind(int id", controller);
        Assert.Contains("ClientInvoiceReminderEmail.Build(", controller);
        Assert.Contains("\"overdue\" =>", controller);

        Assert.Contains("ClientInvoiceReminderEmail.Build(", Read(@"src\IPRO.Scheduler\OverdueInvoiceReminderJob.cs"));

        var list = Read(@"src\IPRO.Web\Views\ClientInvoices\Index.cshtml");
        Assert.Contains("/portal/ClientInvoices/Aging", list);
        Assert.Contains("<option value=\"overdue\"", list);
        Assert.Contains("status=overdue", list);                     // the glance's "past due" link lands on exactly those

        Assert.Contains("/portal/ClientInvoices/Remind/", Read(@"src\IPRO.Web\Views\ClientInvoices\Aging.cshtml"));
    }

    // ---- the page, the button and the filter, for real ----------------------------------------------

    [Fact]
    public async Task The_aging_page_shows_only_the_signed_in_advisers_unpaid_invoices()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var otherId = await SeedAgentAsync(db);
        await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Sent, 100m, dueDaysAgo: 3);
        await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Sent, 250m, dueDaysAgo: -15);
        await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Paid, 80m, dueDaysAgo: 40, paidAtUtc: DateTime.UtcNow.AddDays(-1));
        await SeedInvoiceAsync(db, otherId, ClientInvoiceStatus.Sent, 999m, dueDaysAgo: 40);

        var controller = NewInvoicesController(db, new RecordingEmail(), agentId);
        var result = Assert.IsType<ViewResult>(await controller.Aging());
        var aging = Assert.IsType<ClientInvoiceAging>(result.Model);

        Assert.Equal(2, aging.Rows.Count);               // one client per seeded invoice
        Assert.Equal(350m, aging.Total);
        Assert.Equal(100m, aging.Days1To30);
        Assert.Equal(250m, aging.Current);
        Assert.Equal(0m, aging.Over90);
        Assert.Equal(100m, aging.Rows[0].Total);         // the overdue client first
    }

    [Fact]
    public async Task The_reminder_button_sends_the_nightly_jobs_email_and_never_twice_in_a_day()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (invoiceId, clientEmail) = await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Sent, 120m, dueDaysAgo: 5);

        var email = new RecordingEmail();
        var controller = NewInvoicesController(db, email, agentId);
        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Remind(invoiceId, "aging"));
        Assert.Equal("Aging", redirect.ActionName);

        var sent = Assert.Single(email.Sent);
        Assert.Equal(clientEmail, sent.To);
        Assert.StartsWith("Reminder: Invoice ", sent.Subject);
        Assert.EndsWith(" is overdue", sent.Subject);
        Assert.Contains("is now overdue", sent.Html);
        Assert.Contains("/invoice/", sent.Html);

        var stored = await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
        Assert.NotNull(stored.LastReminderSentAt);
        Assert.True(stored.LastReminderSentAt > DateTime.UtcNow.AddMinutes(-2));
        var log = Assert.Single(await db.ClientInvoiceEmails.AsNoTracking().Where(e => e.ClientInvoiceId == invoiceId).ToListAsync());
        Assert.Equal(ClientInvoiceEmailKind.Reminder, log.Kind);
        Assert.Contains("sent to", (string)controller.TempData["Success"]!);

        // The same button a minute later: refused, nothing sent, the adviser told when the last one went.
        controller.TempData.Clear();
        Assert.IsType<RedirectToActionResult>(await controller.Remind(invoiceId, "aging"));
        Assert.Single(email.Sent);
        Assert.Contains("already went", (string)controller.TempData["Error"]!);
    }

    [Fact]
    public async Task The_reminder_refuses_what_is_not_overdue_and_what_is_not_theirs()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var otherId = await SeedAgentAsync(db);
        var (notDueId, _) = await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Sent, 120m, dueDaysAgo: -10);
        var (paidId, _) = await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Paid, 120m, dueDaysAgo: 30, paidAtUtc: DateTime.UtcNow);
        var (othersId, _) = await SeedInvoiceAsync(db, otherId, ClientInvoiceStatus.Sent, 120m, dueDaysAgo: 30);

        var email = new RecordingEmail();
        var controller = NewInvoicesController(db, email, agentId);

        Assert.IsType<RedirectToActionResult>(await controller.Remind(notDueId));
        Assert.Contains("not overdue", (string)controller.TempData["Error"]!);
        controller.TempData.Clear();
        Assert.IsType<RedirectToActionResult>(await controller.Remind(paidId));
        Assert.Contains("not overdue", (string)controller.TempData["Error"]!);
        Assert.IsType<NotFoundResult>(await controller.Remind(othersId));
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task The_overdue_filter_lists_only_invoices_past_their_due_date()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (overdueId, _) = await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Sent, 100m, dueDaysAgo: 3);
        await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Sent, 250m, dueDaysAgo: -15);
        await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Paid, 80m, dueDaysAgo: 40, paidAtUtc: DateTime.UtcNow);
        await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Sent, 60m, dueDaysAgo: 40, type: ClientInvoiceDocumentType.Estimate);

        var controller = NewInvoicesController(db, new RecordingEmail(), agentId);
        var result = Assert.IsType<ViewResult>(await controller.Index(status: "overdue"));
        var listed = Assert.IsAssignableFrom<IEnumerable<ClientInvoice>>(result.Model).ToList();

        var only = Assert.Single(listed);
        Assert.Equal(overdueId, only.Id);
    }

    // ---- helpers -------------------------------------------------------------------------------------

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

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T523-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t523b-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Aging",
            LastName = "Agent",
            CompanyName = "Aging Co",
            DomainName = ($"t523b-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    // dueDaysAgo: positive is past due, negative is still to come.
    private static async Task<(int InvoiceId, string ClientEmail)> SeedInvoiceAsync(IPRODbContext db, int agentId, ClientInvoiceStatus status, decimal total,
        int dueDaysAgo, DateTime? paidAtUtc = null, ClientInvoiceDocumentType type = ClientInvoiceDocumentType.Invoice)
    {
        var client = new Client
        {
            AgentUserId = agentId, FirstName = "Cli", LastName = status.ToString(),
            Email = $"{Guid.NewGuid():N}@example.test"
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var invoice = new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id,
            DocumentType = type, Status = status,
            DocumentNumber = ($"G-{Guid.NewGuid():N}")[..20], Total = total, Currency = "CAD",
            ViewToken = Guid.NewGuid().ToString("N"),
            IssueDate = DateTime.UtcNow.Date.AddDays(-Math.Max(dueDaysAgo, 0) - 10),
            DueDate = DateTime.UtcNow.Date.AddDays(-dueDaysAgo),
            SentAt = status == ClientInvoiceStatus.Sent ? DateTime.UtcNow.AddDays(-5) : null,
            PaidAt = paidAtUtc
        };
        db.ClientInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return (invoice.Id, client.Email);
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

    private sealed class RecordingEmail : IEmailService
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        {
            Sent.Add((toEmail, subject, htmlBody));
            return Task.FromResult(EmailSendResult.Sent($"acs-{Sent.Count}"));
        }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> recipients, string subject, string htmlBody, string? textBody = null) =>
            Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string toEmail, string toName, string templateId, object templateData) => Task.FromResult(true);
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
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
