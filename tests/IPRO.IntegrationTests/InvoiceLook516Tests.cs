using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Xunit;

namespace IPRO.IntegrationTests;

// 516 (2026-09-22). The owner's first look at a real invoice on the 514 design, three asks and one
// thing seen beside them: (1) the tax appeared as a charge line ("ON 13% HST tax (13.000 %)") on top
// of the tax summary and the totals -- the stored tax line is now shown once, in the summary; (2) the
// bill-to printed the city, then "Province PostalCode" on the next line -- "Toronto, Ontario M4N 2Z3"
// on one line, for invoices already issued (the frozen snapshot is tidied on display, never
// rewritten) and for every address the invoices and estimates print from now on; (3) the PayPal
// reference "I-44AX6W6UNF68, 3L145276ES560371F" was one value broken mid-token -- it is the
// subscription and the transaction, two rows; (4) "Thank you for choosing iPro Advisers Inc.." --
// no second period after a name that ends with one. Every defect test observed RED on the pre-fix code.
public class InvoiceLook516Tests
{
    private static Invoice Paid(string transaction = "I-44AX6W6UNF68, 3L145276ES560371F") => new()
    {
        InvoiceNumber = "IPRO-2026-000019",
        SubTotal = 307.14m, TaxRate = 0.13m, TaxAmount = 39.93m, TaxRegion = "ON 13% HST", Total = 347.07m, Currency = "CAD",
        IssuedAt = new DateTime(2026, 8, 28, 14, 0, 0, DateTimeKind.Utc), IsPaid = true, PayPalTransactionId = transaction,
        BillToName = "Bahman Motamed", BillToCompany = "Global", BillToEmail = "b@example.test",
        BillToAddress = "123 Fast Lane\nToronto\nOntario M4N2Z3\nCanada",
        Billing = new IPRO.Entities.Billing { Period = BillingPeriod.Monthly, Status = BillingStatus.Active },
        LineItems = new List<InvoiceLineItem>
        {
            new() { Description = "IPro Platinum monthly recurring subscription", Amount = 307.14m, SortOrder = 0 },
            new() { Description = "ON 13% HST tax (13.000 %)", Amount = 39.93m, SortOrder = 10 }
        }
    };

    // ---- (1) the tax is shown once ----------------------------------------------------------------

    [Fact]
    public void The_stored_tax_line_is_not_listed_as_a_charge()
    {
        var view = InvoicePresentation.From(Paid(), null, new BillingRule { PackageName = "IPro Platinum" });

        var line = Assert.Single(view.Lines);
        Assert.Equal("IPro Platinum monthly recurring subscription", line.Description);
        Assert.Equal(307.14m, line.Amount);
        Assert.True(view.HasTax);
        Assert.Equal("ON 13% HST", view.TaxLabel);      // still in the summary and the totals
    }

    [Fact]
    public void Control_a_charge_that_merely_costs_the_same_as_the_tax_stays()
    {
        var invoice = Paid();
        invoice.LineItems.Add(new InvoiceLineItem { Description = "Setup fee", Amount = 39.93m, SortOrder = 20 });

        var view = InvoicePresentation.From(invoice, null, null);

        Assert.Equal(new[] { "IPro Platinum monthly recurring subscription", "Setup fee" }, view.Lines.Select(l => l.Description));
        Assert.True(InvoiceLines.IsTaxLine("ON 13% HST tax (13.000 %)", 39.93m, 39.93m));
        Assert.True(InvoiceLines.IsTaxLine("AB GST tax (5.000 %)", 3.00m, 3.00m));
        Assert.False(InvoiceLines.IsTaxLine("Setup fee", 39.93m, 39.93m));
        Assert.False(InvoiceLines.IsTaxLine("ON 13% HST tax (13.000 %)", 39.93m, 12.00m));   // not this invoice's tax
    }

    // ---- (2) the city line ---------------------------------------------------------------------------

    [Fact]
    public void An_invoice_issued_before_516_shows_city_province_and_postal_code_on_one_line()
    {
        var view = InvoicePresentation.From(Paid(), null, null);

        Assert.Equal(new[] { "123 Fast Lane", "Toronto, Ontario M4N2Z3", "Canada" }, view.BillToAddressLines);
    }

    [Theory]
    [InlineData("Toronto\nOntario M4N2Z3\nCanada", "Toronto, Ontario M4N2Z3|Canada")]              // no street
    [InlineData("1 King St W\nToronto\nOntario\nCanada", "1 King St W|Toronto, Ontario|Canada")]     // no postal code
    [InlineData("1 King St W\nToronto\nCanada", "1 King St W|Toronto|Canada")]                       // no province: nothing to join
    [InlineData("1 King St W\nToronto, Ontario M5H 1A1\nCanada", "1 King St W|Toronto, Ontario M5H 1A1|Canada")]  // already one line
    [InlineData("350 Fifth Ave\nNew York\nNY 10118\nUSA", "350 Fifth Ave|New York, NY 10118|USA")]
    [InlineData("12 Rue Test\nMontréal\nQuébec H2X 1Y6\nCanada", "12 Rue Test|Montréal, Québec H2X 1Y6|Canada")]
    public void The_snapshot_is_tidied_on_display_only_where_the_shape_is_certain(string snapshot, string expected)
    {
        var lines = AddressText.JoinCityAndProvinceLines(snapshot.Split('\n'));

        Assert.Equal(expected.Split('|'), lines);
    }

    [Fact]
    public void The_city_line_is_written_one_way_everywhere()
    {
        Assert.Equal("Toronto, ON M4N 3P6", AddressText.CityLine("Toronto", "ON", "M4N 3P6"));
        Assert.Equal("Toronto M4N-3P6", AddressText.CityLine("Toronto", "", "M4N-3P6"));       // no province: no comma
        Assert.Equal("Toronto", AddressText.CityLine(" Toronto ", null, null));
        Assert.Equal("ON M4N 3P6", AddressText.CityLine("", "ON", "M4N 3P6"));
        Assert.Equal(string.Empty, AddressText.CityLine(null, null, null));

        // the live agent (invoices older than the snapshot), Company Details, and the adviser's own documents
        var agent = new AgentUser { FirstName = "Michael", LastName = "Tran", CompanyAddress = "1 King St W", City = "Toronto", Province = "Ontario", PostalCode = "M5H 1A1", Country = "Canada" };
        var old = Paid(); old.BillToAddress = string.Empty;
        Assert.Equal(new[] { "1 King St W", "Toronto, Ontario M5H 1A1", "Canada" }, InvoicePresentation.From(old, agent, null).BillToAddressLines);
        Assert.Equal(new[] { "3230 Yonge Street", "Toronto, ON M4N 3P6", "Canada" },
            BillingCompanyDetails.AddressLinesOf(new BillingCompanyProfile { AddressLine1 = "3230 Yonge Street", City = "Toronto", Province = "ON", PostalCode = "M4N 3P6", Country = "Canada" }));
        var document = new ClientInvoice
        {
            DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent, IssueDate = DateTime.UtcNow,
            Client = new Client { FirstName = "Ava", LastName = "Chen", Address = "22 Bay St", City = "Toronto", Province = "ON", PostalCode = "M5J 2T3", Country = "Canada" },
            LineItems = new List<ClientInvoiceLineItem>()
        };
        var clientView = ClientDocumentPresentation.From(document, agent, null);
        Assert.Equal(new[] { "1 King St W", "Toronto, Ontario M5H 1A1", "Canada" }, clientView.SupplierAddressLines);
        Assert.Equal(new[] { "22 Bay St", "Toronto, ON M5J 2T3", "Canada" }, clientView.BillToAddressLines);
    }

    // ---- (3) the PayPal reference --------------------------------------------------------------------

    [Theory]
    [InlineData("I-44AX6W6UNF68, 3L145276ES560371F", "I-44AX6W6UNF68", "3L145276ES560371F")]
    [InlineData("I-44AX6W6UNF68, PAYPAL_FAILED:9ZZ, 3L145276ES560371F", "I-44AX6W6UNF68", "3L145276ES560371F")]  // the retry settled it
    [InlineData("582843411U3731847", "", "582843411U3731847")]                                                  // a job-created invoice
    [InlineData("I-06NA6SDTLYJW", "I-06NA6SDTLYJW", "")]                                                        // paid, nothing settled yet
    public void The_reference_reads_as_subscription_and_transaction(string stored, string subscription, string transaction)
    {
        var view = InvoicePresentation.From(Paid(stored), null, null);

        Assert.Equal(subscription, view.SubscriptionId);
        Assert.Equal(transaction, view.TransactionId);
    }

    [Fact]
    public void A_failed_or_unpaid_invoice_names_no_reference()
    {
        var failed = Paid("I-44AX6W6UNF68, PAYPAL_FAILED:9ZZ");
        failed.IsPaid = false;
        var view = InvoicePresentation.From(failed, null, null);

        Assert.Equal("Payment failed", view.StatusText);
        Assert.Equal(string.Empty, view.SubscriptionId);
        Assert.Equal(string.Empty, view.TransactionId);
        var split = PayPalReference.Split(failed.PayPalTransactionId);
        Assert.Equal("I-44AX6W6UNF68", split.SubscriptionId);
        Assert.Equal(new[] { "9ZZ" }, split.FailedIds);
        Assert.Empty(split.TransactionIds);
    }

    // ---- the page, the email and the print view follow -------------------------------------------------

    [Fact]
    public void Every_invoice_surface_shows_the_tax_once_the_city_line_one_way_and_the_reference_in_two_rows()
    {
        var page = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Billing\Invoice.cshtml"));
        Assert.Contains("view.SubscriptionId", page);
        Assert.Contains("PayPal subscription", page);
        Assert.Contains("@(companyName.EndsWith(\".\") ? \"\" : \".\")", page);   // (4) no "Inc.."

        var service = File.ReadAllText(FindRepoFile(@"src\IPRO.Billing\PayPalBillingService.cs"));
        Assert.Contains("InvoiceLines.Charges(", service);                       // the paid-invoice email
        Assert.Contains("AddressText.CityLine(", service);                        // the snapshot written from now on, and the email's bill-to
        Assert.Contains("PayPalReference.Split(", service);
        Assert.DoesNotContain("var provincePostal = agent == null ? string.Empty : $\"{agent.Province} {agent.PostalCode}\".Trim();", service);

        var print = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Reports\Invoice.cshtml"));
        Assert.Contains("InvoiceLines.Charges(", print);
        Assert.Contains("AddressText.JoinCityAndProvinceLines(", print);
        Assert.Contains("PayPalReference.Split(", print);

        Assert.Contains("AddressText.CityLine(", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Infrastructure\ClientDocumentPresentation.cs")));
        Assert.Contains("AddressText.CityLine(", File.ReadAllText(FindRepoFile(@"src\IPRO.DataAccess\BillingCompanyDetails.cs")));
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
