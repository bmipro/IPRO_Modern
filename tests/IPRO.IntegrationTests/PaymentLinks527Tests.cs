using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 527 (2026-09-28), rewritten around links on the owner's word ("what I wanted was to show/allow
// agents to use one of those services and enter their code or link to allow their clients to pay
// directly from invoices sent to them"): the Payments page is where the adviser enters what each
// service gives them -- a PayPal.me name, a Stripe Payment Link, a Square link, an Interac
// e-Transfer email, any other link -- and the invoice shows a Pay button per service. No account
// on iPro's side; the Stripe Connect machinery of slice 1 stays dormant. The rules are pinned
// without a database; the page and the client's document run against MySQL.
public class PaymentLinks527Tests
{
    [Fact]
    public void The_payment_methods_are_wired()
    {
        Assert.Contains("class AgentPaymentMethod", Read(@"src\IPRO.Entities\AgentPaymentMethod.cs"));
        Assert.Contains("class PaymentMethodLinks", Read(@"src\IPRO.Web\Infrastructure\PaymentMethodLinks.cs"));
        Assert.Contains("CREATE TABLE IF NOT EXISTS `AgentPaymentMethods`", Read(@"src\IPRO.DataAccess\StartupSchemaRepair.cs"));
        Assert.Contains("(\"AgentPaymentMethods\"", Read(@"src\IPRO.DataAccess\AgentDataEraser.cs"));
        Assert.Contains("DbSet<AgentPaymentMethod> AgentPaymentMethods", Read(@"src\IPRO.DataAccess\IPRODbContext.cs"));

        var controller = Read(@"src\IPRO.Web\Controllers\PaymentsController.cs");
        Assert.Contains("public async Task<IActionResult> Save(", controller);
        Assert.Contains("ViewBag.PayOptions", Read(@"src\IPRO.Web\Controllers\ClientDocumentController.cs"));

        var view = Read(@"src\IPRO.Web\Views\Payments\Index.cshtml");
        foreach (var field in new[] { "PayPal", "StripeLink", "SquareLink", "ETransferEmail", "ETransferNote", "OtherLink" })
            Assert.Contains($"asp-for=\"Form.{field}\"", view);
        Assert.Contains("/portal/Payments/Save", view);

        var document = Read(@"src\IPRO.Web\Views\ClientInvoices\_ClientInvoiceDocument.cshtml");
        Assert.Contains("ViewBag.PayOptions", document);
        Assert.DoesNotContain("ViewBag.PaymentLink", document);
        // The line above the buttons, in the owner's words (2026-09-28): the adviser's own business, never iPro.
        Assert.Contains("Pay online now, or contact @companyName to pay another way.", document);
        Assert.Contains(".pay-instruction", Read(@"src\IPRO.Web\wwwroot\css\invoice.css"));

        // The Profile's old single link points at the Payments page now; the profile form no longer writes it.
        Assert.Contains("/portal/Payments", Read(@"src\IPRO.Web\Views\Account\Profile.cshtml"));
        Assert.DoesNotContain("asp-for=\"DefaultPaymentLink\"", Read(@"src\IPRO.Web\Views\Account\Profile.cshtml"));
        Assert.DoesNotContain("agent.DefaultPaymentLink = model.DefaultPaymentLink", Read(@"src\IPRO.Web\Controllers\AccountController.cs"));

        // The help icon on the page opens the guide at the rewritten section.
        Assert.Contains("\"getting-paid-by-your-clients\"", Read(@"src\IPRO.Web\Infrastructure\HelpLinks.cs"));
        Assert.Contains("## Getting Paid by Your Clients", Read(@"DOCS\10_CLIENT_INVOICING.md"));
    }

    // -- the rules, without a database -------------------------------------------------------------

    [Fact]
    public void What_the_adviser_types_is_normalized_and_what_cannot_be_used_is_refused()
    {
        Assert.Equal("bob", Ok(PaymentMethodKinds.PayPal, "bob"));
        Assert.Equal("Bob1", Ok(PaymentMethodKinds.PayPal, " @Bob1 "));
        Assert.Equal("bob", Ok(PaymentMethodKinds.PayPal, "paypal.me/bob"));
        Assert.Equal("bob", Ok(PaymentMethodKinds.PayPal, "https://www.paypal.me/bob/"));
        Assert.Equal("bob", Ok(PaymentMethodKinds.PayPal, "https://paypal.com/paypalme/bob?x=1"));
        Assert.Contains("PayPal.me name", Refused(PaymentMethodKinds.PayPal, "bob smith"));
        Assert.Contains("PayPal.me name", Refused(PaymentMethodKinds.PayPal, "paypal.me/"));

        Assert.Equal("https://buy.stripe.com/abc", Ok(PaymentMethodKinds.Stripe, "buy.stripe.com/abc"));
        Assert.Equal("https://buy.stripe.com/abc", Ok(PaymentMethodKinds.Stripe, " https://buy.stripe.com/abc "));
        Assert.Contains("https://", Refused(PaymentMethodKinds.Stripe, "http://buy.stripe.com/abc"));
        Assert.Contains("https://", Refused(PaymentMethodKinds.Stripe, "not a link"));
        Assert.Contains("https://", Refused(PaymentMethodKinds.Stripe, "example"));
        Assert.Equal("https://square.link/u/xyz", Ok(PaymentMethodKinds.Square, "https://square.link/u/xyz"));
        Assert.Equal("https://pay.example.com/x", Ok(PaymentMethodKinds.Other, "pay.example.com/x"));
        Assert.Contains("too long", Refused(PaymentMethodKinds.Other, "https://pay.example.com/" + new string('a', 500)));

        Assert.Equal("pay@adviser.ca", Ok(PaymentMethodKinds.ETransfer, "Pay@Adviser.CA"));
        Assert.Contains("email", Refused(PaymentMethodKinds.ETransfer, "Bob <b@x.ca>"));
        Assert.Contains("email", Refused(PaymentMethodKinds.ETransfer, "nope"));
        Assert.Contains("email", Refused(PaymentMethodKinds.ETransfer, "a@b"));

        // Empty means "not offered", for every method.
        foreach (var method in PaymentMethodKinds.All)
        {
            Assert.True(PaymentMethodLinks.TryNormalize(method, "  ", out var value, out _));
            Assert.Equal(string.Empty, value);
        }
    }

    [Fact]
    public void Each_method_turns_an_invoice_into_the_right_button_or_line()
    {
        var invoice = new ClientInvoice { Total = 113m, Currency = "CAD", DocumentNumber = "IPRO-2026-000029", Client = new Client { Email = "ann@example.test" } };

        Assert.Equal("https://www.paypal.me/bob/113.00CAD", PaymentMethodLinks.PayUrl(PaymentMethodKinds.PayPal, "bob", 113m, "CAD", "IPRO-2026-000029", "ann@example.test"));
        Assert.Equal("https://buy.stripe.com/abc?client_reference_id=IPRO-2026-000029&prefilled_email=ann%40example.test",
            PaymentMethodLinks.PayUrl(PaymentMethodKinds.Stripe, "https://buy.stripe.com/abc", 113m, "CAD", "IPRO-2026-000029", "ann@example.test"));
        Assert.StartsWith("https://buy.stripe.com/abc?locale=fr&client_reference_id=IPRO-2026-000029",
            PaymentMethodLinks.PayUrl(PaymentMethodKinds.Stripe, "https://buy.stripe.com/abc?locale=fr", 113m, "CAD", "IPRO-2026-000029", null)!);
        Assert.Equal("https://buy.stripe.com/abc?client_reference_id=INV-12-3",
            PaymentMethodLinks.PayUrl(PaymentMethodKinds.Stripe, "https://buy.stripe.com/abc", 113m, "CAD", "INV 12/3", null));
        Assert.Equal("https://buy.stripe.com/abc", PaymentMethodLinks.PayUrl(PaymentMethodKinds.Stripe, "https://buy.stripe.com/abc", 113m, "CAD", "", null));
        Assert.Equal("https://square.link/u/xyz", PaymentMethodLinks.PayUrl(PaymentMethodKinds.Square, "https://square.link/u/xyz", 113m, "CAD", "X", "a@b.ca"));
        Assert.Equal("https://pay.example.com/x", PaymentMethodLinks.PayUrl(PaymentMethodKinds.Other, "https://pay.example.com/x", 113m, "CAD", "X", "a@b.ca"));
        Assert.Null(PaymentMethodLinks.PayUrl(PaymentMethodKinds.ETransfer, "pay@adviser.ca", 113m, "CAD", "X", null));
        Assert.Equal("Send an Interac e-Transfer of $113.00 CAD to pay@adviser.ca.", PaymentMethodLinks.Instruction(PaymentMethodKinds.ETransfer, "pay@adviser.ca", 113m, "CAD"));
        Assert.Equal("Send an Interac e-Transfer of $1,234.50 USD to pay@adviser.ca.", PaymentMethodLinks.Instruction(PaymentMethodKinds.ETransfer, "pay@adviser.ca", 1234.5m, "usd"));
        Assert.Null(PaymentMethodLinks.Instruction(PaymentMethodKinds.PayPal, "bob", 113m, "CAD"));

        Assert.Equal("Pay with PayPal", PaymentMethodLinks.Label(PaymentMethodKinds.PayPal));
        Assert.Equal("Pay by card", PaymentMethodLinks.Label(PaymentMethodKinds.Stripe));
        Assert.Equal("Pay with Square", PaymentMethodLinks.Label(PaymentMethodKinds.Square));
        Assert.Equal("Interac e-Transfer", PaymentMethodLinks.Label(PaymentMethodKinds.ETransfer));
        Assert.Equal("Pay Now", PaymentMethodLinks.Label(PaymentMethodKinds.Other));

        // The document's options: the fixed order, whatever order the rows come in; blanks skipped.
        var methods = new[]
        {
            new AgentPaymentMethod { Method = PaymentMethodKinds.Other, Value = "https://pay.example.com/x" },
            new AgentPaymentMethod { Method = PaymentMethodKinds.ETransfer, Value = "pay@adviser.ca", Note = " Auto-deposit is on. " },
            new AgentPaymentMethod { Method = PaymentMethodKinds.Stripe, Value = "https://buy.stripe.com/abc" },
            new AgentPaymentMethod { Method = PaymentMethodKinds.Square, Value = "  " },
            new AgentPaymentMethod { Method = PaymentMethodKinds.PayPal, Value = "bob" },
        };
        var options = PaymentMethodLinks.OptionsFor(methods, "https://paypal.me/ignored", invoice);
        Assert.Equal(new[] { "paypal", "stripe", "etransfer", "other" }, options.Select(o => o.Method).ToArray());
        Assert.Equal("https://www.paypal.me/bob/113.00CAD", options[0].Url);
        Assert.Equal("https://buy.stripe.com/abc?client_reference_id=IPRO-2026-000029&prefilled_email=ann%40example.test", options[1].Url);
        Assert.Null(options[2].Url);
        Assert.Equal("Send an Interac e-Transfer of $113.00 CAD to pay@adviser.ca.", options[2].Instruction);
        Assert.Equal("Auto-deposit is on.", options[2].Note);
        Assert.Equal("Pay Now", options[3].Label);
        Assert.Equal("https://pay.example.com/x", options[3].Url);

        // No methods: the Profile's link from before 527, exactly as it always showed; nothing at all: nothing.
        var legacy = PaymentMethodLinks.OptionsFor(Array.Empty<AgentPaymentMethod>(), "https://paypal.me/legacy", invoice);
        Assert.Single(legacy);
        Assert.Equal("Pay Now", legacy[0].Label);
        Assert.Equal("https://paypal.me/legacy/113.00CAD", legacy[0].Url);
        Assert.Empty(PaymentMethodLinks.OptionsFor(Array.Empty<AgentPaymentMethod>(), "  ", invoice));

        // What the old link becomes on the page the first time it opens.
        Assert.Equal((PaymentMethodKinds.PayPal, "bob"), PaymentMethodLinks.FromLegacyLink("https://www.paypal.me/bob"));
        Assert.Equal((PaymentMethodKinds.PayPal, "bob"), PaymentMethodLinks.FromLegacyLink("paypal.me/bob"));
        Assert.Equal((PaymentMethodKinds.Stripe, "https://buy.stripe.com/x"), PaymentMethodLinks.FromLegacyLink("https://buy.stripe.com/x"));
        Assert.Equal((PaymentMethodKinds.Square, "https://square.link/u/x"), PaymentMethodLinks.FromLegacyLink("https://square.link/u/x"));
        Assert.Equal((PaymentMethodKinds.Other, "https://pay.example.com"), PaymentMethodLinks.FromLegacyLink("https://pay.example.com"));
        Assert.Null(PaymentMethodLinks.FromLegacyLink(null));
    }

    // -- the page, against MySQL ---------------------------------------------------------------------

    [Fact]
    public async Task The_page_offers_the_old_profile_link_saves_the_methods_and_retires_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, "https://paypal.me/bob");

        var first = Assert.IsType<PaymentsViewModel>(Assert.IsType<ViewResult>(await NewController(db, agentId).Index()).Model);
        Assert.Equal("bob", first.Form.PayPal);
        Assert.Equal("https://paypal.me/bob", first.LegacyLink);
        Assert.False(first.HasSavedMethods);
        Assert.False(first.StripeConfigured);
        Assert.Null(first.Stripe);

        var saved = Assert.IsType<RedirectToActionResult>(await NewController(db, agentId).Save(new PaymentsForm
        {
            PayPal = "paypal.me/bob", StripeLink = "buy.stripe.com/abc", ETransferEmail = "Pay@Adviser.ca", ETransferNote = " Auto-deposit is on. "
        }));
        Assert.Equal("Index", saved.ActionName);
        var rows = await db.AgentPaymentMethods.AsNoTracking().Where(m => m.AgentUserId == agentId).OrderBy(m => m.Method).ToListAsync();
        Assert.Equal(new[] { "etransfer", "paypal", "stripe" }, rows.Select(r => r.Method).ToArray());
        Assert.Equal("pay@adviser.ca", rows[0].Value);
        Assert.Equal("Auto-deposit is on.", rows[0].Note);
        Assert.Equal("bob", rows[1].Value);
        Assert.Equal("https://buy.stripe.com/abc", rows[2].Value);
        Assert.Equal(string.Empty, rows[2].Note);
        Assert.Equal(string.Empty, await db.AgentUsers.AsNoTracking().Where(a => a.Id == agentId).Select(a => a.DefaultPaymentLink).SingleAsync());

        var second = Assert.IsType<PaymentsViewModel>(Assert.IsType<ViewResult>(await NewController(db, agentId).Index()).Model);
        Assert.Null(second.LegacyLink);
        Assert.True(second.HasSavedMethods);
        Assert.Equal("bob", second.Form.PayPal);
        Assert.Equal("https://buy.stripe.com/abc", second.Form.StripeLink);
        Assert.Equal("pay@adviser.ca", second.Form.ETransferEmail);
        Assert.Equal("Auto-deposit is on.", second.Form.ETransferNote);
        Assert.Null(second.Form.SquareLink);
        Assert.Null(second.Form.OtherLink);

        // One bad entry sends the whole form back, with the message on that field, and changes nothing.
        var bad = NewController(db, agentId);
        var back = Assert.IsType<ViewResult>(await bad.Save(new PaymentsForm { PayPal = "bob smith", StripeLink = "http://buy.stripe.com/abc", OtherLink = "https://pay.example.com/x" }));
        Assert.Equal("Index", back.ViewName);
        Assert.False(bad.ModelState.IsValid);
        Assert.True(bad.ModelState.ContainsKey("Form.PayPal"));
        Assert.True(bad.ModelState.ContainsKey("Form.StripeLink"));
        Assert.False(bad.ModelState.ContainsKey("Form.OtherLink"));
        Assert.Equal("bob smith", Assert.IsType<PaymentsViewModel>(back.Model).Form.PayPal);
        Assert.Equal(3, await db.AgentPaymentMethods.CountAsync(m => m.AgentUserId == agentId));
        Assert.False(await db.AgentPaymentMethods.AnyAsync(m => m.AgentUserId == agentId && m.Method == PaymentMethodKinds.Other));

        // Emptied means gone; added means there; saving the same thing twice changes nothing.
        Assert.IsType<RedirectToActionResult>(await NewController(db, agentId).Save(new PaymentsForm
        {
            PayPal = "", StripeLink = "https://buy.stripe.com/abc", ETransferEmail = "pay@adviser.ca", ETransferNote = "Auto-deposit is on.", OtherLink = "https://pay.example.com/x"
        }));
        Assert.IsType<RedirectToActionResult>(await NewController(db, agentId).Save(new PaymentsForm
        {
            PayPal = "", StripeLink = "https://buy.stripe.com/abc", ETransferEmail = "pay@adviser.ca", ETransferNote = "Auto-deposit is on.", OtherLink = "https://pay.example.com/x"
        }));
        var after = await db.AgentPaymentMethods.AsNoTracking().Where(m => m.AgentUserId == agentId).OrderBy(m => m.Method).Select(m => m.Method).ToListAsync();
        Assert.Equal(new[] { "etransfer", "other", "stripe" }, after.ToArray());

        // Everything emptied: no rows, and the page says so without an error.
        var cleared = NewController(db, agentId);
        Assert.IsType<RedirectToActionResult>(await cleared.Save(new PaymentsForm()));
        Assert.Equal(0, await db.AgentPaymentMethods.CountAsync(m => m.AgentUserId == agentId));
        Assert.Contains("no payment method", (string)cleared.TempData["Success"]!);
    }

    [Fact]
    public async Task The_clients_document_shows_a_button_per_method_and_the_old_link_when_there_is_none()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var withMethods = await SeedAgentAsync(db, "https://paypal.me/ignored");
        db.AgentPaymentMethods.AddRange(
            new AgentPaymentMethod { AgentUserId = withMethods, Method = PaymentMethodKinds.PayPal, Value = "bob" },
            new AgentPaymentMethod { AgentUserId = withMethods, Method = PaymentMethodKinds.Stripe, Value = "https://buy.stripe.com/abc" },
            new AgentPaymentMethod { AgentUserId = withMethods, Method = PaymentMethodKinds.ETransfer, Value = "pay@adviser.ca", Note = "Auto-deposit is on." });
        await db.SaveChangesAsync();
        var (tokenA, numberA) = await SeedInvoiceAsync(db, withMethods, 250m, "ann@example.test");

        var optionsA = await PayOptionsAsync(db, tokenA);
        Assert.Equal(new[] { "paypal", "stripe", "etransfer" }, optionsA.Select(o => o.Method).ToArray());
        Assert.Equal("https://www.paypal.me/bob/250.00CAD", optionsA[0].Url);
        Assert.Equal($"https://buy.stripe.com/abc?client_reference_id={numberA}&prefilled_email=ann%40example.test", optionsA[1].Url);
        Assert.Equal("Send an Interac e-Transfer of $250.00 CAD to pay@adviser.ca.", optionsA[2].Instruction);
        Assert.Equal("Auto-deposit is on.", optionsA[2].Note);

        var legacyOnly = await SeedAgentAsync(db, "https://paypal.me/legacy");
        var (tokenB, _) = await SeedInvoiceAsync(db, legacyOnly, 99.5m, "bob@example.test");
        var optionsB = await PayOptionsAsync(db, tokenB);
        Assert.Single(optionsB);
        Assert.Equal("Pay Now", optionsB[0].Label);
        Assert.Equal("https://paypal.me/legacy/99.50CAD", optionsB[0].Url);

        var nothing = await SeedAgentAsync(db, "");
        var (tokenC, _) = await SeedInvoiceAsync(db, nothing, 10m, null);
        Assert.Empty(await PayOptionsAsync(db, tokenC));
    }

    // -- helpers -------------------------------------------------------------------------------------

    private static string Ok(string method, string raw)
    {
        Assert.True(PaymentMethodLinks.TryNormalize(method, raw, out var value, out var error), $"{method} '{raw}' was refused: {error}");
        return value;
    }

    private static string Refused(string method, string raw)
    {
        Assert.False(PaymentMethodLinks.TryNormalize(method, raw, out _, out var error), $"{method} '{raw}' was accepted");
        return error;
    }

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.example.test" })
        .Build();

    private static async Task<IReadOnlyList<PayOption>> PayOptionsAsync(IPRODbContext db, string token)
    {
        var controller = new IPRO.Web.Controllers.ClientDocumentController(db);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) } };
        Assert.IsType<ViewResult>(await controller.Show(token));
        return Assert.IsAssignableFrom<IReadOnlyList<PayOption>>(controller.ViewData["PayOptions"]);
    }

    private static IPRO.Web.Controllers.PaymentsController NewController(IPRODbContext db, int agentId)
    {
        var controller = new IPRO.Web.Controllers.PaymentsController(db, new NoStripe(), new GrantAll(), Config(), new EphemeralDataProtectionProvider());
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("app.example.test");
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string legacyLink)
    {
        var agent = new AgentUser
        {
            UserName = $"t527b-{Guid.NewGuid():N}"[..20],
            Email = $"t527b-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Links", LastName = "Adviser",
            CompanyName = "Links Co",
            DomainName = $"t527b-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario",
            DefaultPaymentLink = legacyLink
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<(string Token, string Number)> SeedInvoiceAsync(IPRODbContext db, int agentId, decimal total, string? clientEmail)
    {
        var client = new Client { AgentUserId = agentId, FirstName = "Cli", LastName = "Ent", Email = clientEmail ?? $"{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var invoice = new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id,
            DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent,
            DocumentNumber = $"L-{Guid.NewGuid():N}"[..20], Total = total, Currency = "CAD",
            ViewToken = Guid.NewGuid().ToString("N"),
            IssueDate = DateTime.UtcNow.Date.AddDays(-7),
            DueDate = DateTime.UtcNow.Date.AddDays(23),
            SentAt = DateTime.UtcNow.AddDays(-7)
        };
        db.ClientInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return (invoice.ViewToken, invoice.DocumentNumber);
    }

    private sealed class NoStripe : IStripeConnectService
    {
        public bool IsConfigured => false;
        public string BuildAuthorizationUrl(string redirectUri, string state) => throw new NotSupportedException();
        public Task<StripeConnectedAccount> ExchangeCodeAsync(string code) => throw new NotSupportedException();
        public Task DeauthorizeAsync(string accountId) => throw new NotSupportedException();
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

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, relative);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }
}
