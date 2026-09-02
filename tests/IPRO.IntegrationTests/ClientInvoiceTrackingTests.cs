using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
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

// TODO 452 (2026-09-02). "Do we track invoices that are sent? I think we should." We did not:
// ClientInvoicesController.Send stamped Status=Sent BEFORE the mail was attempted, discarded the
// provider's answer, showed "Invoice sent to x" regardless, stored no provider message id (so the
// delivery pipeline could not correlate Delivered/Bounced), and the public document page recorded
// nothing when the client opened it. Every test here runs the real controller, job, tracker or
// query against a real MySQL database.
public class ClientInvoiceTrackingTests
{
    // ---- the send is recorded, and its outcome is the truth -----------------------------------

    [Fact]
    public async Task Sending_records_the_email_with_its_provider_id_and_the_invoice_becomes_sent()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (invoiceId, clientEmail) = await SeedInvoiceAsync(db, agentId, "INV-T1", ClientInvoiceStatus.Draft);

        var email = new RecordingEmail { Next = _ => EmailSendResult.Sent("acs-inv-1") };
        var controller = NewInvoicesController(db, email, agentId);
        await controller.Send(invoiceId);

        db.ChangeTracker.Clear();
        var row = Assert.Single(await db.ClientInvoiceEmails.AsNoTracking().Where(e => e.ClientInvoiceId == invoiceId).ToListAsync());
        Assert.Equal(ClientInvoiceEmailKind.Send, row.Kind);
        Assert.Equal(ClientInvoiceEmailStatus.Sent, row.Status);
        Assert.Equal("acs-inv-1", row.ProviderMessageId);
        Assert.Equal(clientEmail, row.ToEmail);
        Assert.Equal(agentId, row.AgentUserId);
        Assert.NotNull(row.SentAt);

        var invoice = await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
        Assert.Equal(ClientInvoiceStatus.Sent, invoice.Status);
        Assert.NotNull(invoice.SentAt);
        Assert.Contains(clientEmail, (string)controller.TempData["Success"]!);
    }

    [Fact]
    public async Task A_failed_send_is_recorded_with_its_reason_and_the_invoice_stays_a_draft()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (invoiceId, clientEmail) = await SeedInvoiceAsync(db, agentId, "INV-T2", ClientInvoiceStatus.Draft);

        var email = new RecordingEmail { Next = _ => EmailSendResult.Failed("Mailbox does not exist") };
        var controller = NewInvoicesController(db, email, agentId);
        await controller.Send(invoiceId);

        db.ChangeTracker.Clear();
        var row = Assert.Single(await db.ClientInvoiceEmails.AsNoTracking().Where(e => e.ClientInvoiceId == invoiceId).ToListAsync());
        Assert.Equal(ClientInvoiceEmailStatus.Failed, row.Status);
        Assert.Contains("Mailbox does not exist", row.FailureReason);
        Assert.NotNull(row.FailedAt);

        // The invoice was NOT sent, so it must not claim to be: it stays a draft, with the
        // Send button still available, and the agent is told why.
        var invoice = await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
        Assert.Equal(ClientInvoiceStatus.Draft, invoice.Status);
        Assert.Null(invoice.SentAt);
        Assert.Null(controller.TempData["Success"]);
        var error = (string)controller.TempData["Error"]!;
        Assert.Contains("Mailbox does not exist", error);
        Assert.Contains(clientEmail, error);
    }

    // ---- the provider's delivery report lands on the invoice's email ---------------------------

    [Fact]
    public async Task A_delivery_report_for_an_invoice_email_lands_on_its_row()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (invoiceId, _) = await SeedInvoiceAsync(db, agentId, "INV-T3", ClientInvoiceStatus.Sent);
        var emailId = await SeedSentEmailRowAsync(db, invoiceId, "acs-inv-2");

        var result = await PostAcsEventAsync(db, DeliveryEvent("acs-inv-2", "Delivered", ""));
        Assert.IsType<OkResult>(result);

        db.ChangeTracker.Clear();
        var row = await db.ClientInvoiceEmails.AsNoTracking().SingleAsync(e => e.Id == emailId);
        Assert.NotNull(row.DeliveredAt);
        Assert.Equal(ClientInvoiceEmailStatus.Delivered, row.Status);
        Assert.Equal("delivered", row.LastEvent);
    }

    [Fact]
    public async Task A_hard_bounce_marks_the_invoice_email_and_suppresses_the_client()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (invoiceId, _) = await SeedInvoiceAsync(db, agentId, "INV-T4", ClientInvoiceStatus.Sent);
        var emailId = await SeedSentEmailRowAsync(db, invoiceId, "acs-inv-3");

        var result = await PostAcsEventAsync(db, DeliveryEvent("acs-inv-3", "Bounced", "550 5.1.1 no such user"));
        Assert.IsType<OkResult>(result);

        db.ChangeTracker.Clear();
        var row = await db.ClientInvoiceEmails.AsNoTracking().SingleAsync(e => e.Id == emailId);
        Assert.NotNull(row.BouncedAt);
        Assert.Equal(ClientInvoiceEmailStatus.Bounced, row.Status);
        Assert.Contains("550", row.FailureReason);

        // The same protection every other channel has: a hard bounce suppresses the address.
        var clientId = (await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).ClientId;
        var client = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId);
        Assert.NotNull(client.EmailOptOutAt);
    }

    // ---- the client opening the invoice is the strongest signal, and it is free -----------------

    [Fact]
    public async Task The_client_opening_the_invoice_stamps_the_view()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (invoiceId, _) = await SeedInvoiceAsync(db, agentId, "INV-T5", ClientInvoiceStatus.Sent);
        var token = (await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).ViewToken;

        var controller = new IPRO.Web.Controllers.ClientDocumentController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        Assert.IsType<ViewResult>(await controller.Show(token));
        db.ChangeTracker.Clear();
        Assert.IsType<ViewResult>(await controller.Show(token));

        db.ChangeTracker.Clear();
        var invoice = await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
        Assert.NotNull(invoice.FirstViewedAt);
        Assert.NotNull(invoice.LastViewedAt);
        Assert.Equal(2, invoice.ViewCount);
        Assert.True(invoice.LastViewedAt >= invoice.FirstViewedAt);
    }

    [Fact]
    public async Task The_agent_previewing_their_own_invoice_is_not_a_view()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (invoiceId, _) = await SeedInvoiceAsync(db, agentId, "INV-T6", ClientInvoiceStatus.Sent);
        var token = (await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).ViewToken;

        var ctx = new DefaultHttpContext { User = AgentPrincipal(agentId) };
        var controller = new IPRO.Web.Controllers.ClientDocumentController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx }
        };
        Assert.IsType<ViewResult>(await controller.Show(token));

        db.ChangeTracker.Clear();
        var invoice = await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
        Assert.Null(invoice.FirstViewedAt);
        Assert.Equal(0, invoice.ViewCount);
    }

    // ---- the overdue reminder is an invoice email too ------------------------------------------

    [Fact]
    public async Task The_overdue_reminder_records_its_email()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (invoiceId, clientEmail) = await SeedInvoiceAsync(db, agentId, "INV-T7", ClientInvoiceStatus.Sent, dueDaysAgo: 3);
        db.ChangeTracker.Clear();

        var email = new RecordingEmail { Next = _ => EmailSendResult.Sent("acs-rem-1") };
        await NewReminderJob(db, email).RunAsync();

        db.ChangeTracker.Clear();
        var row = Assert.Single(await db.ClientInvoiceEmails.AsNoTracking().Where(e => e.ClientInvoiceId == invoiceId).ToListAsync());
        Assert.Equal(ClientInvoiceEmailKind.Reminder, row.Kind);
        Assert.Equal(ClientInvoiceEmailStatus.Sent, row.Status);
        Assert.Equal("acs-rem-1", row.ProviderMessageId);
        Assert.Equal(clientEmail, row.ToEmail);
        Assert.NotNull((await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).LastReminderSentAt);
    }

    [Fact]
    public async Task A_permanently_failed_reminder_waits_the_normal_interval_but_a_transient_one_retries()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (permanentId, permanentEmail) = await SeedInvoiceAsync(db, agentId, "INV-T8A", ClientInvoiceStatus.Sent, dueDaysAgo: 3);
        var (transientId, _) = await SeedInvoiceAsync(db, agentId, "INV-T8B", ClientInvoiceStatus.Sent, dueDaysAgo: 3);
        db.ChangeTracker.Clear();

        var email = new RecordingEmail
        {
            Next = to => to == permanentEmail
                ? EmailSendResult.Failed("Address rejected")
                : EmailSendResult.FailedTransient("Timed out")
        };
        await NewReminderJob(db, email).RunAsync();

        db.ChangeTracker.Clear();
        var rows = await db.ClientInvoiceEmails.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ClientInvoiceEmailStatus.Failed, r.Status));
        Assert.Contains("Address rejected", rows.Single(r => r.ClientInvoiceId == permanentId).FailureReason);
        Assert.Contains("Timed out", rows.Single(r => r.ClientInvoiceId == transientId).FailureReason);

        // A permanent rejection must not be retried every run (each attempt is a bounce against
        // our sending reputation): it waits the normal interval. A transient failure retries.
        Assert.NotNull((await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == permanentId)).LastReminderSentAt);
        Assert.Null((await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.Id == transientId)).LastReminderSentAt);
    }

    // ---- invoices show up in Email Activity ----------------------------------------------------

    [Fact]
    public async Task Invoice_emails_appear_in_email_activity_for_their_agent_only()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var (invoiceId, clientEmail) = await SeedInvoiceAsync(db, agentId, "INV-T9", ClientInvoiceStatus.Sent);
        var deliveredId = await SeedSentEmailRowAsync(db, invoiceId, "acs-act-1", delivered: true);
        await SeedSentEmailRowAsync(db, invoiceId, "", failed: true);

        var otherAgent = await SeedAgentAsync(db);
        var (otherInvoice, _) = await SeedInvoiceAsync(db, otherAgent, "INV-T9X", ClientInvoiceStatus.Sent);
        await SeedSentEmailRowAsync(db, otherInvoice, "acs-act-9", delivered: true);

        var rows = await IPRO.Web.Infrastructure.EmailActivityQueries.InvoiceRowsAsync(db, agentId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("invoice", r.TypeKey));
        Assert.All(rows, r => Assert.Contains("INV-T9", r.Subject));
        var delivered = rows.Single(r => r.Id == deliveredId);
        Assert.Equal(1, delivered.Sent);
        Assert.Equal(1, delivered.Delivered);
        Assert.Equal(0, delivered.Failed);
        Assert.Equal(1, rows.Single(r => r.Id != deliveredId).Failed);

        var recipients = await IPRO.Web.Infrastructure.EmailActivityQueries.InvoiceRecipientsAsync(db, agentId, deliveredId);
        var recipient = Assert.Single(recipients);
        Assert.Equal(clientEmail, recipient.Email);
        Assert.NotNull(recipient.DeliveredAt);

        // Another agent's invoice email is not reachable through the detail query either.
        Assert.Empty(await IPRO.Web.Infrastructure.EmailActivityQueries.InvoiceRecipientsAsync(db, otherAgent, deliveredId));
    }

    // ---- pins: the pieces a later edit could silently detach -----------------------------------

    [Fact]
    public void The_delivery_pipeline_knows_invoice_emails()
    {
        var tracker = File.ReadAllText(FindRepoFile(@"src\IPRO.Business\Services\EmailDeliveryTracker.cs"));
        Assert.Contains("case \"invoice\":", tracker);

        var resolver = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\AzureEmailEventsController.cs"));
        Assert.Contains("ClientInvoiceEmails", resolver);
        Assert.Contains("TrackedKind.Invoice", resolver);
    }

    [Fact]
    public void The_agent_sees_delivery_and_views_on_the_invoice_and_can_resend()
    {
        var partial = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\ClientInvoices\_ClientInvoiceDocument.cshtml"));
        Assert.Contains("Viewed", partial);
        Assert.Contains("Not viewed yet", partial);
        Assert.Contains("Resend", partial);
        Assert.Contains("ClientInvoices/Send/", partial);

        var index = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\ClientInvoices\Index.cshtml"));
        Assert.Contains("Delivery", index);

        var activity = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\EmailActivity\Index.cshtml"));
        Assert.Contains("(\"invoice\", \"Invoices\")", activity);
    }

    [Fact]
    public void Production_gets_the_table_and_the_columns_at_startup()
    {
        var repair = File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        Assert.Contains("CREATE TABLE IF NOT EXISTS `ClientInvoiceEmails`", repair);
        Assert.Contains("ON DELETE CASCADE", repair);
        foreach (var column in new[] { "FirstViewedAt", "LastViewedAt", "ViewCount" })
            Assert.Contains($"ALTER TABLE `ClientInvoices` ADD COLUMN `{column}`", repair);

        // Deleting an agent must take the log with the invoices (the eraser deletes by raw SQL).
        var eraser = File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\AgentDataEraser.cs"));
        Assert.Contains("\"ClientInvoiceEmails\"", eraser);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static IPRO.Web.Controllers.ClientInvoicesController NewInvoicesController(IPRODbContext db, IEmailService email, int agentId)
    {
        var controller = new IPRO.Web.Controllers.ClientInvoicesController(
            db, new GrantAll(), new IPRO.Business.Services.ClientInvoiceService(new IPRO.DataAccess.Repositories.UnitOfWork(db)), email,
            new ConfigurationBuilder().Build());
        var ctx = new DefaultHttpContext { User = AgentPrincipal(agentId) };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static IPRO.Scheduler.OverdueInvoiceReminderJob NewReminderJob(IPRODbContext db, IEmailService email) =>
        new(db, new GrantAll(), email, new ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IPRO.Scheduler.OverdueInvoiceReminderJob>.Instance);

    private static async Task<IActionResult> PostAcsEventAsync(IPRODbContext db, string body)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Email:AzureEventWebhookSecret"] = "s3cret" })
            .Build();
        var consent = new IPRO.Business.Services.EmailConsentService(
            db, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<IPRO.Business.Services.EmailConsentService>.Instance,
            Array.Empty<IPRO.Business.Services.IUnsubscribeNotifier>());
        var uow = new IPRO.DataAccess.Repositories.UnitOfWork(db);
        var controller = new IPRO.Web.Controllers.AzureEmailEventsController(
            db,
            new IPRO.Business.Services.NewsLetterService(uow, consent, db),
            new IPRO.Business.Services.EmailDeliveryTracker(db,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<IPRO.Business.Services.EmailDeliveryTracker>.Instance, consent),
            consent, config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IPRO.Web.Controllers.AzureEmailEventsController>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?secret=s3cret");
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Request.ContentLength = body.Length;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return await controller.Index();
    }

    private static string DeliveryEvent(string messageId, string status, string detail) =>
        "[{\"eventType\":\"Microsoft.Communication.EmailDeliveryReportReceived\",\"data\":{" +
        $"\"messageId\":\"{messageId}\",\"status\":\"{status}\"," +
        "\"deliveryAttemptTimeStamp\":\"2026-09-02T18:00:00Z\"," +
        $"\"deliveryStatusDetails\":{{\"statusMessage\":\"{detail}\"}}}}}}]";

    private static ClaimsPrincipal AgentPrincipal(int agentId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"));

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T452-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t452-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Tracked",
            LastName = "Agent",
            CompanyName = "Tracked Co",
            DomainName = ($"t452-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<(int InvoiceId, string ClientEmail)> SeedInvoiceAsync(
        IPRODbContext db, int agentId, string number, ClientInvoiceStatus status, int? dueDaysAgo = null)
    {
        var client = new Client
        {
            AgentUserId = agentId, FirstName = "Cli", LastName = number,
            Email = $"{number.ToLowerInvariant()}-{Guid.NewGuid():N}@example.test"
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var invoice = new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id,
            DocumentType = ClientInvoiceDocumentType.Invoice, Status = status,
            DocumentNumber = number, Total = 100m, Currency = "CAD",
            ViewToken = Guid.NewGuid().ToString("N"),
            DueDate = dueDaysAgo.HasValue ? DateTime.UtcNow.Date.AddDays(-dueDaysAgo.Value) : DateTime.UtcNow.Date.AddDays(15),
            SentAt = status == ClientInvoiceStatus.Sent ? DateTime.UtcNow.AddDays(-5) : null
        };
        db.ClientInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return (invoice.Id, client.Email);
    }

    private static async Task<int> SeedSentEmailRowAsync(IPRODbContext db, int invoiceId, string providerMessageId, bool delivered = false, bool failed = false)
    {
        var invoice = await db.ClientInvoices.AsNoTracking().Include(i => i.Client).SingleAsync(i => i.Id == invoiceId);
        var now = DateTime.UtcNow;
        var row = new ClientInvoiceEmail
        {
            ClientInvoiceId = invoiceId, AgentUserId = invoice.AgentUserId, ClientId = invoice.ClientId,
            Kind = ClientInvoiceEmailKind.Send, ToEmail = invoice.Client.Email,
            Subject = $"Tracked Co sent you invoice {invoice.DocumentNumber}",
            ProviderMessageId = providerMessageId,
            Status = failed ? ClientInvoiceEmailStatus.Failed : delivered ? ClientInvoiceEmailStatus.Delivered : ClientInvoiceEmailStatus.Sent,
            SentAt = failed ? null : now.AddMinutes(-10),
            DeliveredAt = delivered ? now.AddMinutes(-9) : null,
            FailedAt = failed ? now.AddMinutes(-10) : null,
            FailureReason = failed ? "Address rejected" : string.Empty,
            LastEvent = failed ? "failed" : delivered ? "delivered" : "sent"
        };
        db.ClientInvoiceEmails.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return row.Id;
    }

    private sealed class RecordingEmail : IEmailService
    {
        public Func<string, EmailSendResult> Next { get; set; } = _ => EmailSendResult.Sent();
        public List<(string To, string Subject)> Sent { get; } = new();
        public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            (await SendDetailedAsync(toEmail, toName, subject, htmlBody, textBody, customArgs, replyToEmail, replyToName, listUnsubscribeUrl)).Success;
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null, IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null)
        { Sent.Add((toEmail, subject)); return Task.FromResult(Next(toEmail)); }
        public Task<bool> SendBulkAsync(IEnumerable<EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
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

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
