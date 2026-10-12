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
using IPRO.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 568 (2026-10-11): the invoice editor remembers items. The bakery's owner, entering the same loaves
// on every invoice, asked the owner whether he has to type an item again each time: "maybe the system
// intelligently remembers the items that had used for previous invoices". It does, from the lines the
// business has already written: no list to set up or keep. Typing in a description offers them; one
// pick fills the description, the last price and the last Tax tick.
public class RememberedItems568Tests
{
    private static readonly DateTime Day = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static RememberedInvoiceItems.Used Use(string what, decimal price, bool taxed, int day) => new(what, price, taxed, Day.AddDays(day));

    [Fact]
    public void An_item_is_a_description_with_the_price_and_tax_tick_it_had_last_and_the_most_used_come_first()
    {
        var items = RememberedInvoiceItems.From(new[]
        {
            Use("Sourdough loaf", 6.00m, false, 1), Use("sourdough  LOAF ", 6.25m, false, 5), Use("Sourdough loaf", 6.50m, false, 9),   // one item, three spellings
            Use("Birthday cake", 75m, true, 2), Use("Birthday cake", 80m, true, 8),
            Use("Delivery", 12m, true, 9), Use("Baguette", 3m, false, 3), Use("   ", 99m, true, 9), Use("", 99m, true, 9),
        });

        Assert.Equal(new[] { "Sourdough loaf", "Birthday cake", "Delivery", "Baguette" }, items.Select(i => i.Description).ToArray());
        Assert.Equal((6.50m, false, 3, "$6.50"), (items[0].UnitPrice, items[0].IsTaxable, items[0].Uses, items[0].PriceText));   // the last price charged
        Assert.Equal((80m, true, 2), (items[1].UnitPrice, items[1].IsTaxable, items[1].Uses));
        // Used once each: the more recent first.
        Assert.Equal(new[] { "Delivery", "Baguette" }, items.Skip(2).Select(i => i.Description).ToArray());
    }

    [Fact]
    public void The_tax_tick_follows_the_last_use_and_a_credit_keeps_its_sign()
    {
        var items = RememberedInvoiceItems.From(new[] { Use("Cookies, half dozen", 9m, true, 1), Use("Cookies, half dozen", 9m, false, 4), Use("Returned tray", -15m, false, 2) });
        Assert.False(items.Single(i => i.Description == "Cookies, half dozen").IsTaxable);
        Assert.Equal("-$15.00", items.Single(i => i.Description == "Returned tray").PriceText);

        Assert.Equal("Sourdough loaf", RememberedInvoiceItems.Clean("  Sourdough \t loaf  "));
        Assert.Empty(RememberedInvoiceItems.From(Array.Empty<RememberedInvoiceItems.Used>()));
        Assert.Equal(RememberedInvoiceItems.Most, RememberedInvoiceItems.From(Enumerable.Range(1, RememberedInvoiceItems.Most + 50).Select(n => Use($"Item {n}", n, true, 0))).Count);
    }

    [Fact]
    public async Task The_editor_is_handed_the_businesss_own_items_from_its_invoices_and_schedules()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (bakeryId, bakeryClient) = await SeedAsync(db);
        var (otherId, otherClient) = await SeedAsync(db);
        var bakery = NewInvoicesController(db, bakeryId);

        // Nothing invoiced yet: nothing to offer, and the editor still opens.
        Assert.IsType<ViewResult>(await bakery.Create());
        Assert.Empty(Remembered(bakery));

        await bakery.Create(Order(bakeryClient, ("Sourdough loaf", 10, 6.00m, false), ("Birthday cake", 1, 80m, true)));
        await bakery.Create(Order(bakeryClient, ("sourdough loaf", 12, 6.50m, false)));                      // the price went up
        await bakery.Create(Order(bakeryClient, ("Wedding cake by mistake", 1, 900m, true)));
        var mistake = await db.ClientInvoices.SingleAsync(i => i.AgentUserId == bakeryId && i.LineItems.Any(l => l.Description.StartsWith("Wedding")));
        Assert.IsType<RedirectToActionResult>(await bakery.Void(mistake.Id));
        db.RecurringInvoiceSchedules.Add(new RecurringInvoiceSchedule
        {
            AgentUserId = bakeryId, ClientId = bakeryClient, NextRunDate = DateTime.UtcNow.AddDays(20),
            LineItems = { new RecurringInvoiceLineItem { Description = "Standing order, baguettes", Quantity = 20, UnitPrice = 3m, IsTaxable = false } }
        });
        await db.SaveChangesAsync();
        await NewInvoicesController(db, otherId).Create(Order(otherClient, ("Tax return", 1, 450m, true)));

        var fresh = NewInvoicesController(db, bakeryId);
        Assert.IsType<ViewResult>(await fresh.Create());
        var items = Remembered(fresh);
        Assert.Equal("Sourdough loaf", items[0].Description, ignoreCase: true);                             // used twice
        Assert.Equal((6.50m, false, 2), (items[0].UnitPrice, items[0].IsTaxable, items[0].Uses));
        Assert.Equal(new[] { "Birthday cake", "Standing order, baguettes" }, items.Skip(1).Select(i => i.Description).OrderBy(d => d).ToArray());
        Assert.DoesNotContain(items, i => i.Description.StartsWith("Wedding"));                               // a voided document is a mistake taken back
        Assert.DoesNotContain(items, i => i.Description == "Tax return");                                     // another business's item

        // The same list in the edit screen and in the recurring-schedule screen.
        var draft = await db.ClientInvoices.Where(i => i.AgentUserId == bakeryId && i.Status == ClientInvoiceStatus.Draft).OrderBy(i => i.Id).FirstAsync();
        var editing = NewInvoicesController(db, bakeryId);
        Assert.IsType<ViewResult>(await editing.Edit(draft.Id));
        Assert.Equal(3, Remembered(editing).Count);
        var recurring = new IPRO.Web.Controllers.RecurringInvoicesController(db, new GrantAll());
        recurring.ControllerContext = new ControllerContext { HttpContext = Context(bakeryId) };
        Assert.IsType<ViewResult>(await recurring.Create());
        Assert.Equal(3, Assert.IsAssignableFrom<IEnumerable<RememberedInvoiceItem>>((object)recurring.ViewBag.RememberedItems).Count());
    }

    [Fact]
    public void The_editor_offers_them_under_the_description_and_one_pick_fills_the_line()
    {
        var editor = Read(@"src\IPRO.Web\Views\Shared\_InvoiceLinesEditor.cshtml");
        Assert.Contains("<div id=\"rememberedItems\" hidden data-items=\"@rememberedJson\"></div>", editor);
        Assert.Contains("<div id=\"itemSuggestions\" class=\"list-group shadow item-suggestions\" role=\"listbox\"", editor);
        Assert.Contains("placeholder=\"Service or product description\" autocomplete=\"off\" />", editor);    // the browser's own list would sit on ours
        // One pick: the description, the last price, the last Tax tick; the cursor goes to the quantity.
        Assert.Contains("row.querySelector('input[name$=\".UnitPrice\"]').value = item.p;", editor);
        Assert.Contains("row.querySelector('.line-tax').checked = !!item.t;", editor);
        Assert.Contains("quantity.focus();", editor);
        // Enter on a lit offer picks it; it must not send the form.
        Assert.Contains("else if (e.key === 'Enter' && lit >= 0) { e.preventDefault(); pick(e.target, offered[lit]); }", editor);
        // The list hangs from the page so the table's sideways scroll cannot cut it off.
        Assert.Contains("document.body.appendChild(offers);", editor);
        Assert.Contains(".item-suggestions { position: absolute;", Read(@"src\IPRO.Web\Views\Shared\_InvoiceLineStyles.cshtml"));

        Assert.Contains("ViewBag.RememberedItems = await RememberedInvoiceItems.ForAgentAsync(_db, AgentId);", Read(@"src\IPRO.Web\Controllers\ClientInvoicesController.cs"));
        Assert.Contains("ViewBag.RememberedItems = await IPRO.Web.Infrastructure.RememberedInvoiceItems.ForAgentAsync(_db, AgentId);", Read(@"src\IPRO.Web\Controllers\RecurringInvoicesController.cs"));
        Assert.Contains("### Items You Have Used Before", Read(@"DOCS\10_CLIENT_INVOICING.md"));
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static List<RememberedInvoiceItem> Remembered(Controller controller) =>
        Assert.IsAssignableFrom<IEnumerable<RememberedInvoiceItem>>((object)controller.ViewBag.RememberedItems).ToList();

    private static ClientInvoiceEditViewModel Order(int clientId, params (string What, decimal Quantity, decimal Price, bool Taxed)[] lines) => new()
    {
        ClientId = clientId, IssueDate = new DateTime(2026, 10, 11), DueDate = new DateTime(2026, 10, 26),
        LineItems = lines.Select(l => new ClientInvoiceLineItemInputModel { Description = l.What, Quantity = l.Quantity, UnitPrice = l.Price, IsTaxable = l.Taxed }).ToList()
    };

    private static async Task<(int AgentId, int ClientId)> SeedAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T568-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t568-{Guid.NewGuid():N}")[..20], Email = $"{Guid.NewGuid():N}@example.test", FirstName = "Corner", LastName = "Baker",
            CompanyName = "Corner Bakery", Province = "ON", DomainName = ($"t568-{Guid.NewGuid():N}")[..24], PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var client = new Client { AgentUserId = agent.Id, FirstName = "Ava", LastName = "Chen", Email = $"{Guid.NewGuid():N}@example.test", Province = "ON" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return (agent.Id, client.Id);
    }

    private static DefaultHttpContext Context(int agentId) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
    };

    private static IPRO.Web.Controllers.ClientInvoicesController NewInvoicesController(IPRODbContext db, int agentId)
    {
        var controller = new IPRO.Web.Controllers.ClientInvoicesController(
            db, new GrantAll(), new ClientInvoiceService(new IPRO.DataAccess.Repositories.UnitOfWork(db)), new NoEmail(),
            new ConfigurationBuilder().Build());
        var ctx = Context(agentId);
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
