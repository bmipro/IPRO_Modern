using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 525 (2026-09-27): three lines the owner found missing. "SSL included" in the package catalogue;
// "Powered by iPro" under every adviser site and at the foot of every client document; "Sent with
// iPro" on the emails that carry a document. The link goes to the brand for the adviser's business.
public class PoweredBy525Tests
{
    [Fact]
    public void The_three_lines_are_wired()
    {
        Assert.Contains("SslCertificate = \"ssl_certificate\"", Read(@"src\IPRO.Entities\PackageFeatureCodes.cs"));
        Assert.Contains("PackageFeatureCodes.SslCertificate,", Read(@"src\IPRO.DataAccess\PackageEntitlementSeeder.cs"));

        var footer = Read(@"src\IPRO.Web\Views\PublicWebsite\_PublicFooterContent.cshtml");
        Assert.Contains("public-footer__powered", footer);
        Assert.Contains("PoweredBy.BrandUrl(", footer);

        var document = Read(@"src\IPRO.Web\Views\ClientInvoices\_ClientInvoiceDocument.cshtml");
        Assert.Contains("class=\"powered-by\"", document);
        Assert.Contains("PoweredBy.BrandUrl(", document);
        Assert.Contains(".powered-by", Read(@"src\IPRO.Web\wwwroot\css\invoice.css"));

        // 542: the invoice and reminder emails are letters now, and the letter carries the line.
        var letter = Read(@"src\IPRO.Entities\ClientLetter.cs");
        Assert.Contains("Sent with", letter);
        Assert.Contains("PoweredBy.BrandUrl(", letter);
        Assert.Contains("ClientInvoiceEmail.Html(", Read(@"src\IPRO.Web\Controllers\ClientInvoicesController.cs"));
        Assert.Contains("ClientLetter.Html(", Read(@"src\IPRO.Scheduler\ClientInvoiceEmail.cs"));
        Assert.Contains("ClientLetter.Html(", Read(@"src\IPRO.Scheduler\ClientInvoiceReminderEmail.cs"));
    }

    [Fact]
    public void The_link_follows_the_advisers_business()
    {
        Assert.Equal("https://www.iproaccountants.com/", PoweredBy.BrandUrl("Accountants"));
        Assert.Equal("https://www.iproaccountants.com/", PoweredBy.BrandUrl(" accountant "));
        Assert.Equal("https://www.ipromortgages.com/", PoweredBy.BrandUrl("Mortgage"));
        Assert.Equal("https://www.ipromortgages.com/", PoweredBy.BrandUrl("Mortgage Broker"));
        Assert.Equal("https://www.iproadvisers.com/", PoweredBy.BrandUrl("Insurance / Financial"));
        Assert.Equal("https://www.iproadvisers.com/", PoweredBy.BrandUrl("Generic"));
        Assert.Equal("https://www.iproadvisers.com/", PoweredBy.BrandUrl(null));
        Assert.Equal("https://www.iproadvisers.com/", PoweredBy.BrandUrl(""));

        Assert.Equal("www.iProAccountants.com", PoweredBy.BrandName("Accountants"));     // the owner's capitals, for people
        Assert.Equal("www.iProMortgages.com", PoweredBy.BrandName("Mortgage"));
        Assert.Equal("www.iProAdvisers.com", PoweredBy.BrandName("Generic"));
        Assert.Equal("iPro", PoweredBy.Label);
    }

    [Fact]
    public void The_reminder_email_says_sent_with_iPro_and_links_the_right_brand()
    {
        var invoice = new ClientInvoice
        {
            DocumentNumber = "INV-9", Total = 100m, Currency = "CAD", DueDate = new DateTime(2026, 9, 1),
            Client = new Client { FirstName = "Ann", LastName = "X" },
            AgentUser = new AgentUser { CompanyName = "Mortgage Co", BusinessType = "Mortgage" }
        };
        var (_, html) = IPRO.Scheduler.ClientInvoiceReminderEmail.Build(invoice, "https://example.test/invoice/t", ClientInvoiceReminderSchedule.Defaults(1), new DateTime(2026, 9, 10));

        Assert.Contains("Sent with", html);
        Assert.Contains("https://www.ipromortgages.com/", html);
        Assert.Contains(">iPro</a>", html);
    }

    [Fact]
    public async Task The_catalogue_seeds_ssl_included_on_every_package_once()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        await PackageEntitlementSeeder.SeedAsync(db);
        var rows = await db.PackageFeatures.AsNoTracking().Where(f => f.FeatureCode == PackageFeatureCodes.SslCertificate).ToListAsync();
        Assert.Equal(4, rows.Count);                                   // Silver, Gold, Platinum, Broker
        Assert.All(rows, r => Assert.True(r.IsIncluded));
        Assert.All(rows, r => Assert.Equal(175, r.SortOrder));
        Assert.All(rows, r => Assert.Contains("SSL certificate", r.FeatureName));

        await PackageEntitlementSeeder.SeedAsync(db);                  // the next start-up adds nothing twice
        Assert.Equal(4, await db.PackageFeatures.CountAsync(f => f.FeatureCode == PackageFeatureCodes.SslCertificate));
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
