using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 563 (2026-10-11): tax on an invoice to a client is a tick on each line.
//
// The owner's first non-adviser customer, a bakery, sent an invoice with no tax on it. Its client
// had no province on file, the rule read the client's province alone, and the editor showed no
// subtotal, tax or total, so nothing said so before the invoice went out. And a bakery sells plain
// bread (zero-rated) beside cakes (taxed), so tax cannot be all-or-nothing for an invoice. The
// owner, with a screenshot: "for each item all we need is a checked box and make that column a
// bit smaller".
public class TaxableLines563Tests
{
    private static readonly ProvinceTaxRate[] Rates =
    {
        new() { ProvinceCode = "ON", ProvinceName = "Ontario", TaxLabel = "13% HST", Rate = 0.13m, IsActive = true },
        new() { ProvinceCode = "AB", ProvinceName = "Alberta", TaxLabel = "5% GST", Rate = 0.05m, IsActive = true },
        new() { ProvinceCode = "QC", ProvinceName = "Quebec", TaxLabel = "GST + QST", Rate = 0.14975m, IsActive = false },
    };

    private static Client Buyer(string province, string country = "Canada") => new() { FirstName = "Ava", LastName = "Chen", Province = province, Country = country };
    private static AgentUser Bakery(string province, string country = "Canada") => new() { CompanyName = "Corner Bakery", Province = province, Country = country };

    // ---- the rule ------------------------------------------------------------------------------------

    [Fact]
    public void The_clients_province_decides_and_the_businesss_own_stands_in_when_the_client_has_none()
    {
        var fromClient = ClientInvoiceTaxRules.Resolve(Buyer("AB"), Bakery("ON"), Rates);
        Assert.Equal((0.05m, "AB 5% GST", ClientInvoiceTaxRules.FromClient, string.Empty), (fromClient.Rate, fromClient.Region, fromClient.Source, fromClient.Note));

        Assert.Equal(0.13m, ClientInvoiceTaxRules.Resolve(Buyer("Ontario"), null, Rates).Rate);        // the name, not the code
        Assert.Equal(0.13m, ClientInvoiceTaxRules.Resolve(Buyer("on", country: ""), null, Rates).Rate); // a province with no country is in Canada

        // The bakery's case: a client with a name and an email and no address.
        var fromBusiness = ClientInvoiceTaxRules.Resolve(Buyer(""), Bakery("ON"), Rates);
        Assert.Equal((0.13m, "ON 13% HST", ClientInvoiceTaxRules.FromBusiness), (fromBusiness.Rate, fromBusiness.Region, fromBusiness.Source));
        Assert.Contains("no province on file", fromBusiness.Note);
        Assert.True(fromBusiness.Charges);
    }

    [Fact]
    public void When_no_tax_is_charged_the_rule_says_why()
    {
        var nobody = ClientInvoiceTaxRules.Resolve(Buyer(""), Bakery(""), Rates);
        Assert.Equal((0m, ClientInvoiceTaxRules.None), (nobody.Rate, nobody.Source));
        Assert.Contains("neither does your business profile", nobody.Note);
        Assert.False(nobody.Charges);

        Assert.Equal(0m, ClientInvoiceTaxRules.Resolve(Buyer(""), null, Rates).Rate);                   // no business asked: as before

        var typo = ClientInvoiceTaxRules.Resolve(Buyer("Ont"), Bakery("ON"), Rates);                    // a province given is never guessed over
        Assert.Equal((0m, "ONT"), (typo.Rate, typo.Region));
        Assert.Contains("\"Ont\"", typo.Note);

        Assert.Equal((0m, "US"), Pair(ClientInvoiceTaxRules.Resolve(Buyer("NY", "USA"), Bakery("ON"), Rates)));
        Assert.Equal((0m, "France"), Pair(ClientInvoiceTaxRules.Resolve(Buyer("", "France"), Bakery("ON"), Rates)));
        Assert.Equal(0m, ClientInvoiceTaxRules.Resolve(Buyer("QC"), Bakery("ON"), Rates).Rate);         // a rate switched off in admin is not charged
        Assert.Equal(0m, ClientInvoiceTaxRules.Resolve(Buyer(""), Bakery("ON", "USA"), Rates).Rate);    // a business outside Canada lends no rate
    }

    private static (decimal, string) Pair(ClientInvoiceTaxRule rule) => (rule.Rate, rule.Region);

    [Fact]
    public void Tax_is_worked_out_to_the_cent_with_halves_rounded_up()
    {
        Assert.Equal(10.40m, ClientInvoiceTaxRules.TaxOn(80m, 0.13m));
        Assert.Equal(4.34m, ClientInvoiceTaxRules.TaxOn(33.35m, 0.13m));     // 4.3355
        Assert.Equal(0.07m, ClientInvoiceTaxRules.TaxOn(0.50m, 0.13m));      // 0.065, away from zero
        Assert.Equal(0m, ClientInvoiceTaxRules.TaxOn(0m, 0.13m));
        Assert.Equal(0m, ClientInvoiceTaxRules.TaxOn(-25m, 0.13m));          // credits alone are not taxed backwards
        Assert.Equal(0m, ClientInvoiceTaxRules.TaxOn(80m, 0m));
    }

    // ---- the document --------------------------------------------------------------------------------

    private static ClientInvoice Document(decimal taxRate, params (string What, decimal Amount, bool Taxed)[] lines)
    {
        var taxable = lines.Where(l => l.Taxed).Sum(l => l.Amount);
        var tax = ClientInvoiceTaxRules.TaxOn(taxable, taxRate);
        var document = new ClientInvoice
        {
            DocumentNumber = "INV-1001", DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent,
            IssueDate = new DateTime(2026, 10, 11), SubTotal = lines.Sum(l => l.Amount),
            TaxRegion = tax > 0 ? "ON 13% HST" : "No tax", TaxRate = tax > 0 ? taxRate : 0m, TaxAmount = tax, Total = lines.Sum(l => l.Amount) + tax,
            Client = Buyer("ON")
        };
        var order = 0;
        foreach (var (what, amount, taxed) in lines)
            document.LineItems.Add(new ClientInvoiceLineItem { Description = what, Quantity = 1, UnitPrice = amount, Amount = amount, IsTaxable = taxed, SortOrder = order++ });
        return document;
    }

    [Fact]
    public void The_document_ticks_the_taxed_lines_and_the_summary_carries_what_they_add_up_to()
    {
        var view = ClientDocumentPresentation.From(Document(0.13m, ("Sourdough loaves", 65m, false), ("Birthday cake", 80m, true)), Bakery("ON"), null);

        Assert.True(view.HasTax);
        Assert.Equal("ON 13% HST", view.TaxLabel);
        Assert.Equal(80m, view.TaxableAmount);                                    // not the subtotal of 145
        Assert.Equal(new[] { false, true }, view.Lines.Select(l => l.Taxed).ToArray());
        Assert.Equal(new[] { "No tax", "ON 13% HST" }, view.Lines.Select(l => l.TaxLabel).ToArray());
    }

    [Fact]
    public void An_invoice_from_before_ticks_reads_as_it_did_and_one_with_no_tax_ticks_nothing()
    {
        // Every line written before 563 is taxed whenever its invoice is: the property's default.
        Assert.True(new ClientInvoiceLineItem().IsTaxable);
        Assert.True(new RecurringInvoiceLineItem().IsTaxable);
        Assert.True(new ClientInvoiceLineItemInputModel().IsTaxable);

        var older = ClientDocumentPresentation.From(Document(0.13m, ("Plan review", 250m, true), ("Filing", 50m, true)), Bakery("ON"), null);
        Assert.Equal(300m, older.TaxableAmount);
        Assert.All(older.Lines, l => Assert.True(l.Taxed));

        var untaxed = ClientDocumentPresentation.From(Document(0m, ("Plan review", 250m, true)), Bakery("ON"), null);
        Assert.False(untaxed.HasTax);
        Assert.Equal(0m, untaxed.TaxableAmount);
        Assert.All(untaxed.Lines, l => Assert.False(l.Taxed));                    // a tick on a document with no tax would be a lie
    }

    // ---- 564: the dates on the document ---------------------------------------------------------------
    // Found while looking at 563 on the local app: the invoice page showed the issue date and the due
    // date one day early. They are calendar dates stored at midnight; the page read them as moments in
    // UTC and moved them to the adviser's zone. The email, the list and the statement never did.

    private const string Toronto = "(GMT-05:00) Eastern Time (US & Canada)";

    [Fact]
    public void The_document_shows_the_issue_and_due_dates_as_they_were_typed()
    {
        var adviser = Bakery("ON");
        adviser.TimeZone = Toronto;
        var document = Document(0.13m, ("Birthday cake", 80m, true));
        document.IssueDate = new DateTime(2026, 10, 11);                          // what the editor's date box stores
        document.DueDate = new DateTime(2026, 10, 26);
        document.Status = ClientInvoiceStatus.Paid;
        document.PaidAt = new DateTime(2026, 10, 12, 2, 30, 0, DateTimeKind.Utc);  // 10:30 in the evening of the 11th in Toronto

        var view = ClientDocumentPresentation.From(document, adviser, null);

        Assert.Equal("October 11, 2026", view.IssueDate);                         // was October 10
        Assert.Equal("October 26, 2026", view.DueDate);                           // was October 25
        Assert.Equal("October 11, 2026", view.PaidOn);                            // a payment is a moment: still the adviser's day
    }

    [Fact]
    public async Task A_new_invoice_is_dated_the_advisers_own_today()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, province: "ON");
        var agent = await db.AgentUsers.SingleAsync(a => a.Id == agentId);
        agent.TimeZone = "(GMT-08:00) Pacific Time (US & Canada)";                // a third of every day, its date is not the UTC date
        await db.SaveChangesAsync();
        var today = AgentLocalTime.FromUtc(DateTime.UtcNow, agent.TimeZone).Date;

        var model = Assert.IsType<ClientInvoiceEditViewModel>(Assert.IsType<ViewResult>(await NewInvoicesController(db, agentId).Create()).Model);
        Assert.Equal(today, model.IssueDate);
        Assert.Equal(today.AddDays(15), model.DueDate);

        var controller = Read(@"src\IPRO.Web\Controllers\ClientInvoicesController.cs");
        Assert.DoesNotContain("DateTime.UtcNow.Date", controller);                // new, converted and duplicated documents alike
        Assert.Contains("var today = AgentLocalTime.FromUtc(DateTime.UtcNow, business?.TimeZone).Date;", Read(@"src\IPRO.Scheduler\RecurringClientInvoiceJob.cs"));
    }

    // ---- saving, against MySQL -----------------------------------------------------------------------

    private static ClientInvoiceEditViewModel Order(int clientId) => new()
    {
        ClientId = clientId,
        IssueDate = new DateTime(2026, 10, 11),
        DueDate = new DateTime(2026, 10, 26),
        LineItems =
        {
            new ClientInvoiceLineItemInputModel { Description = "Sourdough loaves", Quantity = 10, UnitPrice = 6.50m, IsTaxable = false },
            new ClientInvoiceLineItemInputModel { Description = "Birthday cake", Quantity = 1, UnitPrice = 80m, IsTaxable = true },
        }
    };

    [Fact]
    public async Task Saving_an_invoice_taxes_the_ticked_lines_only_and_a_copy_keeps_the_ticks()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedOntarioAsync(db);
        var agentId = await SeedAgentAsync(db, province: "ON");
        var clientId = await SeedClientAsync(db, agentId, province: "ON");
        var controller = NewInvoicesController(db, agentId);

        Assert.IsType<RedirectToActionResult>(await controller.Create(Order(clientId)));
        var invoice = await db.ClientInvoices.Include(i => i.LineItems).SingleAsync(i => i.AgentUserId == agentId);
        Assert.Equal((145.00m, 10.40m, 155.40m, 0.13m, "ON 13% HST"), (invoice.SubTotal, invoice.TaxAmount, invoice.Total, invoice.TaxRate, invoice.TaxRegion));
        Assert.Equal(new[] { false, true }, invoice.LineItems.OrderBy(l => l.SortOrder).Select(l => l.IsTaxable).ToArray());

        // The editor opens with each line's tick as it was saved.
        var edit = Assert.IsType<ClientInvoiceEditViewModel>(Assert.IsType<ViewResult>(await controller.Edit(invoice.Id)).Model);
        Assert.Equal(new[] { false, true }, edit.LineItems.Select(l => l.IsTaxable).ToArray());

        // Tick the bread, untick the cake: the tax follows.
        edit.LineItems[0].IsTaxable = true;
        edit.LineItems[1].IsTaxable = false;
        Assert.IsType<RedirectToActionResult>(await controller.Edit(invoice.Id, edit));
        await using (var fresh = testDb.CreateContext())
        {
            var saved = await fresh.ClientInvoices.Include(i => i.LineItems).SingleAsync(i => i.Id == invoice.Id);
            Assert.Equal((145.00m, 8.45m, 153.45m), (saved.SubTotal, saved.TaxAmount, saved.Total));
            Assert.Equal(new[] { true, false }, saved.LineItems.OrderBy(l => l.SortOrder).Select(l => l.IsTaxable).ToArray());
        }

        // No line ticked: no tax, and the invoice says so rather than carrying a rate with nothing under it.
        edit.LineItems[0].IsTaxable = false;
        Assert.IsType<RedirectToActionResult>(await controller.Edit(invoice.Id, edit));
        await using (var fresh = testDb.CreateContext())
        {
            var saved = await fresh.ClientInvoices.SingleAsync(i => i.Id == invoice.Id);
            Assert.Equal((145.00m, 0m, 145.00m, 0m), (saved.SubTotal, saved.TaxAmount, saved.Total, saved.TaxRate));
        }

        // A duplicate keeps the lines' ticks and is taxed afresh.
        edit.LineItems[1].IsTaxable = true;
        Assert.IsType<RedirectToActionResult>(await controller.Edit(invoice.Id, edit));
        Assert.IsType<RedirectToActionResult>(await controller.Duplicate(invoice.Id));
        await using (var fresh = testDb.CreateContext())
        {
            var copy = await fresh.ClientInvoices.Include(i => i.LineItems).SingleAsync(i => i.AgentUserId == agentId && i.Id != invoice.Id);
            Assert.Equal((145.00m, 10.40m, 155.40m), (copy.SubTotal, copy.TaxAmount, copy.Total));
            Assert.Equal(new[] { false, true }, copy.LineItems.OrderBy(l => l.SortOrder).Select(l => l.IsTaxable).ToArray());
        }
    }

    [Fact]
    public async Task A_client_with_no_province_is_taxed_at_the_businesss_rate_and_the_editor_is_told_which_rule_applies()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedOntarioAsync(db);
        var bakeryId = await SeedAgentAsync(db, province: "ON");
        var walkIn = await SeedClientAsync(db, bakeryId, province: "");
        var controller = NewInvoicesController(db, bakeryId);

        // What the editor is handed for each client: the rule the save will apply, with its reason.
        Assert.IsType<ViewResult>(await controller.Create());
        var rules = Assert.IsAssignableFrom<IDictionary<int, ClientInvoiceTaxRule>>((object)controller.ViewBag.TaxByClient);
        Assert.Equal((0.13m, ClientInvoiceTaxRules.FromBusiness), (rules[walkIn].Rate, rules[walkIn].Source));

        Assert.IsType<RedirectToActionResult>(await controller.Create(Order(walkIn)));
        var invoice = await db.ClientInvoices.SingleAsync(i => i.AgentUserId == bakeryId);
        Assert.Equal((145.00m, 10.40m, 155.40m, "ON 13% HST"), (invoice.SubTotal, invoice.TaxAmount, invoice.Total, invoice.TaxRegion));

        // A business with no province of its own has no rate to lend: no tax, as before, and now a reason.
        var nowhereId = await SeedAgentAsync(db, province: "");
        var stranger = await SeedClientAsync(db, nowhereId, province: "");
        var other = NewInvoicesController(db, nowhereId);
        Assert.IsType<ViewResult>(await other.Create());
        var otherRules = Assert.IsAssignableFrom<IDictionary<int, ClientInvoiceTaxRule>>((object)other.ViewBag.TaxByClient);
        Assert.Equal(ClientInvoiceTaxRules.None, otherRules[stranger].Source);
        Assert.False(string.IsNullOrWhiteSpace(otherRules[stranger].Note));
        Assert.IsType<RedirectToActionResult>(await other.Create(Order(stranger)));
        Assert.Equal(0m, (await db.ClientInvoices.SingleAsync(i => i.AgentUserId == nowhereId)).TaxAmount);
    }

    [Fact]
    public async Task The_file_for_Xero_marks_an_unticked_line_exempt()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedOntarioAsync(db);
        var agentId = await SeedAgentAsync(db, province: "ON");
        var clientId = await SeedClientAsync(db, agentId, province: "ON");
        var controller = NewInvoicesController(db, agentId);
        Assert.IsType<RedirectToActionResult>(await controller.Create(Order(clientId)));
        var invoice = await db.ClientInvoices.SingleAsync(i => i.AgentUserId == agentId);
        invoice.Status = ClientInvoiceStatus.Sent;
        await db.SaveChangesAsync();

        var file = Assert.IsType<FileContentResult>(await controller.ExportXero(2026, 10));
        var lines = Encoding.UTF8.GetString(file.FileContents).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains(lines, l => l.Contains(",Sourdough loaves,") && l.Contains(",Tax Exempt,0.00,"));
        Assert.Contains(lines, l => l.Contains(",Birthday cake,") && l.Contains(",10.40,") && !l.Contains("Tax Exempt"));
    }

    [Fact]
    public async Task A_recurring_schedule_keeps_each_lines_tick_on_the_invoices_it_makes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedOntarioAsync(db);
        var agentId = await SeedAgentAsync(db, province: "ON");
        var clientId = await SeedClientAsync(db, agentId, province: "");      // the standing order of a walk-in customer
        db.RecurringInvoiceSchedules.Add(new RecurringInvoiceSchedule
        {
            AgentUserId = agentId, ClientId = clientId, Frequency = RecurringInvoiceFrequency.Monthly,
            NextRunDate = DateTime.UtcNow.AddMinutes(-5), DueInDays = 15, IsActive = true,
            LineItems =
            {
                new RecurringInvoiceLineItem { Description = "Sourdough loaves", Quantity = 10, UnitPrice = 6.50m, IsTaxable = false, SortOrder = 0 },
                new RecurringInvoiceLineItem { Description = "Birthday cake", Quantity = 1, UnitPrice = 80m, IsTaxable = true, SortOrder = 1 },
            }
        });
        await db.SaveChangesAsync();

        var service = new ClientInvoiceService(new IPRO.DataAccess.Repositories.UnitOfWork(db));
        await new IPRO.Scheduler.RecurringClientInvoiceJob(db, service, NullLogger<IPRO.Scheduler.RecurringClientInvoiceJob>.Instance).RunAsync();

        await using var fresh = testDb.CreateContext();
        var invoice = await fresh.ClientInvoices.Include(i => i.LineItems).SingleAsync(i => i.AgentUserId == agentId);
        Assert.Equal((145.00m, 10.40m, 155.40m, "ON 13% HST"), (invoice.SubTotal, invoice.TaxAmount, invoice.Total, invoice.TaxRegion));
        Assert.Equal(new[] { false, true }, invoice.LineItems.OrderBy(l => l.SortOrder).Select(l => l.IsTaxable).ToArray());
    }

    [Fact]
    public async Task A_database_from_before_gains_the_column_and_every_existing_line_is_taxed_as_it_was()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedOntarioAsync(db);
        var agentId = await SeedAgentAsync(db, province: "ON");
        var clientId = await SeedClientAsync(db, agentId, province: "ON");
        Assert.IsType<RedirectToActionResult>(await NewInvoicesController(db, agentId).Create(Order(clientId)));

        // Yesterday's tables: no such column on either.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE `ClientInvoiceLineItems` DROP COLUMN `IsTaxable`");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE `RecurringInvoiceLineItems` DROP COLUMN `IsTaxable`");

        await StartupSchemaRepair.EnsureClientInvoiceSchemaAsync(db);
        await StartupSchemaRepair.EnsureClientInvoiceSchemaAsync(db);           // and again: nothing to do, nothing thrown

        await using var fresh = testDb.CreateContext();
        var lines = await fresh.ClientInvoiceLineItems.AsNoTracking().ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(l.IsTaxable));                       // the bread too: before the tick, every line was taxed
        Assert.Empty(await fresh.RecurringInvoiceLineItems.AsNoTracking().ToListAsync());
    }

    // ---- what the screens say ------------------------------------------------------------------------

    [Fact]
    public void The_editor_has_a_tick_on_every_line_and_shows_the_totals_as_it_is_filled_in()
    {
        var editor = Read(@"src\IPRO.Web\Views\ClientInvoices\Edit.cshtml");
        // A ticked box posts "true"; the hidden field after it answers "false" for an empty box.
        Assert.Contains("name=\"LineItems[@i].IsTaxable\" value=\"true\" checked=\"@lineItems[i].IsTaxable\"", editor);
        Assert.Contains("<input type=\"hidden\" name=\"LineItems[@i].IsTaxable\" value=\"false\" />", editor);
        Assert.Contains("name=\"LineItems[' + nextIndex + '].IsTaxable\" value=\"true\" checked", editor);     // a line added in the browser
        Assert.Contains("style=\"width:56px\"", editor);                                                        // the narrow column
        foreach (var id in new[] { "id=\"totalsSubtotal\"", "id=\"totalsTax\"", "id=\"totalsTotal\"", "id=\"totalsTaxNote\"" })
            Assert.Contains(id, editor);
        Assert.Contains("data-tax-rate=", editor);
        Assert.Contains("'No tax will be charged on this '", editor);                                           // said before the invoice goes out

        var recurring = Read(@"src\IPRO.Web\Views\RecurringInvoices\Edit.cshtml");
        Assert.Contains("name=\"LineItems[@i].IsTaxable\" value=\"true\" checked=\"@lineItems[i].IsTaxable\"", recurring);
        Assert.Contains("<input type=\"hidden\" name=\"LineItems[@i].IsTaxable\" value=\"false\" />", recurring);

        // On a phone the tick is on the screen, under its own label, not a sideways scroll away: each
        // line is two rows, by one set of styles both editors share.
        var styles = Read(@"src\IPRO.Web\Views\Shared\_InvoiceLineStyles.cshtml");
        Assert.Contains("@@media (max-width: 575.98px) {", styles);            // doubled: a single @ is Razor's, and the build fails on it
        Assert.Contains("#lineItemsTable tbody td[data-label]::before { content: attr(data-label);", styles);
        foreach (var view in new[] { editor, recurring })
        {
            Assert.Contains("<partial name=\"_InvoiceLineStyles\" />", view);
            Assert.Equal(2, view.Split("line-taxed\" data-label=\"Tax\">").Length - 1);      // the row, and the row the script adds
            Assert.Equal(2, view.Split("class=\"line-qty\" data-label=\"Qty\">").Length - 1);
        }
    }

    [Fact]
    public void The_document_shows_a_box_per_line_in_a_narrow_column_and_the_summary_names_the_tax()
    {
        var document = Read(@"src\IPRO.Web\Views\ClientInvoices\_ClientInvoiceDocument.cshtml");
        Assert.Contains("<th scope=\"col\" class=\"col-tax\">Tax</th>", document);
        Assert.Contains("<td data-label=\"Tax\" class=\"col-tax\"><span class=\"tax-box", document);
        Assert.DoesNotContain("<td data-label=\"Tax\">@line.TaxLabel</td>", document);       // the rate is no longer repeated on every line
        Assert.Contains("<p><span>Taxable amount</span><strong>@Money(view.TaxableAmount)</strong></p>", document);
        Assert.Contains("<p><span>@view.TaxLabel</span><strong>@Money(Model.TaxAmount)</strong></p>", document);

        var css = Read(@"src\IPRO.Web\wwwroot\css\invoice.css");
        Assert.Contains("td.col-tax {", css);
        Assert.Contains("td .tax-box {", css);

        var repair = Read(@"src\IPRO.DataAccess\StartupSchemaRepair.cs");
        Assert.Contains("ADD COLUMN `IsTaxable` tinyint(1) NOT NULL DEFAULT 1\");", repair);
        Assert.Equal(2, repair.Split("ADD COLUMN `IsTaxable` tinyint(1) NOT NULL DEFAULT 1").Length - 1);   // both line tables

        Assert.Contains("### Tax on each line", Read(@"DOCS\10_CLIENT_INVOICING.md"));
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static async Task SeedOntarioAsync(IPRODbContext db)
    {
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "ON"))
        {
            db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", Rate = 0.13m, TaxLabel = "13% HST", IsActive = true });
            await db.SaveChangesAsync();
        }
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string province)
    {
        var rule = new BillingRule { PackageName = ($"T563-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t563-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Corner",
            LastName = "Baker",
            CompanyName = "Corner Bakery",
            Province = province,
            DomainName = ($"t563-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> SeedClientAsync(IPRODbContext db, int agentId, string province)
    {
        var client = new Client { AgentUserId = agentId, FirstName = "Ava", LastName = "Chen", Email = $"{Guid.NewGuid():N}@example.test", Province = province };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
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
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
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
