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
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 523 (2026-09-26), slice 4: the statement for the adviser's accountant -- what was invoiced in a
// month or a quarter, the tax collected by rate, what was received, what was still owed at the
// period's end -- with a CSV of the rows and a file in Xero's sales-invoice import layout. The
// numbers are pinned to the cent without a clock; the page and the files run against MySQL.
public class InvoiceStatement523Tests
{
    private static readonly string Zone = AgentLocalTime.DefaultTimeZone;   // Toronto

    private static ClientInvoice Doc(string number, DateTime issued, decimal subtotal, decimal rate, string region,
        ClientInvoiceStatus status = ClientInvoiceStatus.Sent, DateTime? paidAtUtc = null, ClientInvoiceDocumentType type = ClientInvoiceDocumentType.Invoice) => new()
    {
        DocumentNumber = number, DocumentType = type, Status = status, IssueDate = issued, DueDate = issued.AddDays(30),
        SubTotal = subtotal, TaxRegion = region, TaxRate = rate, TaxAmount = Math.Round(subtotal * rate, 2), Total = subtotal + Math.Round(subtotal * rate, 2),
        Currency = "CAD", PaidAt = paidAtUtc, PaidMethod = paidAtUtc.HasValue ? ClientInvoicePaymentMethod.EFT : null,
        Client = new Client { FirstName = "Cli", LastName = number }
    };

    private static List<ClientInvoice> September() => new()
    {
        Doc("A", new DateTime(2026, 9, 5), 100m, 0.13m, "Ontario HST"),
        Doc("B", new DateTime(2026, 9, 20), 200m, 0.05m, "GST", ClientInvoiceStatus.Paid, new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc)),
        Doc("C", new DateTime(2026, 8, 30), 300m, 0m, "", ClientInvoiceStatus.Paid, new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc)),
        Doc("D", new DateTime(2026, 9, 10), 999m, 0.13m, "Ontario HST", ClientInvoiceStatus.Draft),
        Doc("E", new DateTime(2026, 9, 12), 999m, 0.13m, "Ontario HST", ClientInvoiceStatus.Void),
        Doc("F", new DateTime(2026, 9, 15), 999m, 0.13m, "Ontario HST", ClientInvoiceStatus.Sent, type: ClientInvoiceDocumentType.Estimate),
        Doc("G", new DateTime(2026, 9, 28), 50m, 0.13m, "Ontario HST", ClientInvoiceStatus.Paid, new DateTime(2026, 10, 1, 3, 30, 0, DateTimeKind.Utc)),   // 30 Sept, 11:30 p.m. in Toronto
        Doc("H", new DateTime(2026, 7, 1), 400m, 0m, "")
    };

    // ---- the periods and the numbers ------------------------------------------------------------------

    [Fact]
    public void A_month_and_a_quarter_run_from_their_first_day_to_their_last()
    {
        Assert.Equal((new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)), ClientInvoiceStatement.Month(2026, 9));
        Assert.Equal((new DateTime(2026, 2, 1), new DateTime(2026, 2, 28)), ClientInvoiceStatement.Month(2026, 2));
        Assert.Equal((new DateTime(2026, 7, 1), new DateTime(2026, 9, 30)), ClientInvoiceStatement.Quarter(2026, 3));
        Assert.Equal((new DateTime(2026, 10, 1), new DateTime(2026, 12, 31)), ClientInvoiceStatement.Quarter(2026, 4));
    }

    [Fact]
    public void Invoiced_goes_by_issue_date_and_leaves_out_what_is_not_revenue()
    {
        var s = ClientInvoiceStatement.From(September(), new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), Zone);

        Assert.Equal(new[] { "A", "B", "G" }, s.Invoiced.Select(r => r.DocumentNumber).ToArray());
        Assert.Equal(350m, s.InvoicedSubtotal);
        Assert.Equal(29.5m, s.InvoicedTax);
        Assert.Equal(379.5m, s.InvoicedTotal);
        Assert.Equal("CAD", s.Currency);
        Assert.Equal(new DateTime(2026, 9, 1), s.Start);
        Assert.Equal(new DateTime(2026, 9, 30), s.End);
    }

    [Fact]
    public void Tax_is_totalled_by_rate()
    {
        var s = ClientInvoiceStatement.From(September(), new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), Zone);

        Assert.Equal(2, s.TaxByRate.Count);
        var hst = s.TaxByRate[0];
        Assert.Equal(0.13m, hst.Rate);
        Assert.Equal("Ontario HST", hst.Region);
        Assert.Equal("Ontario HST 13%", hst.Label);
        Assert.Equal(2, hst.Count);
        Assert.Equal(150m, hst.Subtotal);
        Assert.Equal(19.5m, hst.Tax);
        Assert.Equal(169.5m, hst.Total);
        var gst = s.TaxByRate[1];
        Assert.Equal(0.05m, gst.Rate);
        Assert.Equal(1, gst.Count);
        Assert.Equal(10m, gst.Tax);
        Assert.Equal("GST 5%", gst.Label);

        var q = ClientInvoiceStatement.From(September(), new DateTime(2026, 7, 1), new DateTime(2026, 9, 30), Zone);
        Assert.Equal("No tax", q.TaxByRate.Single(l => l.Rate == 0).Label);
        Assert.Equal(700m, q.TaxByRate.Single(l => l.Rate == 0).Subtotal);   // C and H
    }

    [Fact]
    public void Received_goes_by_the_day_the_payment_landed_in_the_advisers_own_zone()
    {
        var s = ClientInvoiceStatement.From(September(), new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), Zone);

        Assert.Equal(new[] { "C", "B", "G" }, s.Received.Select(r => r.DocumentNumber).ToArray());
        Assert.Equal(566.5m, s.ReceivedTotal);
        Assert.Equal(new DateTime(2026, 9, 30), s.Received.Single(r => r.DocumentNumber == "G").PaidOn);
        Assert.Equal(ClientInvoicePaymentMethod.EFT, s.Received[0].PaidMethod);

        var october = ClientInvoiceStatement.From(September(), new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), Zone);
        Assert.Empty(october.Received);
    }

    [Fact]
    public void Owed_at_the_end_is_what_was_issued_by_then_and_not_yet_paid()
    {
        var s = ClientInvoiceStatement.From(September(), new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), Zone);
        Assert.Equal(513m, s.OwedAtEnd);          // A (113) and H (400); B, C and G were paid by then
        Assert.Equal(2, s.OwedAtEndCount);

        var august = ClientInvoiceStatement.From(September(), new DateTime(2026, 8, 1), new DateTime(2026, 8, 31), Zone);
        Assert.Equal(700m, august.OwedAtEnd);     // C (300, paid in September) and H (400)
    }

    // ---- the page and the files ----------------------------------------------------------------------

    [Fact]
    public void The_statement_is_wired()
    {
        var controller = Read(@"src\IPRO.Web\Controllers\ClientInvoicesController.cs");
        Assert.Contains("ClientInvoiceStatement.From(", controller);
        Assert.Contains("public async Task<IActionResult> ExportXero(", controller);
        Assert.Contains("public async Task<IActionResult> ExportStatement(", controller);
        Assert.Contains("/portal/ClientInvoices/Statement", Read(@"src\IPRO.Web\Views\ClientInvoices\Index.cshtml"));
        Assert.Contains("## For Your Accountant", Read(@"DOCS\10_CLIENT_INVOICING.md"));
    }

    [Fact]
    public async Task The_statement_page_and_its_files_cover_the_signed_in_advisers_period_only()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var otherId = await SeedAgentAsync(db);
        await SeedInvoiceAsync(db, agentId, "S-1", new DateTime(2026, 9, 5), 100m, 0.13m, "Ontario HST", ClientInvoiceStatus.Sent, lines: 2);
        await SeedInvoiceAsync(db, agentId, "S-2", new DateTime(2026, 9, 20), 200m, 0.05m, "GST", ClientInvoiceStatus.Paid, new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc), lines: 1);
        await SeedInvoiceAsync(db, agentId, "S-3", new DateTime(2026, 8, 3), 300m, 0m, "", ClientInvoiceStatus.Sent, lines: 1);
        await SeedInvoiceAsync(db, otherId, "S-9", new DateTime(2026, 9, 9), 999m, 0.13m, "Ontario HST", ClientInvoiceStatus.Sent, lines: 1);

        var controller = NewInvoicesController(db, agentId);
        var page = Assert.IsType<ViewResult>(await controller.Statement(2026, 9));
        var statement = Assert.IsType<ClientInvoiceStatement>(page.Model);
        Assert.Equal(new[] { "S-1", "S-2" }, statement.Invoiced.Select(r => r.DocumentNumber).ToArray());
        Assert.Equal(323m, statement.InvoicedTotal);       // 113 + 210
        Assert.Equal(23m, statement.InvoicedTax);
        Assert.Equal(210m, statement.ReceivedTotal);
        Assert.Equal(413m, statement.OwedAtEnd);            // S-1 (113) and S-3 (300)

        var csv = Assert.IsType<FileContentResult>(await controller.ExportStatement(2026, 9));
        var lines = Encoding.UTF8.GetString(csv.FileContents).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.StartsWith("Document #,Client,Issue Date,Due Date,Status,Subtotal,Tax Region,Tax Rate,Tax,Total,Currency,Paid On,Paid Method", lines[0]);
        Assert.Equal(3, lines.Count);                       // the header and the two September invoices
        Assert.Contains(lines, l => l.StartsWith("S-1,") && l.Contains(",100.00,Ontario HST,13.000%,13.00,113.00,CAD,"));
        Assert.Contains(lines, l => l.StartsWith("S-2,") && l.Contains(",2026-09-25,EFT"));

        var xero = Assert.IsType<FileContentResult>(await controller.ExportXero(2026, 9));
        var xeroLines = Encoding.UTF8.GetString(xero.FileContents).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal("*ContactName,EmailAddress,POAddressLine1,POAddressLine2,POAddressLine3,POAddressLine4,POCity,PORegion,POPostalCode,POCountry,*InvoiceNumber,Reference,*InvoiceDate,*DueDate,Total,InventoryItemCode,*Description,*Quantity,*UnitAmount,Discount,*AccountCode,*TaxType,TaxAmount,TrackingName1,TrackingOption1,TrackingName2,TrackingOption2,Currency,BrandingTheme", xeroLines[0]);
        Assert.Equal(4, xeroLines.Count);                   // the header, two lines of S-1, one of S-2
        Assert.Equal(2, xeroLines.Count(l => l.Contains(",S-1,")));
        Assert.Contains(xeroLines, l => l.Contains(",S-2,") && l.Contains(",2026-09-20,2026-10-20,") && l.Contains(",200,") && l.Contains(",GST 5%,"));
        Assert.DoesNotContain(xeroLines, l => l.Contains("S-9") || l.Contains("S-3"));
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
            UserName = ($"t523d-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "State",
            LastName = "Agent",
            CompanyName = "Statement Co",
            DomainName = ($"t523d-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task SeedInvoiceAsync(IPRODbContext db, int agentId, string number, DateTime issued, decimal subtotal, decimal rate, string region,
        ClientInvoiceStatus status, DateTime? paidAtUtc = null, int lines = 1)
    {
        var client = new Client { AgentUserId = agentId, FirstName = "Cli", LastName = number, Email = $"{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var tax = Math.Round(subtotal * rate, 2);
        var invoice = new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id,
            DocumentType = ClientInvoiceDocumentType.Invoice, Status = status,
            DocumentNumber = number, Currency = "CAD",
            IssueDate = issued, DueDate = issued.AddDays(30),
            SubTotal = subtotal, TaxRegion = region, TaxRate = rate, TaxAmount = tax, Total = subtotal + tax,
            ViewToken = Guid.NewGuid().ToString("N"),
            SentAt = issued, PaidAt = paidAtUtc, PaidMethod = paidAtUtc.HasValue ? ClientInvoicePaymentMethod.EFT : null
        };
        for (var n = 1; n <= lines; n++)
        {
            var amount = Math.Round(subtotal / lines, 2);
            invoice.LineItems.Add(new ClientInvoiceLineItem { Description = $"Line {n}", Quantity = 1, UnitPrice = amount, Amount = amount, SortOrder = n });
        }
        db.ClientInvoices.Add(invoice);
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
