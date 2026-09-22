using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Xunit;

namespace IPRO.IntegrationTests;

// 515 (2026-09-22). With the platform's own invoice redesigned (514) the owner asked for the same for
// the invoices and estimates advisers send their clients -- "two tasks but one deploy". The same
// design, branded for the adviser: their company name, address, phone and email, their website's
// logo when they have one, and the columns this document has always tracked (quantity, unit price),
// its due date, paid-on date and method. The agent's toolbar (Send, Resend, Mark Paid, Convert,
// Duplicate, Void) and the client's actions (Approve, Decline, Pay Now) are kept as they were.
// Every defect test observed RED on the pre-fix code.
public class ClientDocumentDesign515Tests
{
    private static readonly AgentUser Agent = new()
    {
        FirstName = "Michael", LastName = "Tran", CompanyName = "Tran Financial Group", Email = "michaeltran@alladvisers.com", Phone = "416-555-0100",
        CompanyAddress = "1 King St W", City = "Toronto", Province = "Ontario", PostalCode = "M5H 1A1", Country = "Canada",
        TimeZone = "(GMT-05:00) Eastern Time (US & Canada)"
    };

    private static ClientInvoice Document(ClientInvoiceDocumentType type = ClientInvoiceDocumentType.Invoice, ClientInvoiceStatus status = ClientInvoiceStatus.Sent) => new()
    {
        DocumentType = type, Status = status, DocumentNumber = "INV-0007",
        IssueDate = new DateTime(2026, 9, 22, 14, 0, 0, DateTimeKind.Utc), DueDate = new DateTime(2026, 10, 6, 14, 0, 0, DateTimeKind.Utc),
        SubTotal = 250m, TaxRegion = "ON HST", TaxRate = 0.13m, TaxAmount = 32.50m, Total = 282.50m, Currency = "CAD",
        Notes = "Thank you.",
        Client = new Client { FirstName = "Ava", LastName = "Chen", CompanyName = "Chen Dental", Email = "ava@example.test", Address = "22 Bay St", City = "Toronto", Province = "ON", PostalCode = "M5J 2T3", Country = "Canada" },
        LineItems = new List<ClientInvoiceLineItem>
        {
            new() { Description = "Financial plan review", Quantity = 2, UnitPrice = 125m, Amount = 250m, SortOrder = 0 }
        }
    };

    [Fact]
    public void A_sent_invoice_is_branded_for_the_adviser_and_says_what_is_owed()
    {
        var view = ClientDocumentPresentation.From(Document(), Agent, "/media/logos/tran.png");

        Assert.Equal("Invoice", view.DocumentLabel);
        Assert.False(view.IsEstimate);
        Assert.Equal("Awaiting payment", view.StatusText);
        Assert.Equal("open", view.StatusClass);
        Assert.Equal("Tran Financial Group", view.SupplierName);
        Assert.Equal(new[] { "1 King St W", "Toronto Ontario M5H 1A1", "Canada" }, view.SupplierAddressLines);
        Assert.Equal("416-555-0100", view.SupplierPhone);
        Assert.Equal("michaeltran@alladvisers.com", view.SupplierEmail);
        Assert.Equal("/media/logos/tran.png", view.LogoUrl);
        Assert.Equal("September 22, 2026", view.IssueDate);
        Assert.Equal("October 6, 2026", view.DueDate);
        Assert.Equal(string.Empty, view.PaidOn);
        Assert.Equal("Ava Chen", view.BillToName);
        Assert.Equal("Chen Dental", view.BillToCompany);
        Assert.Equal("ava@example.test", view.BillToEmail);
        Assert.Equal(new[] { "22 Bay St", "Toronto ON M5J 2T3", "Canada" }, view.BillToAddressLines);
        Assert.Equal("ON HST 13%", view.TaxLabel);
        var line = Assert.Single(view.Lines);
        Assert.Equal(("Financial plan review", 2m, 125m, "ON HST 13%", 250m), (line.Description, line.Quantity, line.UnitPrice, line.TaxLabel, line.Amount));
        Assert.True(view.ShowsBalance);
        Assert.Equal(0m, view.PaymentReceived);
        Assert.Equal(282.50m, view.BalanceDue);
    }

    [Fact]
    public void A_paid_invoice_shows_when_and_how_it_was_paid_and_nothing_owing()
    {
        var paid = Document(status: ClientInvoiceStatus.Paid);
        paid.PaidAt = new DateTime(2026, 9, 25, 3, 30, 0, DateTimeKind.Utc);   // 11:30 p.m. Eastern on the 24th
        paid.PaidMethod = ClientInvoicePaymentMethod.EFT;

        var view = ClientDocumentPresentation.From(paid, Agent, null);

        Assert.Equal("Paid", view.StatusText);
        Assert.Equal("paid", view.StatusClass);
        Assert.Equal("September 24, 2026", view.PaidOn);
        Assert.Equal("EFT", view.PaidMethod);
        Assert.Equal(282.50m, view.PaymentReceived);
        Assert.Equal(0m, view.BalanceDue);
        Assert.Equal(string.Empty, view.LogoUrl);
    }

    [Theory]
    [InlineData(ClientInvoiceStatus.Draft, "Draft", "open")]
    [InlineData(ClientInvoiceStatus.Declined, "Declined", "failed")]
    [InlineData(ClientInvoiceStatus.Void, "Void", "void")]
    [InlineData(ClientInvoiceStatus.Approved, "Approved", "paid")]
    public void Every_status_has_words_and_a_colour(ClientInvoiceStatus status, string text, string cls)
    {
        var view = ClientDocumentPresentation.From(Document(status: status), Agent, null);

        Assert.Equal(text, view.StatusText);
        Assert.Equal(cls, view.StatusClass);
    }

    [Fact]
    public void An_estimate_is_called_one_and_owes_nothing_yet()
    {
        var view = ClientDocumentPresentation.From(Document(ClientInvoiceDocumentType.Estimate), Agent, null);

        Assert.Equal("Estimate", view.DocumentLabel);
        Assert.True(view.IsEstimate);
        Assert.Equal("Awaiting your reply", view.StatusText);
        Assert.False(view.ShowsBalance);
        Assert.Equal(0m, view.BalanceDue);
    }

    [Fact]
    public void Control_an_adviser_without_company_details_still_gets_a_document()
    {
        var bare = Document();
        bare.Client = new Client { CompanyName = "Chen Dental" };

        var view = ClientDocumentPresentation.From(bare, new AgentUser { Email = "a@example.test" }, null);

        Assert.Equal("Your Business", view.SupplierName);
        Assert.Empty(view.SupplierAddressLines);
        Assert.Equal("Chen Dental", view.BillToName);      // no person's name: the company stands in
        Assert.Equal(string.Empty, view.BillToCompany);
    }

    // ---- the document itself ------------------------------------------------------------------------

    [Fact]
    public void The_document_is_built_on_the_design_with_the_advisers_brand_and_keeps_every_action()
    {
        var razor = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\ClientInvoices\_ClientInvoiceDocument.cshtml"));

        Assert.Contains("ClientDocumentPresentation.From(", razor);
        foreach (var marker in new[] { "class=\"invoice-summary\"", "class=\"party-grid\"", "class=\"tax-note\"", "class=\"totals\"", "class=\"brand-rule\"", "data-label=\"Description\"", "data-label=\"Quantity\"", "data-label=\"Unit price\"", "data-label=\"Tax\"", "data-label=\"Amount\"" })
            Assert.Contains(marker, razor);
        Assert.Contains("view.LogoUrl", razor);
        Assert.DoesNotContain("{{", razor);
        Assert.DoesNotContain("ipro-advisers-logo", razor);            // the adviser's document carries no IPRO branding
        Assert.DoesNotContain("iproadvisers.com", razor);
        Assert.Contains("/css/invoice.css", razor);                       // the designer's stylesheet, shared by both documents
        Assert.Contains("size: Letter", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\wwwroot\css\invoice.css")));
        Assert.Contains("window.print()", razor);
        Assert.Contains("Context.GetCspNonce()", razor);
        // the agent's tools and the client's actions, exactly as before
        foreach (var action in new[] { "ClientInvoices/Send/", "ClientInvoices/MarkPaid/", "ClientInvoices/ConvertToInvoice/", "ClientInvoices/Duplicate/", "ClientInvoices/Void/", "/decline", "/approve", "Pay Now", "Resend", "Not viewed yet", "Viewed" })
            Assert.Contains(action, razor);
    }

    [Fact]
    public void Both_pages_that_show_the_document_hand_it_the_advisers_logo()
    {
        Assert.Contains("ViewBag.LogoUrl", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\ClientInvoicesController.cs")));
        Assert.Contains("ViewBag.LogoUrl", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\ClientDocumentController.cs")));
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
