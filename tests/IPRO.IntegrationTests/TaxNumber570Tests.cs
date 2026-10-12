using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 570 (2026-10-12): a business's GST/HST registration number on its invoices to clients. Offered on
// 2026-10-11 with the tax work (a registered business is expected to show it, and there was no field
// for one); the owner the next morning: "Lets do the GST/HST number on client invoices". It is typed
// once on the Profile page and printed where iPro's own invoice prints iPro's: under the supplier's
// details and in the Tax summary. Empty prints nothing.
public class TaxNumber570Tests
{
    private static ClientInvoice Document() => new()
    {
        DocumentNumber = "INV-1001", DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent,
        IssueDate = new DateTime(2026, 10, 12), SubTotal = 80m, TaxRegion = "ON 13% HST", TaxRate = 0.13m, TaxAmount = 10.40m, Total = 90.40m,
        Client = new Client { FirstName = "Ava", LastName = "Chen", Province = "ON" },
        LineItems = { new ClientInvoiceLineItem { Description = "Birthday cake", Quantity = 1, UnitPrice = 80m, Amount = 80m } }
    };

    [Fact]
    public void The_document_carries_the_businesss_number_and_nothing_when_it_has_none()
    {
        var registered = ClientDocumentPresentation.From(Document(), new AgentUser { CompanyName = "Corner Bakery", TaxRegistrationNumber = "  123456789 RT0001 " }, null);
        Assert.Equal("123456789 RT0001", registered.SupplierTaxNumber);

        Assert.Equal(string.Empty, ClientDocumentPresentation.From(Document(), new AgentUser { CompanyName = "Corner Bakery" }, null).SupplierTaxNumber);
        Assert.Equal(string.Empty, ClientDocumentPresentation.From(Document(), null, null).SupplierTaxNumber);
        Assert.Equal(string.Empty, new AgentUser().TaxRegistrationNumber);                    // every business from before: none
    }

    [Fact]
    public void It_is_printed_under_the_suppliers_details_and_in_the_tax_summary_as_on_the_platforms_own_invoice()
    {
        var document = Read(@"src\IPRO.Web\Views\ClientInvoices\_ClientInvoiceDocument.cshtml");
        // Both headers: the one with a logo and the one with the name as a wordmark.
        Assert.Equal(2, document.Split("<p>GST/HST registration no. @view.SupplierTaxNumber</p>").Length - 1);
        Assert.Contains("<p><span>GST/HST registration no.</span><strong>@view.SupplierTaxNumber</strong></p>", document);
        Assert.Equal(3, document.Split("@if (!string.IsNullOrWhiteSpace(view.SupplierTaxNumber))").Length - 1);   // never an empty label

        // The same words as iPro's own invoice to an adviser.
        var platform = Read(@"src\IPRO.Web\Views\Billing\Invoice.cshtml");
        Assert.Contains("<p>GST/HST registration no. @taxNumber</p>", platform);
        Assert.Contains("<p><span>GST/HST registration no.</span><strong>@taxNumber</strong></p>", platform);
    }

    [Fact]
    public void The_profile_page_asks_for_it_once_and_keeps_it()
    {
        var page = Read(@"src\IPRO.Web\Views\Account\Profile.cshtml");
        Assert.Contains("<label asp-for=\"TaxRegistrationNumber\" class=\"form-label fw-semibold\">GST/HST number</label>", page);
        Assert.Contains("<input asp-for=\"TaxRegistrationNumber\" class=\"form-control\" maxlength=\"60\"", page);
        Assert.Contains("Leave it empty if you are not registered for GST/HST.", page);

        var controller = Read(@"src\IPRO.Web\Controllers\AccountController.cs");
        Assert.Contains("agent.TaxRegistrationNumber = model.TaxRegistrationNumber ?? \"\";", controller);            // saved
        Assert.Contains("TaxRegistrationNumber = agent.TaxRegistrationNumber,", controller);                          // shown again
        Assert.Contains("model.TaxRegistrationNumber = model.TaxRegistrationNumber?.Trim() ?? \"\";", controller);    // without stray spaces

        var model = typeof(IPRO.Web.Models.AgentProfileViewModel).GetProperty("TaxRegistrationNumber")!;
        var length = (System.ComponentModel.DataAnnotations.StringLengthAttribute)model.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.StringLengthAttribute), false).Single();
        Assert.Equal(60, length.MaximumLength);
        Assert.Contains("e.Property(u => u.TaxRegistrationNumber).HasMaxLength(60);", Read(@"src\IPRO.DataAccess\IPRODbContext.cs"));
        Assert.Contains("### Your GST/HST Number", Read(@"DOCS\10_CLIENT_INVOICING.md"));
    }

    [Fact]
    public async Task A_database_from_before_gains_the_column_and_the_number_is_kept_with_the_business()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, number: "");

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE `AgentUsers` DROP COLUMN `TaxRegistrationNumber`");   // yesterday's table
        await StartupSchemaRepair.EnsureClientInvoiceSchemaAsync(db);
        await StartupSchemaRepair.EnsureClientInvoiceSchemaAsync(db);                                             // again: nothing to do

        await using (var fresh = testDb.CreateContext())
        {
            var agent = await fresh.AgentUsers.SingleAsync(a => a.Id == agentId);
            Assert.Equal(string.Empty, agent.TaxRegistrationNumber);                    // an existing business: none, and nothing printed
            agent.TaxRegistrationNumber = "123456789 RT0001";
            await fresh.SaveChangesAsync();
        }
        await using var again = testDb.CreateContext();
        Assert.Equal("123456789 RT0001", (await again.AgentUsers.AsNoTracking().SingleAsync(a => a.Id == agentId)).TaxRegistrationNumber);

        // Both apps read AgentUsers and both run this method at start-up, so whichever starts first adds the column.
        Assert.Contains("ADD COLUMN `TaxRegistrationNumber` varchar(60) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''", Read(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        foreach (var program in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
            Assert.Contains("StartupSchemaRepair.EnsureClientInvoiceSchemaAsync(db)", Read(program));
    }

    [Fact]
    public async Task The_editor_reminds_a_business_that_charges_tax_and_has_no_number()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var without = await SeedAgentAsync(db, number: "");
        var with = await SeedAgentAsync(db, number: "123456789 RT0001");

        var first = NewInvoicesController(db, without);
        Assert.IsType<ViewResult>(await first.Create());
        Assert.True((bool)first.ViewBag.TaxNumberMissing);
        var second = NewInvoicesController(db, with);
        Assert.IsType<ViewResult>(await second.Create());
        Assert.False((bool)second.ViewBag.TaxNumberMissing);

        var editor = Read(@"src\IPRO.Web\Views\Shared\_InvoiceLinesEditor.cshtml");
        Assert.Contains("@if (ViewBag.TaxNumberMissing as bool? == true)", editor);
        Assert.Contains("id=\"taxNumberReminder\" hidden>Your GST/HST number is not on your Profile yet, so it will not print on this @Model.DocWord.", editor);
        Assert.Contains("<a href=\"/portal/Account/Profile\" target=\"_blank\" rel=\"noopener\">Add it</a>", editor);   // a new tab: the document is not lost
        Assert.Contains("if (reminder) reminder.hidden = !(tax > 0);", editor);                                       // only while tax is charged
        Assert.Contains("ViewBag.TaxNumberMissing = string.IsNullOrWhiteSpace(business?.TaxRegistrationNumber);", Read(@"src\IPRO.Web\Controllers\RecurringInvoicesController.cs"));
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string number)
    {
        var rule = new BillingRule { PackageName = ($"T570-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t570-{Guid.NewGuid():N}")[..20], Email = $"{Guid.NewGuid():N}@example.test", FirstName = "Corner", LastName = "Baker",
            CompanyName = "Corner Bakery", Province = "ON", TaxRegistrationNumber = number, DomainName = ($"t570-{Guid.NewGuid():N}")[..24], PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static IPRO.Web.Controllers.ClientInvoicesController NewInvoicesController(IPRODbContext db, int agentId)
    {
        var controller = new IPRO.Web.Controllers.ClientInvoicesController(
            db, new GrantAll(), new ClientInvoiceService(new IPRO.DataAccess.Repositories.UnitOfWork(db)), new NoEmail(),
            new ConfigurationBuilder().Build());
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return controller;
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

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
