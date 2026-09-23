using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 514 (2026-09-22). The owner had the customer invoice redesigned (the marketing designer's package
// invoice-template-v1: markup, stylesheet, desktop and phone renders, a developer handoff) and asked
// for it the same day. The page is rebuilt on that design with the SAME data the old one showed --
// nothing the system does not track is printed: the package's quantity and rate columns are gone
// (every line is one item), "billing period" shows the package and its cycle rather than invented
// dates, and "paid on" is not shown because no such date is stored. What the design adds and the
// system CAN say: the supplier's address and GST/HST number, the tax name and rate on every line
// and in a tax summary, payment received and balance due, the status as Paid / Unpaid / Payment
// failed, Letter-size print styling. The supplier's details are not hardcoded (the owner: "can u not
// hardcode the elements needed i.e. GST/HST and addresses so we could populate it from
// superadmin"): SuperAdmin -> Company Details, one row, read by the page, the invoice email and
// SuperAdmin's print view, with the settings as the fallback for any blank field. The bill-to is
// the snapshot frozen on the invoice at issue time (it outlives the agent), with the live agent as
// the fallback for invoices older than the snapshot. Every defect test observed RED on the pre-fix code.
public class InvoiceDesign514Tests
{
    private static Invoice Paid(decimal subtotal = 60m, decimal rate = 0.13m, string region = "ON HST", string transaction = "582843411U3731847") => new()
    {
        InvoiceNumber = "IPRO-2026-000031",
        SubTotal = subtotal, TaxRate = rate, TaxAmount = Math.Round(subtotal * rate, 2), TaxRegion = region,
        Total = subtotal + Math.Round(subtotal * rate, 2), Currency = "CAD",
        IssuedAt = new DateTime(2026, 9, 22, 20, 30, 0, DateTimeKind.Utc),
        IsPaid = true, PayPalTransactionId = transaction,
        BillToName = "Free test", BillToCompany = "Abcd Inc.", BillToEmail = "etest@iproadvisers.com",
        BillToAddress = "3230 Yonge Street Suite 2005\nToronto\nOntario M5R 1R9\nCanada",
        Billing = new IPRO.Entities.Billing { Period = BillingPeriod.Monthly, Status = BillingStatus.Active },
        LineItems = new List<InvoiceLineItem>
        {
            new() { Description = "IPro Gold monthly recurring subscription", Amount = subtotal, SortOrder = 0 }
        }
    };

    private static readonly BillingRule Gold = new() { PackageName = "IPro Gold" };

    // ---- what the page says ---------------------------------------------------------------------

    [Fact]
    public void A_paid_invoice_reads_paid_with_the_payment_received_and_nothing_owing()
    {
        var view = InvoicePresentation.From(Paid(), null, Gold);

        Assert.Equal("Paid", view.StatusText);
        Assert.Equal("paid", view.StatusClass);
        Assert.Equal("Monthly subscription", view.Eyebrow);
        Assert.Equal("IPro Gold", view.PackageName);
        Assert.Equal("Monthly", view.BillingCycle);
        Assert.Equal("PayPal", view.Method);
        Assert.Equal("582843411U3731847", view.TransactionId);
        Assert.Equal(67.80m, view.PaymentReceived);
        Assert.Equal(0m, view.BalanceDue);
    }

    [Fact]
    public void An_unpaid_invoice_owes_its_total_and_a_failed_payment_says_so()
    {
        var open = Paid(transaction: "");
        open.IsPaid = false;
        var openView = InvoicePresentation.From(open, null, Gold);
        Assert.Equal("Unpaid", openView.StatusText);
        Assert.Equal("open", openView.StatusClass);
        Assert.Equal(0m, openView.PaymentReceived);
        Assert.Equal(67.80m, openView.BalanceDue);
        Assert.Equal(string.Empty, openView.TransactionId);

        var failed = Paid(transaction: "PAYPAL_FAILED:abc123");
        failed.IsPaid = false;
        var failedView = InvoicePresentation.From(failed, null, Gold);
        Assert.Equal("Payment failed", failedView.StatusText);
        Assert.Equal("failed", failedView.StatusClass);
        Assert.Equal(string.Empty, failedView.TransactionId);   // the failure marker is not a transaction
        Assert.Equal(67.80m, failedView.BalanceDue);
    }

    [Fact]
    public void The_date_is_the_advisers_own_and_the_bill_to_is_the_snapshot_frozen_on_the_invoice()
    {
        var agent = new AgentUser { FirstName = "Changed", LastName = "Later", CompanyName = "New Co", Email = "new@example.test", TimeZone = "(GMT-08:00) Pacific Time (US & Canada)" };

        var view = InvoicePresentation.From(Paid(), agent, Gold);

        Assert.Equal("September 22, 2026", view.InvoiceDate);          // 8:30 p.m. UTC is still the 22nd on the Pacific coast
        Assert.Equal("Free test", view.BillToName);
        Assert.Equal("Abcd Inc.", view.BillToCompany);
        Assert.Equal("etest@iproadvisers.com", view.BillToEmail);
        Assert.Equal(new[] { "3230 Yonge Street Suite 2005", "Toronto, Ontario M5R 1R9", "Canada" }, view.BillToAddressLines);   // 516: one city line
    }

    [Fact]
    public void An_invoice_older_than_the_snapshot_falls_back_to_the_agent_as_the_old_page_did()
    {
        var old = Paid();
        old.BillToName = old.BillToCompany = old.BillToEmail = old.BillToAddress = string.Empty;
        var agent = new AgentUser { FirstName = "Michael", LastName = "Tran", CompanyName = "Tran Financial Group", Email = "michaeltran@alladvisers.com", CompanyAddress = "1 King St W", City = "Toronto", Province = "Ontario", PostalCode = "M5H 1A1", Country = "Canada" };

        var view = InvoicePresentation.From(old, agent, Gold);

        Assert.Equal("Michael Tran", view.BillToName);
        Assert.Equal("Tran Financial Group", view.BillToCompany);
        Assert.Equal("michaeltran@alladvisers.com", view.BillToEmail);
        Assert.Equal(new[] { "1 King St W", "Toronto, Ontario M5H 1A1", "Canada" }, view.BillToAddressLines);   // 516: one city line
    }

    [Fact]
    public void Control_with_neither_snapshot_nor_agent_the_page_still_renders_something_honest()
    {
        var old = Paid();
        old.BillToName = old.BillToCompany = old.BillToEmail = old.BillToAddress = string.Empty;

        var view = InvoicePresentation.From(old, null, null);

        Assert.Equal("IPRO Agent", view.BillToName);
        Assert.Empty(view.BillToAddressLines);
        Assert.Equal("IPRO package", view.PackageName);
        Assert.Equal("Subscription", InvoicePresentation.From(new Invoice { Billing = null! }, null, null).Eyebrow);
    }

    [Fact]
    public void The_tax_is_named_with_its_rate_on_every_line_and_in_the_summary()
    {
        var view = InvoicePresentation.From(Paid(), null, Gold);

        Assert.True(view.HasTax);
        Assert.Equal("ON HST 13%", view.TaxLabel);
        Assert.Equal("ON HST 13%", Assert.Single(view.Lines).TaxLabel);
        Assert.Equal("IPro Gold monthly recurring subscription", view.Lines[0].Description);
        Assert.Equal(60m, view.Lines[0].Amount);
    }

    [Theory]
    [InlineData(0.13, "ON 13% HST", "ON 13% HST")]        // older invoices already carry the rate in the region
    [InlineData(0.05, "AB GST", "AB GST 5%")]
    [InlineData(0.14975, "QC GST + QST", "QC GST + QST 14.975%")]
    public void The_tax_label_never_prints_the_rate_twice(double rate, string region, string expected)
    {
        var view = InvoicePresentation.From(Paid(rate: (decimal)rate, region: region), null, Gold);

        Assert.Equal(expected, view.TaxLabel);
    }

    [Fact]
    public void A_comped_invoice_says_no_tax_and_no_charge_instead_of_an_unexplained_zero()
    {
        var comped = Paid(subtotal: 0m, rate: 0m, region: "No tax", transaction: "");
        comped.LineItems = new List<InvoiceLineItem> { new() { Description = "IPro Platinum monthly subscription - free with promotion code OLD_DEMO", Amount = 0m } };

        var view = InvoicePresentation.From(comped, null, new BillingRule { PackageName = "IPro Platinum" });

        Assert.False(view.HasTax);
        Assert.Equal("No tax", view.TaxLabel);
        Assert.Equal("No tax", view.Lines[0].TaxLabel);
        Assert.Equal("No charge", view.Method);
        Assert.Equal(0m, view.BalanceDue);
    }

    [Fact]
    public void An_invoice_with_no_lines_still_shows_its_subtotal_as_one_line()
    {
        var bare = Paid();
        bare.LineItems = new List<InvoiceLineItem>();

        var view = InvoicePresentation.From(bare, null, Gold);

        Assert.Equal("IPRO billing charge", Assert.Single(view.Lines).Description);
        Assert.Equal(60m, view.Lines[0].Amount);
    }

    // ---- the supplier's details: SuperAdmin's, with the settings as the fallback ------------------------

    private static string? Setting(string key) => key switch
    {
        "BillingCompany:Name" => "IPRO Advisers",
        "BillingCompany:Email" => "billing@iproadvisers.com",
        "BillingCompany:Website" => "www.iProAdvisers.com",
        "BillingCompany:TaxRegistrationNumber" => "",
        "Legal:RegisteredAddress" => "3230 Yonge Street, Suite 2005, Toronto, ON M4N 3P6",
        _ => null
    };

    [Fact]
    public void With_nothing_saved_in_SuperAdmin_the_details_are_what_the_settings_say()
    {
        var details = BillingCompanyDetails.From(null, Setting);

        Assert.Equal("IPRO Advisers", details.Name);
        Assert.Equal("billing@iproadvisers.com", details.Email);
        Assert.Equal("www.iProAdvisers.com", details.Website);
        Assert.Equal(string.Empty, details.TaxRegistrationNumber);
        Assert.Equal(new[] { "3230 Yonge Street, Suite 2005, Toronto, ON M4N 3P6" }, details.AddressLines);
    }

    [Fact]
    public void A_saved_profile_wins_field_by_field_and_a_blank_field_falls_back()
    {
        var row = new BillingCompanyProfile
        {
            Name = "iPro Advisers Inc.", AddressLine1 = "3230 Yonge Street", AddressLine2 = "Suite 2005",
            City = "Toronto", Province = "ON", PostalCode = "M4N 3P6", Country = "Canada",
            TaxRegistrationNumber = " 822383303 RT0001 ", Email = "", Website = ""
        };

        var details = BillingCompanyDetails.From(row, Setting);

        Assert.Equal("iPro Advisers Inc.", details.Name);
        Assert.Equal("822383303 RT0001", details.TaxRegistrationNumber);
        Assert.Equal(new[] { "3230 Yonge Street", "Suite 2005", "Toronto, ON M4N 3P6", "Canada" }, details.AddressLines);
        Assert.Equal("billing@iproadvisers.com", details.Email);      // blank in the profile: the setting serves
        Assert.Equal("www.iProAdvisers.com", details.Website);
    }

    [Fact]
    public async Task The_profile_is_saved_once_and_updated_in_place()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var when = new DateTime(2026, 9, 22, 22, 0, 0, DateTimeKind.Utc);

        await BillingCompanyDetails.SaveAsync(db, new BillingCompanyProfile { Name = "iPro Advisers Inc.", TaxRegistrationNumber = "822383303 RT0001" }, when);
        await BillingCompanyDetails.SaveAsync(db, new BillingCompanyProfile { Name = "iPro Advisers Inc.", TaxRegistrationNumber = "822383303 RT0001", City = "Toronto" }, when.AddMinutes(1));

        Assert.Equal(1, await db.BillingCompanyProfiles.CountAsync());
        var loaded = await BillingCompanyDetails.LoadAsync(db, Setting);
        Assert.Equal("iPro Advisers Inc.", loaded.Name);
        Assert.Equal("822383303 RT0001", loaded.TaxRegistrationNumber);
        Assert.Equal(new[] { "Toronto" }, loaded.AddressLines);
        Assert.Equal("billing@iproadvisers.com", loaded.Email);
    }

    [Fact]
    public void Every_place_that_prints_the_supplier_reads_the_same_details()
    {
        Assert.Contains("BillingCompanyDetails.LoadAsync(", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\BillingController.cs")));
        Assert.Contains("BillingCompanyDetails.LoadAsync(", File.ReadAllText(FindRepoFile(@"src\IPRO.Billing\PayPalBillingService.cs")));
        Assert.Contains("BillingCompanyDetails.LoadAsync(", File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Controllers\ReportsController.cs")));
        var adminInvoice = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Reports\Invoice.cshtml"));
        Assert.Contains("company.Name", adminInvoice);
        Assert.Contains("company.TaxRegistrationNumber", adminInvoice);
        Assert.DoesNotContain("<h4 class=\"fw-bold mb-1\">IPRO Advisers</h4>", adminInvoice);
        foreach (var program in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
            Assert.Contains("StartupSchemaRepair.EnsureBillingCompanyProfileSchemaAsync(db)", File.ReadAllText(FindRepoFile(program)));
    }

    [Fact]
    public void SuperAdmin_has_a_Company_Details_page_in_its_Billing_section()
    {
        var layout = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Shared\_Layout.cshtml"));
        Assert.Contains("href=\"/CompanyDetails\"", layout);

        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Controllers\CompanyDetailsController.cs"));
        Assert.Contains("[Authorize(Policy = \"SuperAdmin\")]", controller);
        Assert.Contains("BillingCompanyDetails.SaveAsync(", controller);
        Assert.Contains("_auditLog.LogAsync(", controller);

        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\CompanyDetails\Index.cshtml"));
        foreach (var field in new[] { "Name", "AddressLine1", "AddressLine2", "City", "Province", "PostalCode", "Country", "TaxRegistrationNumber", "Email", "Website" })
            Assert.Contains("asp-for=\"" + field + "\"", view);
    }

    // ---- the page itself ----------------------------------------------------------------------------

    [Fact]
    public void The_page_is_built_on_the_designers_markup_with_the_systems_data_and_nothing_invented()
    {
        var razor = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Billing\Invoice.cshtml"));

        Assert.Contains("InvoicePresentation.From(", razor);
        foreach (var marker in new[] { "class=\"invoice-summary\"", "class=\"party-grid\"", "class=\"tax-note\"", "class=\"totals\"", "class=\"brand-rule\"", "class=\"logo-plate\"", "data-label=\"Description\"", "data-label=\"Tax\"", "data-label=\"Amount\"" })
            Assert.Contains(marker, razor);
        Assert.DoesNotContain("data-label=\"Quantity\"", razor);    // not tracked: not printed
        Assert.DoesNotContain("data-label=\"Rate\"", razor);
        Assert.DoesNotContain("{{", razor);                         // no placeholder survives
        Assert.DoesNotContain("Due on receipt", razor);
        Assert.DoesNotContain("Paid on", razor);
        Assert.Contains("/images/ipro-advisers-logo.png", razor);
        Assert.Contains("/css/invoice.css", razor);                       // the designer's stylesheet, shared by both documents
        Assert.Contains("size: Letter", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\wwwroot\css\invoice.css")));
        Assert.Contains("window.print()", razor);
        Assert.Contains("Context.GetCspNonce()", razor);
        // 511 stays: the brand domain and address a person reads carry the capitals.
        Assert.Contains("@IPRO.Email.BrandText.WithCapitals(companyEmail)", razor);
        Assert.Contains("@IPRO.Email.BrandText.WithCapitals(companyWebsite)", razor);
        // the supplier's address and GST/HST number come from Company Details, and are simply absent when unset
        Assert.Contains("companyAddressLines", razor);
        Assert.Contains("GST/HST registration no.", razor);
        Assert.Contains("@if (!string.IsNullOrWhiteSpace(taxNumber))", razor);
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
