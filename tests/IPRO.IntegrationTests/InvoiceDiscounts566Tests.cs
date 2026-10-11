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

// 566 (2026-10-11): a discount on an invoice to a client. The bakery wanted "one discount line for
// the whole invoice"; the owner wanted both for other businesses: "apply discount to whole or per
// item". So the editor has one choice -- None, Whole invoice, Each item -- and the arithmetic is one
// class (ClientInvoiceMath) that the save, a duplicate and the recurring job all use, and that the
// editor's script follows step for step.
//
// 567, with it: on the client's document the figures' columns sit together at the right (the owner's
// two-line invoice had wide gaps between Qty, Unit price and Amount).
public class InvoiceDiscounts566Tests
{
    // ---- the arithmetic ------------------------------------------------------------------------------

    [Fact]
    public void A_discount_is_a_percent_or_dollars_and_never_more_than_what_it_comes_off()
    {
        Assert.Equal(14.50m, ClientInvoiceMath.Discount(145m, InvoiceDiscountType.Percent, 10m));
        Assert.Equal(0.07m, ClientInvoiceMath.Discount(0.65m, InvoiceDiscountType.Percent, 10m));     // 0.065, away from zero
        Assert.Equal(48.33m, ClientInvoiceMath.Discount(145m, InvoiceDiscountType.Percent, 33.33m));   // 48.3285
        Assert.Equal(145m, ClientInvoiceMath.Discount(145m, InvoiceDiscountType.Percent, 250m));       // a percent stops at a hundred
        Assert.Equal(20m, ClientInvoiceMath.Discount(145m, InvoiceDiscountType.Amount, 20m));
        Assert.Equal(145m, ClientInvoiceMath.Discount(145m, InvoiceDiscountType.Amount, 500m));        // never below nothing
        Assert.Equal(0m, ClientInvoiceMath.Discount(145m, InvoiceDiscountType.None, 10m));
        Assert.Equal(0m, ClientInvoiceMath.Discount(145m, InvoiceDiscountType.Percent, 0m));
        Assert.Equal(0m, ClientInvoiceMath.Discount(145m, InvoiceDiscountType.Percent, -5m));
        Assert.Equal(0m, ClientInvoiceMath.Discount(-25m, InvoiceDiscountType.Percent, 10m));          // nothing off a credit
        Assert.Equal(0m, ClientInvoiceMath.Discount(0m, InvoiceDiscountType.Amount, 10m));

        Assert.Equal(65m, ClientInvoiceMath.LineGross(10m, 6.50m));
        Assert.Equal(80m, ClientInvoiceMath.LineGross(0m, 80m));                                       // a quantity of nothing is one
        Assert.Equal(("10%", "12.5%", "$5.00", "", ""), (ClientInvoiceMath.Words(InvoiceDiscountType.Percent, 10m), ClientInvoiceMath.Words(InvoiceDiscountType.Percent, 12.50m),
            ClientInvoiceMath.Words(InvoiceDiscountType.Amount, 5m), ClientInvoiceMath.Words(InvoiceDiscountType.None, 5m), ClientInvoiceMath.Words(InvoiceDiscountType.Percent, 0m)));
        Assert.Equal(("none", "invoice", "lines", "none"), (ClientInvoiceMath.CleanMode(null), ClientInvoiceMath.CleanMode(" Invoice "), ClientInvoiceMath.CleanMode("LINES"), ClientInvoiceMath.CleanMode("everything")));
    }

    [Fact]
    public void A_discount_on_the_whole_invoice_is_shared_between_the_taxed_and_the_untaxed_lines()
    {
        // The bakery: bread (not taxed) and a cake (taxed), ten percent off the lot.
        var lines = new[] { (Amount: 65m, IsTaxable: false), (Amount: 80m, IsTaxable: true) };

        var none = ClientInvoiceMath.Totals(lines, InvoiceDiscountType.None, 0m);
        Assert.Equal((145m, 0m, 145m, 80m), (none.ItemsTotal, none.InvoiceDiscount, none.SubTotal, none.TaxableAmount));

        var tenPercent = ClientInvoiceMath.Totals(lines, InvoiceDiscountType.Percent, 10m);
        Assert.Equal((145m, 14.50m, 130.50m, 72.00m), (tenPercent.ItemsTotal, tenPercent.InvoiceDiscount, tenPercent.SubTotal, tenPercent.TaxableAmount));
        Assert.Equal(9.36m, ClientInvoiceTaxRules.TaxOn(tenPercent.TaxableAmount, 0.13m));             // tax on the cake after its share

        var twenty = ClientInvoiceMath.Totals(lines, InvoiceDiscountType.Amount, 20m);
        Assert.Equal((20m, 125m, 68.97m), (twenty.InvoiceDiscount, twenty.SubTotal, twenty.TaxableAmount));   // the cake's share of $20 is 11.03

        // Everything taxed: the whole discount comes off what is taxed. Nothing taxed: nothing to share.
        Assert.Equal(130.50m, ClientInvoiceMath.Totals(new[] { (65m, true), (80m, true) }, InvoiceDiscountType.Percent, 10m).TaxableAmount);
        Assert.Equal(0m, ClientInvoiceMath.Totals(new[] { (65m, false), (80m, false) }, InvoiceDiscountType.Percent, 10m).TaxableAmount);
        // An invoice that comes to nothing, or to a credit, takes no discount.
        Assert.Equal(0m, ClientInvoiceMath.Totals(new[] { (50m, true), (-50m, true) }, InvoiceDiscountType.Percent, 10m).InvoiceDiscount);
    }

    // ---- saving, against MySQL -----------------------------------------------------------------------

    private static ClientInvoiceEditViewModel Order(int clientId, string mode = "none", InvoiceDiscountType type = InvoiceDiscountType.Percent, decimal? value = null,
        (InvoiceDiscountType Type, decimal? Value) bread = default, (InvoiceDiscountType Type, decimal? Value) cake = default) => new()
    {
        ClientId = clientId,
        IssueDate = new DateTime(2026, 10, 11),
        DueDate = new DateTime(2026, 10, 26),
        DiscountMode = mode, DiscountType = type, DiscountValue = value,
        LineItems =
        {
            new ClientInvoiceLineItemInputModel { Description = "Sourdough loaves", Quantity = 10, UnitPrice = 6.50m, IsTaxable = false, DiscountType = bread.Type, DiscountValue = bread.Value },
            new ClientInvoiceLineItemInputModel { Description = "Birthday cake", Quantity = 1, UnitPrice = 80m, IsTaxable = true, DiscountType = cake.Type, DiscountValue = cake.Value },
        }
    };

    [Fact]
    public async Task One_discount_on_the_whole_invoice_comes_off_before_tax_and_the_totals_still_add_up()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedAsync(db);
        var controller = NewInvoicesController(db, agentId);

        // The line boxes are filled in too: on "invoice" they are not read.
        Assert.IsType<RedirectToActionResult>(await controller.Create(Order(clientId, "invoice", InvoiceDiscountType.Percent, 10m,
            bread: (InvoiceDiscountType.Amount, 5m), cake: (InvoiceDiscountType.Percent, 50m))));
        var invoice = await db.ClientInvoices.Include(i => i.LineItems).SingleAsync(i => i.AgentUserId == agentId);
        Assert.Equal((InvoiceDiscountType.Percent, 10m, 14.50m), (invoice.DiscountType, invoice.DiscountValue, invoice.DiscountAmount));
        Assert.Equal((130.50m, 9.36m, 139.86m), (invoice.SubTotal, invoice.TaxAmount, invoice.Total));
        Assert.Equal(invoice.Total, invoice.SubTotal + invoice.TaxAmount);                             // as every report expects
        Assert.Equal(new[] { 65m, 80m }, invoice.LineItems.OrderBy(l => l.SortOrder).Select(l => l.Amount).ToArray());
        Assert.All(invoice.LineItems, l => Assert.Equal((InvoiceDiscountType.None, 0m, 0m), (l.DiscountType, l.DiscountValue, l.DiscountAmount)));

        // The editor opens on what was saved.
        var edit = Assert.IsType<ClientInvoiceEditViewModel>(Assert.IsType<ViewResult>(await controller.Edit(invoice.Id)).Model);
        Assert.Equal(("invoice", InvoiceDiscountType.Percent, (decimal?)10m), (edit.DiscountMode, edit.DiscountType, edit.DiscountValue));

        // Twenty dollars instead.
        edit.DiscountType = InvoiceDiscountType.Amount;
        edit.DiscountValue = 20m;
        Assert.IsType<RedirectToActionResult>(await controller.Edit(invoice.Id, edit));
        await using (var fresh = testDb.CreateContext())
        {
            var saved = await fresh.ClientInvoices.SingleAsync(i => i.Id == invoice.Id);
            Assert.Equal((InvoiceDiscountType.Amount, 20m, 20m, 125m, 8.97m, 133.97m), (saved.DiscountType, saved.DiscountValue, saved.DiscountAmount, saved.SubTotal, saved.TaxAmount, saved.Total));
        }

        // None again, or a box left empty: no discount, and nothing left behind.
        edit.DiscountMode = "none";
        Assert.IsType<RedirectToActionResult>(await controller.Edit(invoice.Id, edit));
        await using (var fresh = testDb.CreateContext())
        {
            var saved = await fresh.ClientInvoices.SingleAsync(i => i.Id == invoice.Id);
            Assert.Equal((InvoiceDiscountType.None, 0m, 0m, 145m, 10.40m, 155.40m), (saved.DiscountType, saved.DiscountValue, saved.DiscountAmount, saved.SubTotal, saved.TaxAmount, saved.Total));
        }
        edit.DiscountMode = "invoice";
        edit.DiscountValue = null;
        Assert.IsType<RedirectToActionResult>(await controller.Edit(invoice.Id, edit));
        await using (var fresh = testDb.CreateContext())
        {
            Assert.Equal((InvoiceDiscountType.None, 0m, 155.40m), await fresh.ClientInvoices.Where(i => i.Id == invoice.Id).Select(i => new ValueTuple<InvoiceDiscountType, decimal, decimal>(i.DiscountType, i.DiscountAmount, i.Total)).SingleAsync());
        }
    }

    [Fact]
    public async Task A_discount_on_each_item_comes_off_its_line_and_a_copy_keeps_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedAsync(db);
        var controller = NewInvoicesController(db, agentId);

        // Five dollars off the bread, ten percent off the cake; the whole-invoice box is filled in and not read.
        Assert.IsType<RedirectToActionResult>(await controller.Create(Order(clientId, "lines", InvoiceDiscountType.Percent, 50m,
            bread: (InvoiceDiscountType.Amount, 5m), cake: (InvoiceDiscountType.Percent, 10m))));
        var invoice = await db.ClientInvoices.Include(i => i.LineItems).SingleAsync(i => i.AgentUserId == agentId);
        var lines = invoice.LineItems.OrderBy(l => l.SortOrder).ToList();
        Assert.Equal((InvoiceDiscountType.Amount, 5m, 5m, 60m), (lines[0].DiscountType, lines[0].DiscountValue, lines[0].DiscountAmount, lines[0].Amount));
        Assert.Equal((InvoiceDiscountType.Percent, 10m, 8m, 72m), (lines[1].DiscountType, lines[1].DiscountValue, lines[1].DiscountAmount, lines[1].Amount));
        Assert.Equal((InvoiceDiscountType.None, 0m, 132m, 9.36m, 141.36m), (invoice.DiscountType, invoice.DiscountAmount, invoice.SubTotal, invoice.TaxAmount, invoice.Total));

        var edit = Assert.IsType<ClientInvoiceEditViewModel>(Assert.IsType<ViewResult>(await controller.Edit(invoice.Id)).Model);
        Assert.Equal("lines", edit.DiscountMode);
        Assert.Equal(new[] { (InvoiceDiscountType.Amount, (decimal?)5m), (InvoiceDiscountType.Percent, (decimal?)10m) }, edit.LineItems.Select(l => (l.DiscountType, l.DiscountValue)).ToArray());

        Assert.IsType<RedirectToActionResult>(await controller.Duplicate(invoice.Id));
        await using var fresh = testDb.CreateContext();
        var copy = await fresh.ClientInvoices.Include(i => i.LineItems).SingleAsync(i => i.AgentUserId == agentId && i.Id != invoice.Id);
        Assert.Equal((132m, 9.36m, 141.36m), (copy.SubTotal, copy.TaxAmount, copy.Total));
        Assert.Equal(new[] { 5m, 8m }, copy.LineItems.OrderBy(l => l.SortOrder).Select(l => l.DiscountAmount).ToArray());
    }

    [Fact]
    public async Task The_files_for_the_accountant_add_up_to_the_invoice()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedAsync(db);
        var controller = NewInvoicesController(db, agentId);
        Assert.IsType<RedirectToActionResult>(await controller.Create(Order(clientId, "invoice", InvoiceDiscountType.Percent, 10m)));
        Assert.IsType<RedirectToActionResult>(await controller.Create(Order(clientId, "lines", cake: (InvoiceDiscountType.Percent, 10m), bread: (InvoiceDiscountType.Amount, 5m))));
        foreach (var invoice in await db.ClientInvoices.Where(i => i.AgentUserId == agentId).ToListAsync())
            invoice.Status = ClientInvoiceStatus.Sent;
        await db.SaveChangesAsync();
        var whole = await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.AgentUserId == agentId && i.DiscountAmount > 0);
        var perItem = await db.ClientInvoices.AsNoTracking().SingleAsync(i => i.AgentUserId == agentId && i.DiscountAmount == 0);

        List<string> Lines(IActionResult file) => Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(file).FileContents)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

        // Xero: the discount on the whole invoice is two lines, the taxed share and the rest.
        var xero = Lines(await controller.ExportXero(2026, 10));
        var mine = xero.Where(l => l.Contains($",{whole.DocumentNumber},")).ToList();
        Assert.Equal(4, mine.Count);
        Assert.Contains(mine, l => l.Contains(",Discount 10%,1,-8,,200,") && l.Contains(",-1.04,"));     // the cake's share, and its tax back
        Assert.Contains(mine, l => l.Contains(",Discount 10%,1,-6.5,,200,Tax Exempt,0.00,"));
        // Per item: a percent goes in Xero's own Discount column; dollars off become the unit amount.
        var theirs = xero.Where(l => l.Contains($",{perItem.DocumentNumber},")).ToList();
        Assert.Equal(2, theirs.Count);
        Assert.Contains(theirs, l => l.Contains(",Birthday cake,1,80,10,200,"));
        Assert.Contains(theirs, l => l.Contains(",Sourdough loaves,10,6,,200,Tax Exempt,0.00,"));

        // QuickBooks: each invoice's rows add up to what the client is billed before tax.
        var quickBooks = Lines(await controller.ExportQuickBooks());
        decimal Sum(string number) => quickBooks.Where(l => l.StartsWith(number + ",")).Sum(l => decimal.Parse(l.Split(',')[9], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(whole.SubTotal, Sum(whole.DocumentNumber));
        Assert.Equal(perItem.SubTotal, Sum(perItem.DocumentNumber));
        Assert.Contains(quickBooks, l => l.StartsWith(whole.DocumentNumber + ",") && l.Contains(",Discount 10%,1,-14.50,-14.50,"));
        Assert.Contains(quickBooks, l => l.StartsWith(perItem.DocumentNumber + ",") && l.Contains(",Sourdough loaves,10,6.00,60.00,"));
    }

    [Fact]
    public async Task A_recurring_schedule_carries_its_discount_onto_every_invoice_it_makes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedAsync(db);
        var entitlements = new GrantAll();
        var recurring = new IPRO.Web.Controllers.RecurringInvoicesController(db, entitlements);
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test")) };
        recurring.ControllerContext = new ControllerContext { HttpContext = ctx };
        recurring.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());

        var order = Order(clientId, "invoice", InvoiceDiscountType.Percent, 10m);
        Assert.IsType<RedirectToActionResult>(await recurring.Create(new RecurringInvoiceEditViewModel
        {
            ClientId = clientId, Frequency = RecurringInvoiceFrequency.Monthly, NextRunDate = DateTime.UtcNow.AddMinutes(-5), DueInDays = 15,
            DiscountMode = order.DiscountMode, DiscountType = order.DiscountType, DiscountValue = order.DiscountValue, LineItems = order.LineItems
        }));
        var schedule = await db.RecurringInvoiceSchedules.Include(s => s.LineItems).SingleAsync(s => s.AgentUserId == agentId);
        Assert.Equal((InvoiceDiscountType.Percent, 10m), (schedule.DiscountType, schedule.DiscountValue));

        // The editor opens on it, and is handed each client's tax rule for the figures it shows.
        var view = Assert.IsType<ViewResult>(await recurring.Edit(schedule.Id));
        Assert.Equal("invoice", Assert.IsType<RecurringInvoiceEditViewModel>(view.Model).DiscountMode);
        Assert.Equal(0.13m, Assert.IsAssignableFrom<IDictionary<int, ClientInvoiceTaxRule>>((object)recurring.ViewBag.TaxByClient)[clientId].Rate);

        var service = new ClientInvoiceService(new IPRO.DataAccess.Repositories.UnitOfWork(db));
        await new IPRO.Scheduler.RecurringClientInvoiceJob(db, service, NullLogger<IPRO.Scheduler.RecurringClientInvoiceJob>.Instance).RunAsync();
        await using var fresh = testDb.CreateContext();
        var invoice = await fresh.ClientInvoices.SingleAsync(i => i.AgentUserId == agentId);
        Assert.Equal((InvoiceDiscountType.Percent, 10m, 14.50m, 130.50m, 9.36m, 139.86m),
            (invoice.DiscountType, invoice.DiscountValue, invoice.DiscountAmount, invoice.SubTotal, invoice.TaxAmount, invoice.Total));
    }

    [Fact]
    public async Task A_database_from_before_gains_the_columns_and_nothing_old_has_a_discount()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, clientId) = await SeedAsync(db);
        Assert.IsType<RedirectToActionResult>(await NewInvoicesController(db, agentId).Create(Order(clientId)));

        foreach (var (table, columns) in new[] { ("ClientInvoices", new[] { "DiscountType", "DiscountValue", "DiscountAmount" }), ("ClientInvoiceLineItems", new[] { "DiscountType", "DiscountValue", "DiscountAmount" }),
                     ("RecurringInvoiceSchedules", new[] { "DiscountType", "DiscountValue" }), ("RecurringInvoiceLineItems", new[] { "DiscountType", "DiscountValue" }) })
            foreach (var column in columns)
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE `{table}` DROP COLUMN `{column}`");

        await StartupSchemaRepair.EnsureClientInvoiceSchemaAsync(db);
        await StartupSchemaRepair.EnsureClientInvoiceSchemaAsync(db);           // and again: nothing to do, nothing thrown

        await using var fresh = testDb.CreateContext();
        var invoice = await fresh.ClientInvoices.Include(i => i.LineItems).AsNoTracking().SingleAsync(i => i.AgentUserId == agentId);
        Assert.Equal((InvoiceDiscountType.None, 0m, 0m, 155.40m), (invoice.DiscountType, invoice.DiscountValue, invoice.DiscountAmount, invoice.Total));
        Assert.All(invoice.LineItems, l => Assert.Equal((InvoiceDiscountType.None, 0m), (l.DiscountType, l.DiscountAmount)));
        Assert.Equal(0, (int)InvoiceDiscountType.None);                         // the column's default must mean "none"
    }

    // ---- the document --------------------------------------------------------------------------------

    private static ClientInvoice Document(decimal invoiceDiscount = 0m, InvoiceDiscountType type = InvoiceDiscountType.None, decimal value = 0m, bool onItems = false)
    {
        var document = new ClientInvoice
        {
            DocumentNumber = "INV-1001", DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent, IssueDate = new DateTime(2026, 10, 11),
            DiscountType = type, DiscountValue = value, DiscountAmount = invoiceDiscount, TaxRegion = "ON 13% HST", TaxRate = 0.13m, TaxAmount = 9.36m,
            Client = new Client { FirstName = "Ava", LastName = "Chen", Province = "ON" }
        };
        document.LineItems.Add(new ClientInvoiceLineItem { Description = "Sourdough loaves", Quantity = 10, UnitPrice = 6.50m, IsTaxable = false, SortOrder = 0,
            DiscountType = onItems ? InvoiceDiscountType.Amount : InvoiceDiscountType.None, DiscountValue = onItems ? 5m : 0m, DiscountAmount = onItems ? 5m : 0m, Amount = onItems ? 60m : 65m });
        document.LineItems.Add(new ClientInvoiceLineItem { Description = "Birthday cake", Quantity = 1, UnitPrice = 80m, IsTaxable = true, SortOrder = 1,
            DiscountType = onItems ? InvoiceDiscountType.Percent : InvoiceDiscountType.None, DiscountValue = onItems ? 10m : 0m, DiscountAmount = onItems ? 8m : 0m, Amount = onItems ? 72m : 80m });
        document.SubTotal = document.LineItems.Sum(l => l.Amount) - invoiceDiscount;
        document.Total = document.SubTotal + document.TaxAmount;
        return document;
    }

    [Fact]
    public void The_document_shows_a_discount_on_the_whole_invoice_between_the_subtotal_and_the_tax()
    {
        var view = ClientDocumentPresentation.From(Document(14.50m, InvoiceDiscountType.Percent, 10m), new AgentUser { CompanyName = "Corner Bakery" }, null);
        Assert.True(view.HasInvoiceDiscount);
        Assert.False(view.HasLineDiscounts);
        Assert.Equal((145m, "Discount (10%)", 14.50m, 72.00m), (view.ItemsTotal, view.DiscountLabel, view.DiscountTotal, view.TaxableAmount));
        Assert.All(view.Lines, l => Assert.Equal(string.Empty, l.Discount));
        Assert.Equal(new[] { 65m, 80m }, view.Lines.Select(l => l.Amount).ToArray());

        var dollars = ClientDocumentPresentation.From(Document(20m, InvoiceDiscountType.Amount, 20m), new AgentUser(), null);
        Assert.Equal(("Discount", 68.97m), (dollars.DiscountLabel, dollars.TaxableAmount));

        var plain = ClientDocumentPresentation.From(Document(), new AgentUser(), null);
        Assert.False(plain.HasDiscount);
        Assert.Equal((145m, 80m), (plain.ItemsTotal, plain.TaxableAmount));       // an invoice from before reads as it did
    }

    [Fact]
    public void The_document_shows_a_discount_on_an_item_beside_it_and_the_sum_of_them_in_the_totals()
    {
        // The owner, on seeing the first pictures: "you did not add a line indicating the total discount
        // have been saved as a result of 1 item discount". It was a small note under the lines. Now it
        // is a line of the totals, as a discount on the whole invoice is, and the columns add up to it.
        var view = ClientDocumentPresentation.From(Document(onItems: true), new AgentUser(), null);
        Assert.True(view.HasLineDiscounts);
        Assert.False(view.HasInvoiceDiscount);
        Assert.True(view.HasDiscount);
        Assert.Equal(new[] { "-$5.00", "-$8.00 (10%)" }, view.Lines.Select(l => l.Discount).ToArray());
        Assert.Equal(new[] { 65m, 80m }, view.Lines.Select(l => l.Amount).ToArray());      // at their full prices
        Assert.Equal((145m, "Discounts", 13m, 72m), (view.ItemsTotal, view.DiscountLabel, view.DiscountTotal, view.TaxableAmount));
        Assert.Equal(view.ItemsTotal, view.Lines.Sum(l => l.Amount));                       // the Amount column adds up to the Subtotal
        Assert.Equal(145m - 13m, 132m);                                                     // and Subtotal less Discounts is what is stored
    }

    // ---- what the screens say ------------------------------------------------------------------------

    [Fact]
    public void Both_editors_use_one_lines_editor_with_one_choice_of_where_the_discount_is()
    {
        var editor = Read(@"src\IPRO.Web\Views\Shared\_InvoiceLinesEditor.cshtml");
        foreach (var choice in new[] { "id=\"discountNone\" value=\"none\"", "id=\"discountInvoice\" value=\"invoice\"", "id=\"discountLines\" value=\"lines\"" })
            Assert.Contains("name=\"DiscountMode\" " + choice, editor);
        Assert.Contains("<label class=\"btn btn-outline-secondary\" for=\"discountInvoice\">Whole @Model.DocWord</label>", editor);
        Assert.Contains("name=\"LineItems[@i].DiscountValue\" class=\"form-control line-discount-value\"", editor);
        Assert.Contains("name=\"LineItems[@i].DiscountType\" class=\"form-select line-discount-type\"", editor);
        Assert.Contains("name=\"DiscountValue\" id=\"invoiceDiscountValue\"", editor);
        Assert.Contains("name=\"DiscountType\" id=\"invoiceDiscountType\"", editor);
        // The totals show the discount for either kind; for "each item" it is the sum of the lines' own.
        Assert.Contains("document.getElementById('totalsDiscountLabel').textContent = mode === 'lines' ? 'Discounts' : 'Discount';", editor);
        Assert.Contains("document.getElementById('totalsDiscount').textContent = money(-(invoiceOff + lineDiscounts));", editor);
        // A row is written once and copied by Add Line; a removed row renumbers the selects too.
        Assert.Contains("<template id=\"lineItemTemplate\">", editor);
        Assert.Contains("row.querySelectorAll('input, select')", editor);
        // The script follows ClientInvoiceMath: a percent stops at a hundred, dollars at the amount, and
        // a discount on the whole invoice is shared with the taxed lines.
        Assert.Contains("return Math.floor((amount * Math.min(Math.round(value * 100), 10000) + 5000) / 10000);", editor);
        Assert.Contains("var taxable = invoiceOff > 0 ? taxableItems - share(invoiceOff, taxableItems, items) : taxableItems;", editor);

        foreach (var view in new[] { Read(@"src\IPRO.Web\Views\ClientInvoices\Edit.cshtml"), Read(@"src\IPRO.Web\Views\RecurringInvoices\Edit.cshtml") })
        {
            Assert.Contains("<partial name=\"_InvoiceLinesEditor\" model=\"new IPRO.Web.Models.InvoiceLinesEditorModel {", view);
            Assert.Contains("<partial name=\"_InvoiceClientSelect\" model=\"Model.ClientId\" />", view);
            Assert.DoesNotContain("<script", view);                             // one script, in the shared editor
            Assert.DoesNotContain("lineItemsTable", view);
        }
        Assert.Contains("<select name=\"ClientId\" id=\"invoiceClient\" class=\"form-select\" required>", Read(@"src\IPRO.Web\Views\Shared\_InvoiceClientSelect.cshtml"));

        var styles = Read(@"src\IPRO.Web\Views\Shared\_InvoiceLineStyles.cshtml");
        Assert.Contains("#lineItemsTable .line-discount { display: none; }", styles);
        Assert.Contains("#lineItemsTable.discount-lines .line-discount { display: table-cell; }", styles);
    }

    [Fact]
    public void The_document_has_the_discount_and_its_figures_sit_together_at_the_right()
    {
        var document = Read(@"src\IPRO.Web\Views\ClientInvoices\_ClientInvoiceDocument.cshtml");
        Assert.Contains("<td data-label=\"Discount\">@line.Discount</td>", document);
        Assert.Contains("@if (view.HasDiscount)", document);                   // either kind: the same two lines of the totals
        Assert.Contains("<div><dt>@view.DiscountLabel</dt><dd>@Money(-view.DiscountTotal)</dd></div>", document);
        Assert.Contains("<div><dt>Subtotal</dt><dd>@Money(view.ItemsTotal)</dd></div>", document);
        Assert.DoesNotContain("discount-note", document);                       // the small note under the lines is gone

        // 567: the description takes the table's spare width; on a phone each cell is a row again.
        var css = Read(@"src\IPRO.Web\wwwroot\css\invoice.css");
        Assert.Contains(".client-document .charges td:not(:first-child) {\n  width: 1%;", css.Replace("\r\n", "\n"));
        Assert.Contains("  .client-document .charges td.col-tax {\n    width: 100%;", css.Replace("\r\n", "\n"));

        var repair = Read(@"src\IPRO.DataAccess\StartupSchemaRepair.cs");
        Assert.Equal(4, repair.Split("ADD COLUMN `DiscountType` int NOT NULL DEFAULT 0").Length - 1);
        Assert.Equal(4, repair.Split("ADD COLUMN `DiscountValue` decimal(10,2) NOT NULL DEFAULT 0").Length - 1);
        Assert.Equal(2, repair.Split("ADD COLUMN `DiscountAmount` decimal(10,2) NOT NULL DEFAULT 0").Length - 1);

        Assert.Contains("### Discounts", Read(@"DOCS\10_CLIENT_INVOICING.md"));
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static async Task<(int AgentId, int ClientId)> SeedAsync(IPRODbContext db)
    {
        if (!await db.ProvinceTaxRates.AnyAsync(t => t.ProvinceCode == "ON"))
        {
            db.Add(new ProvinceTaxRate { ProvinceCode = "ON", ProvinceName = "Ontario", Rate = 0.13m, TaxLabel = "13% HST", IsActive = true });
            await db.SaveChangesAsync();
        }
        var rule = new BillingRule { PackageName = ($"T566-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t566-{Guid.NewGuid():N}")[..20], Email = $"{Guid.NewGuid():N}@example.test", FirstName = "Corner", LastName = "Baker",
            CompanyName = "Corner Bakery", Province = "ON", DomainName = ($"t566-{Guid.NewGuid():N}")[..24], PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var client = new Client { AgentUserId = agent.Id, FirstName = "Ava", LastName = "Chen", Email = $"{Guid.NewGuid():N}@example.test", Province = "ON" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return (agent.Id, client.Id);
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
