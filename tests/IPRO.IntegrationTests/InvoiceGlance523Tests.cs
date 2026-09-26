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
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 523 (2026-09-26), slice 1: the adviser's money at a glance on the invoices page and the
// dashboard -- outstanding, overdue, paid this month against last month, average days to pay.
// The numbers come from a pure calculator over the invoices already there, so every one is
// pinned here without a clock; the invoices page is then driven for real against MySQL.
public class InvoiceGlance523Tests
{
    private static readonly string Zone = AgentLocalTime.DefaultTimeZone;   // Toronto
    private static readonly DateTime Today = new(2026, 9, 26);

    private static ClientInvoice Invoice(ClientInvoiceStatus status, decimal total, DateTime? due = null,
        DateTime? paidAtUtc = null, DateTime? issued = null, ClientInvoiceDocumentType type = ClientInvoiceDocumentType.Invoice) => new()
    {
        DocumentType = type, Status = status, Total = total, DueDate = due, PaidAt = paidAtUtc,
        IssueDate = issued ?? new DateTime(2026, 9, 1), Currency = "CAD"
    };

    // ---- the calculator ------------------------------------------------------------------------------

    [Fact]
    public void Outstanding_is_what_clients_still_owe_on_invoices()
    {
        var glance = ClientInvoiceGlance.From(new[]
        {
            Invoice(ClientInvoiceStatus.Sent, 100m),
            Invoice(ClientInvoiceStatus.Approved, 250m),
            Invoice(ClientInvoiceStatus.Draft, 999m),                                       // not issued yet
            Invoice(ClientInvoiceStatus.Void, 999m),                                        // never owed
            Invoice(ClientInvoiceStatus.Paid, 999m, paidAtUtc: new DateTime(2026, 9, 10, 15, 0, 0, DateTimeKind.Utc)),
            Invoice(ClientInvoiceStatus.Sent, 999m, type: ClientInvoiceDocumentType.Estimate)  // an offer, not money
        }, Today, Zone);

        Assert.Equal(350m, glance.Outstanding);
        Assert.Equal(2, glance.OutstandingCount);
        Assert.Equal("CAD", glance.Currency);
        Assert.Equal(3, glance.InvoiceCount);
        Assert.True(glance.HasInvoices);
    }

    [Fact]
    public void Overdue_is_the_outstanding_past_its_due_date()
    {
        var glance = ClientInvoiceGlance.From(new[]
        {
            Invoice(ClientInvoiceStatus.Sent, 100m, due: Today.AddDays(-1)),   // yesterday: overdue
            Invoice(ClientInvoiceStatus.Sent, 200m, due: Today),               // due today: not yet
            Invoice(ClientInvoiceStatus.Sent, 400m),                           // no due date: never overdue
            Invoice(ClientInvoiceStatus.Paid, 800m, due: Today.AddDays(-30),   // paid: nothing owed
                paidAtUtc: new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc))
        }, Today, Zone);

        Assert.Equal(700m, glance.Outstanding);
        Assert.Equal(100m, glance.Overdue);
        Assert.Equal(1, glance.OverdueCount);
    }

    [Fact]
    public void Paid_this_month_and_last_month_follow_the_advisers_own_calendar()
    {
        var glance = ClientInvoiceGlance.From(new[]
        {
            Invoice(ClientInvoiceStatus.Paid, 100m, paidAtUtc: new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc)),  // this month
            Invoice(ClientInvoiceStatus.Paid, 20m, paidAtUtc: new DateTime(2026, 9, 1, 3, 30, 0, DateTimeKind.Utc)),    // 31 Aug, 11:30 p.m. in Toronto: LAST month
            Invoice(ClientInvoiceStatus.Paid, 5m, paidAtUtc: new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc)),    // last month
            Invoice(ClientInvoiceStatus.Paid, 999m, paidAtUtc: new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc))   // older
        }, Today, Zone);

        Assert.Equal(100m, glance.PaidThisMonth);
        Assert.Equal(1, glance.PaidThisMonthCount);
        Assert.Equal(25m, glance.PaidLastMonth);
        Assert.Equal(2, glance.PaidLastMonthCount);
    }

    [Fact]
    public void Average_days_to_pay_covers_the_last_twelve_months()
    {
        var glance = ClientInvoiceGlance.From(new[]
        {
            Invoice(ClientInvoiceStatus.Paid, 100m, issued: new DateTime(2026, 9, 1), paidAtUtc: new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc)),  // 10 days
            Invoice(ClientInvoiceStatus.Paid, 100m, issued: new DateTime(2026, 9, 1), paidAtUtc: new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc)),  // 20 days
            Invoice(ClientInvoiceStatus.Paid, 100m, issued: new DateTime(2025, 6, 1), paidAtUtc: new DateTime(2025, 9, 1, 12, 0, 0, DateTimeKind.Utc)),   // over a year ago: out
            Invoice(ClientInvoiceStatus.Sent, 100m)                                                                                                       // unpaid: no days yet
        }, Today, Zone);

        Assert.Equal(15, glance.AverageDaysToPay);
        Assert.Equal(2, glance.PaidInLastYearCount);
        Assert.Null(ClientInvoiceGlance.From(new[] { Invoice(ClientInvoiceStatus.Sent, 100m) }, Today, Zone).AverageDaysToPay);
    }

    [Fact]
    public void Nothing_yet_is_said_plainly()
    {
        var glance = ClientInvoiceGlance.From(new[]
        {
            Invoice(ClientInvoiceStatus.Draft, 100m),
            Invoice(ClientInvoiceStatus.Sent, 100m, type: ClientInvoiceDocumentType.Estimate)
        }, Today, Zone);

        Assert.False(glance.HasInvoices);
        Assert.Equal(0m, glance.Outstanding);
        Assert.Null(glance.AverageDaysToPay);
    }

    // ---- the pages carry it --------------------------------------------------------------------------

    [Fact]
    public void The_invoices_page_and_the_dashboard_carry_the_glance()
    {
        Assert.Contains("ClientInvoiceGlance.From(", Read(@"src\IPRO.Web\Controllers\ClientInvoicesController.cs"));
        var dashboard = Read(@"src\IPRO.Web\Controllers\DashboardController.cs");
        Assert.Contains("ClientInvoiceGlance.From(", dashboard);
        Assert.Contains("PackageFeatureCodes.ClientInvoicing", dashboard);          // only where the package includes invoicing
        Assert.Contains("ViewBag.Glance as IPRO.DataAccess.ClientInvoiceGlance", Read(@"src\IPRO.Web\Views\ClientInvoices\Index.cshtml"));
        Assert.Contains("ViewBag.Glance as IPRO.DataAccess.ClientInvoiceGlance", Read(@"src\IPRO.Web\Views\Dashboard\Index.cshtml"));
    }

    // ---- the invoices page, for real -----------------------------------------------------------------

    [Fact]
    public async Task The_invoices_page_computes_the_glance_for_the_signed_in_adviser_only()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var otherId = await SeedAgentAsync(db);
        await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Sent, 100m, dueDaysAgo: 3);
        await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Sent, 250m);
        await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Paid, 80m, paidAtUtc: DateTime.UtcNow.AddHours(-2));
        await SeedInvoiceAsync(db, agentId, ClientInvoiceStatus.Void, 999m);
        await SeedInvoiceAsync(db, otherId, ClientInvoiceStatus.Sent, 999m, dueDaysAgo: 40);     // another adviser's money

        var controller = NewInvoicesController(db, agentId);
        Assert.IsType<ViewResult>(await controller.Index());

        var glance = Assert.IsType<ClientInvoiceGlance>(controller.ViewData["Glance"]);
        Assert.Equal(350m, glance.Outstanding);
        Assert.Equal(2, glance.OutstandingCount);
        Assert.Equal(100m, glance.Overdue);
        Assert.Equal(1, glance.OverdueCount);
        Assert.Equal(80m, glance.PaidThisMonth + glance.PaidLastMonth);   // two hours ago is this month, or last month within its first hours
        Assert.Equal(3, glance.InvoiceCount);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static IPRO.Web.Controllers.ClientInvoicesController NewInvoicesController(IPRODbContext db, int agentId)
    {
        var controller = new IPRO.Web.Controllers.ClientInvoicesController(
            db, new GrantAll(), new IPRO.Business.Services.ClientInvoiceService(new IPRO.DataAccess.Repositories.UnitOfWork(db)), new NoEmail(),
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
            UserName = ($"t523-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Glance",
            LastName = "Agent",
            CompanyName = "Glance Co",
            DomainName = ($"t523-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task SeedInvoiceAsync(IPRODbContext db, int agentId, ClientInvoiceStatus status, decimal total,
        int? dueDaysAgo = null, DateTime? paidAtUtc = null)
    {
        var client = new Client
        {
            AgentUserId = agentId, FirstName = "Cli", LastName = status.ToString(),
            Email = $"{Guid.NewGuid():N}@example.test"
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        db.ClientInvoices.Add(new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id,
            DocumentType = ClientInvoiceDocumentType.Invoice, Status = status,
            DocumentNumber = ($"G-{Guid.NewGuid():N}")[..20], Total = total, Currency = "CAD",
            ViewToken = Guid.NewGuid().ToString("N"),
            IssueDate = DateTime.UtcNow.Date.AddDays(-10),
            DueDate = dueDaysAgo.HasValue ? DateTime.UtcNow.Date.AddDays(-dueDaysAgo.Value) : DateTime.UtcNow.Date.AddDays(15),
            SentAt = status == ClientInvoiceStatus.Sent ? DateTime.UtcNow.AddDays(-5) : null,
            PaidAt = paidAtUtc
        });
        await db.SaveChangesAsync();
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

    private sealed class NoEmail : IEmailService
    {
        public Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            Task.FromResult(true);
        public Task<EmailSendResult> SendDetailedAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null,
            IDictionary<string, string>? customArgs = null, string? replyToEmail = null, string? replyToName = null, string? listUnsubscribeUrl = null) =>
            Task.FromResult(EmailSendResult.Sent());
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
