using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 510 (2026-09-21, launch day, the afternoon batch). Three things the morning's work surfaced:
//  - Search Console refused 509's sitemap ("URL not allowed"): one file listed all three brands'
//    front pages, and a sitemap may only name addresses of the site it is submitted for. Each site's
//    sitemap now lists its own pages only.
//  - The first live free-month sign-up produced an invoice whose only line read "IPro Gold
//    subscription adjustment - $0.00": true, and no way to greet every prospect who uses a code. An
//    invoice now says what the promotion did and names the code.
//  - The owner's rule for brand domains in anything a person reads (www.iProAdvisers.com: the capitals
//    separate the words) and the three places that did not follow it.
// Every defect test observed RED on the pre-fix code.
public class FrontDoorFollowUp510Tests
{
    private const string Live = "www.ipromortgages.com=/mortgage,ipromortgages.com=/mortgage,www.iproadvisers.com=/,iproadvisers.com=/,www.iproaccountants.com=/accountants,iproaccountants.com=/accountants";

    private static IConfiguration Config(string? aliasHosts = Live, string baseUrl = "https://app.iproadvisers.com")
    {
        var values = new Dictionary<string, string?> { ["App:BaseUrl"] = baseUrl };
        if (aliasHosts != null) values["App:AliasHosts"] = aliasHosts;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static List<string> Locations(string xml) =>
        xml.Split("<loc>").Skip(1).Select(part => part[..part.IndexOf("</loc>", StringComparison.Ordinal)]).ToList();

    // ---- a sitemap names its own site's pages only ----------------------------------------------

    [Theory]
    [InlineData("www.iproadvisers.com")]
    [InlineData("iproadvisers.com")]
    [InlineData("app.iproadvisers.com")]
    [InlineData("WWW.IPROADVISERS.COM")]
    public void The_advisers_sites_sitemap_lists_the_home_page_terms_and_privacy(string host)
    {
        var xml = PlatformSeoFiles.Sitemap(Config(), host);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml);
        Assert.Equal(new[] { "https://www.iproadvisers.com/", "https://app.iproadvisers.com/terms", "https://app.iproadvisers.com/privacy" }, Locations(xml));
    }

    [Theory]
    [InlineData("www.iproaccountants.com", "https://www.iproaccountants.com/")]
    [InlineData("iproaccountants.com", "https://www.iproaccountants.com/")]
    [InlineData("www.ipromortgages.com", "https://www.ipromortgages.com/")]
    [InlineData("ipromortgages.com", "https://www.ipromortgages.com/")]
    public void A_brand_names_sitemap_lists_its_own_front_page_and_nothing_else(string host, string expected)
    {
        Assert.Equal(new[] { expected }, Locations(PlatformSeoFiles.Sitemap(Config(), host)));
    }

    [Fact]
    public void Control_with_no_brand_names_the_platform_lists_every_public_page_itself()
    {
        Assert.Equal(new[]
        {
            "https://app.iproadvisers.com/", "https://app.iproadvisers.com/accountants", "https://app.iproadvisers.com/mortgage",
            "https://app.iproadvisers.com/terms", "https://app.iproadvisers.com/privacy"
        }, Locations(PlatformSeoFiles.Sitemap(Config(aliasHosts: null), "app.iproadvisers.com")));
    }

    [Fact]
    public void The_controller_asks_for_the_sitemap_of_the_name_the_visitor_used()
    {
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\PublicWebsiteController.cs"));
        Assert.Contains("PlatformSeoFiles.Sitemap(_configuration, publicHost)", controller);
    }

    // ---- an invoice says what the promotion did ---------------------------------------------------

    private static PromotionCode Code(PromoDiscountType type, decimal value, int? cycles, string code = "FREETEST") =>
        new() { Code = code, RecurringDiscountType = type, RecurringDiscountValue = value, RecurringDurationCycles = cycles };

    [Theory]
    [InlineData(1, "Monthly", "IPro Gold monthly subscription - first month free with promotion code FREETEST")]
    [InlineData(3, "Monthly", "IPro Gold monthly subscription - first 3 months free with promotion code FREETEST")]
    [InlineData(1, "Annually", "IPro Gold annual subscription - first year free with promotion code FREETEST")]
    [InlineData(null, "Monthly", "IPro Gold monthly subscription - free with promotion code FREETEST")]
    public void A_free_period_is_called_what_it_is(int? cycles, string period, string expected)
    {
        var text = PromotionInvoiceText.Recurring("IPro Gold", Enum.Parse<BillingPeriod>(period), Code(PromoDiscountType.PercentOff, 100m, cycles), 0m);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void A_discount_names_its_size_its_length_and_its_code()
    {
        Assert.Equal("IPro Gold monthly recurring subscription - 20% off for the first month with promotion code LAUNCH-G",
            PromotionInvoiceText.Recurring("IPro Gold", BillingPeriod.Monthly, Code(PromoDiscountType.PercentOff, 20m, 1, "LAUNCH-G"), 48m));
        Assert.Equal("IPro Gold monthly recurring subscription - 12.5% off for the first 6 months with promotion code LAUNCH-G",
            PromotionInvoiceText.Recurring("IPro Gold", BillingPeriod.Monthly, Code(PromoDiscountType.PercentOff, 12.50m, 6, "LAUNCH-G"), 52.50m));
        Assert.Equal("IPro Gold annual recurring subscription - $50 off for the life of the subscription with promotion code PARTNER",
            PromotionInvoiceText.Recurring("IPro Gold", BillingPeriod.Annually, Code(PromoDiscountType.FlatAmountOff, 50m, null, "PARTNER"), 550m));
    }

    [Fact]
    public void Control_a_code_that_leaves_the_recurring_price_alone_leaves_its_line_alone()
    {
        var setupOnly = new PromotionCode { Code = "NOSETUP", SetupFeeDiscountType = PromoDiscountType.PercentOff, SetupFeeDiscountValue = 100m };

        Assert.Equal("IPro Silver monthly recurring subscription", PromotionInvoiceText.Recurring("IPro Silver", BillingPeriod.Monthly, setupOnly, 40m));
    }

    [Fact]
    public void A_setup_fee_a_code_takes_off_is_shown_not_dropped()
    {
        var waived = new PromotionCode { Code = "NOSETUP", SetupFeeDiscountType = PromoDiscountType.PercentOff, SetupFeeDiscountValue = 100m };
        var halved = new PromotionCode { Code = "HALFSETUP", SetupFeeDiscountType = PromoDiscountType.PercentOff, SetupFeeDiscountValue = 50m };

        Assert.Equal("IPro Silver one-time setup fee - waived with promotion code NOSETUP", PromotionInvoiceText.Setup("IPro Silver", waived, 150m, 0m));
        Assert.Equal("IPro Silver one-time setup fee - 50% off with promotion code HALFSETUP", PromotionInvoiceText.Setup("IPro Silver", halved, 150m, 75m));
    }

    [Fact]
    public void Control_a_fee_the_package_already_waived_or_a_code_without_a_setup_discount_adds_no_line()
    {
        var waived = new PromotionCode { Code = "NOSETUP", SetupFeeDiscountType = PromoDiscountType.PercentOff, SetupFeeDiscountValue = 100m };

        Assert.Null(PromotionInvoiceText.Setup("IPro Gold", waived, 0m, 0m));   // the package's own waiver got there first
        Assert.Null(PromotionInvoiceText.Setup("IPro Gold", Code(PromoDiscountType.PercentOff, 100m, 1), 200m, 200m));
    }

    [Fact]
    public async Task The_first_invoice_of_a_comped_account_says_so_instead_of_subscription_adjustment()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var rule = new BillingRule
        {
            PackageName = ($"PI-{Guid.NewGuid():N}")[..20],
            MonthlyPrice = 90m, AnnualPrice = 900m, SetupFee = 400m, IsActive = true,
            PayPalMonthlyPlanId = "P-PI-M", PayPalAnnualPlanId = "P-PI-A"
        };
        db.Add(rule);
        await db.SaveChangesAsync();
        var promo = new PromotionCode
        {
            Code = ($"PI{Guid.NewGuid():N}")[..12].ToUpperInvariant(), IsActive = true, RestrictedBillingRuleId = rule.Id,
            RecurringDiscountType = PromoDiscountType.PercentOff, RecurringDiscountValue = 100m,
            SetupFeeDiscountType = PromoDiscountType.PercentOff, SetupFeeDiscountValue = 100m
        };
        db.Add(promo);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"pi-{Guid.NewGuid():N}")[..20], Email = $"pi-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Promo", LastName = "Invoice", DomainName = ($"pi-{Guid.NewGuid():N}")[..24],
            IsActive = true, PackageId = rule.Id, PromotionCode = promo.Code
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var result = await NewService(db).CreateSubscriptionAsync(agent.Id, rule.Id, BillingPeriod.Monthly, "https://x/r", "https://x/c");
        db.ChangeTracker.Clear();

        Assert.True(result.Success, result.Message);
        var invoiceId = await db.Invoices.AsNoTracking().Where(i => i.AgentUserId == agent.Id).Select(i => i.Id).SingleAsync();
        var lines = await db.InvoiceLineItems.AsNoTracking().Where(l => l.InvoiceId == invoiceId).OrderBy(l => l.SortOrder).Select(l => l.Description).ToListAsync();
        Assert.Equal(new[]
        {
            $"{rule.PackageName} monthly subscription - free with promotion code {promo.Code}",
            $"{rule.PackageName} one-time setup fee - waived with promotion code {promo.Code}"
        }, lines);
        Assert.DoesNotContain(lines, l => l.Contains("adjustment", StringComparison.OrdinalIgnoreCase));
    }

    // ---- brand domains in what a person reads ------------------------------------------------------

    [Fact]
    public void The_three_places_write_the_brand_domain_with_its_capitals()
    {
        var home = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));
        Assert.Contains("<span>app.iProAdvisers.com</span>", home);
        Assert.DoesNotContain("<span>app.iproadvisers.com</span>", home);

        var emailSetup = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\EmailSetup\Index.cshtml"));
        Assert.Contains("<strong>iProAdvisers.com</strong>", emailSetup);

        Assert.Equal("training@iProAdvisers.com", new IPRO.Web.Models.RegistrationWelcomeModel().TrainingEmail);
    }

    // ---- plumbing ---------------------------------------------------------------------------------

    private static PayPalBillingService NewService(IPRODbContext db) => new(
        new UnitOfWork(db), db, new OfflineHttpClientFactory(), new QuietEmail(),
        Options.Create(new PayPalSettings { ClientId = "test-client", ClientSecret = "test-secret" }),
        new ConfigurationBuilder().Build(),
        NullLogger<PayPalBillingService>.Instance);

    private sealed class OfflineHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RefuseHandler());
        private sealed class RefuseHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken ct)
                => throw new HttpRequestException("offline test handler");
        }
    }

    private sealed class QuietEmail : IPRO.Email.IEmailService
    {
        public Task<bool> SendAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(true);
        public Task<IPRO.Email.EmailSendResult> SendDetailedAsync(string a, string b, string c, string d, string? e = null, IDictionary<string, string>? f = null, string? g = null, string? h = null, string? i = null) => Task.FromResult(IPRO.Email.EmailSendResult.Sent());
        public Task<bool> SendBulkAsync(IEnumerable<IPRO.Email.EmailRecipient> r, string s, string h, string? t = null) => Task.FromResult(true);
        public Task<bool> SendTemplateAsync(string a, string b, string c, object d) => Task.FromResult(true);
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
