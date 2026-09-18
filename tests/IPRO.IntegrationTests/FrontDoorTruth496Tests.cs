using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 496 (2026-09-18), the truth sweep of the public pages three days before launch. The owner found the
// first gap himself that morning (a Generic edition nobody could choose, 494/495) and asked what else
// was left; two read-only reviewers then checked every concrete promise a prospect sees before paying
// -- the home page, both landing pages, the pricing table, the preview, sign-up and the welcome email
// -- against the code. Each test below is one of their findings, verified by hand before it was fixed.
public class FrontDoorTruth496Tests
{
    // ---- "Your site is live from day one" ---------------------------------------------------

    [Fact]
    public async Task A_new_account_is_provisioned_a_published_site_with_its_starter_pages()
    {
        // Nothing at sign-up or at payment created the website: only the Publish button did. Three
        // pages said "live from day one" and the welcome email's main button opened a 404. The site
        // is now written AND published when the account is created. It is safe before payment: an
        // unpaid account's public site answers 404 until billing is active (PublicWebsiteController).
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await SeedStarterContentAsync(db);
        var agent = await SeedAgentAsync(db, "Accountants");
        var websites = new WebsiteService(new UnitOfWork(db));

        var site = await WebsiteProvisioning.EnsurePublishedAsync(db, websites, agent);
        db.ChangeTracker.Clear();

        var stored = await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.AgentUserId == agent.Id);
        Assert.Equal(site.Id, stored.Id);
        Assert.True(stored.IsPublished);
        Assert.False(string.IsNullOrWhiteSpace(stored.SiteTitle));
        var slugs = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == stored.Id && p.ParentPageId == null).Select(p => p.Slug).ToListAsync();
        foreach (var slug in new[] { "home", "about", "contact", "request-meeting", "resources" }) Assert.Contains(slug, slugs);

        // Pressing Publish later (the same code) changes nothing and duplicates nothing.
        var pagesBefore = await db.WebsitePages.CountAsync(p => p.AgentWebsiteId == stored.Id);
        await WebsiteProvisioning.EnsurePublishedAsync(db, new WebsiteService(new UnitOfWork(db)), agent);
        Assert.Equal(1, await db.AgentWebsites.CountAsync(w => w.AgentUserId == agent.Id));
        Assert.Equal(pagesBefore, await db.WebsitePages.CountAsync(p => p.AgentWebsiteId == stored.Id));
    }

    [Fact]
    public void Sign_up_provisions_the_site_before_the_welcome_email_and_publish_runs_the_same_code()
    {
        var account = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\AccountController.cs"));
        var register = account[account.IndexOf("public async Task<IActionResult> Register(AgentRegistrationViewModel model", StringComparison.Ordinal)..];
        var provision = register.IndexOf("WebsiteProvisioning.EnsurePublishedAsync(", StringComparison.Ordinal);
        var welcome = register.IndexOf("RegistrationWelcomeTemplate.BuildHtml(welcome)", StringComparison.Ordinal);
        Assert.True(provision > 0 && welcome > provision, "the site must exist before the email that links to it is sent");
        // A provisioning failure must never cost a sign-up: it is caught and logged.
        var around = register[(provision - 400)..(provision + 600)];
        Assert.Contains("try", around);
        Assert.Contains("catch (Exception", around);

        Assert.Contains("WebsiteProvisioning.EnsurePublishedAsync(", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\WebsiteController.cs")));
    }

    // ---- the setup fee on the landing pages -------------------------------------------------

    [Fact]
    public void The_landing_price_cards_use_the_one_waiver_rule()
    {
        // The partial carried its own copy of the waiver test, inverted for a lapsed waiver: with
        // "waived until September 30" it would have shown NO fee from 1 October while the home page
        // showed it and PayPal charged it. BillingRule says never to re-implement that test.
        var partial = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\_LandingPricing.cshtml"));
        Assert.Contains("IsSetupFeeWaivedOn(now)", partial);
        Assert.DoesNotContain("SetupFeeWaivedUntil.HasValue", partial);

        // The rule itself, for the record: a dated waiver that has passed is not a waiver.
        var lapsed = new BillingRule { SetupFee = 200m, SetupFeeWaived = true, SetupFeeWaivedUntil = DateTime.UtcNow.AddDays(-1) };
        Assert.False(lapsed.IsSetupFeeWaivedOn(DateTime.UtcNow));
        Assert.Equal(200m, lapsed.EffectiveSetupFee(DateTime.UtcNow));
    }

    // ---- the plan table and the package descriptions ----------------------------------------

    [Fact]
    public async Task The_plan_table_stops_selling_what_is_not_there_and_names_two_rows_for_what_they_are()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await PackageEntitlementSeeder.SeedAsync(db);

        // Put the database in the state production is in: the withdrawn row present, the support row
        // carrying its undefined "Limited"/"Unlimited", Gold's description still naming features
        // withdrawn in August, and Platinum's description edited by the owner.
        var gold = await db.BillingRules.SingleAsync(p => p.PackageName == "IPro Gold");
        var platinum = await db.BillingRules.SingleAsync(p => p.PackageName == "IPro Platinum");
        gold.Description = "Expanded package with marketing, banners, coupons, and mail tools.";
        platinum.Description = "The owner's own words about Platinum.";
        // 498: the row exists again (the job behind it was built the same day); production still has it
        // under its old name.
        foreach (var row in await db.PackageFeatures.Where(f => f.FeatureCode == PackageFeatureCodes.EmailReminder).ToListAsync())
            row.FeatureName = "Email reminder";
        foreach (var row in await db.PackageFeatures.Where(f => f.FeatureCode == PackageFeatureCodes.SupportTraining).ToListAsync())
        {
            row.FeatureName = "Support and training"; row.LimitLabel = "Limited"; row.LimitValue = null;
        }
        foreach (var row in await db.PackageFeatures.Where(f => f.FeatureCode == PackageFeatureCodes.PayPalIntegration).ToListAsync())
            row.FeatureName = "PayPal integration";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await PackageEntitlementSeeder.SeedAsync(db);   // the next start-up
        db.ChangeTracker.Clear();

        // "Email reminder" was ticked on every plan with nothing behind it, and 496 withdrew it. 498 built
        // the daily follow-ups email the same afternoon, so the row is back -- under a name that says
        // what it is, never the old one (FollowUpReminderJobTests covers the job itself).
        var reminder = await db.PackageFeatures.Where(f => f.FeatureCode == PackageFeatureCodes.EmailReminder).ToListAsync();
        Assert.NotEmpty(reminder);
        Assert.All(reminder, f => Assert.Equal("Daily follow-up reminder email", f.FeatureName));
        // What exists behind the PayPal row is a Pay Now link on the adviser's client invoices.
        Assert.All(await db.PackageFeatures.Where(f => f.FeatureCode == PackageFeatureCodes.PayPalIntegration).ToListAsync(),
            f => Assert.Equal("Pay Now link on client invoices", f.FeatureName));
        // Support is the same on every plan; "Limited" was defined nowhere.
        var support = await db.PackageFeatures.Where(f => f.FeatureCode == PackageFeatureCodes.SupportTraining).ToListAsync();
        Assert.NotEmpty(support);
        Assert.All(support, f =>
        {
            Assert.Equal("Support by phone, email and portal tickets", f.FeatureName);
            Assert.True(f.IsIncluded);
            Assert.Equal(string.Empty, f.LimitLabel ?? string.Empty);
        });

        // The stale seeded description is replaced; the owner's own text is never touched.
        Assert.Equal("Everything in Silver, plus e-cards, e-letters, unlimited contacts and more storage.",
            (await db.BillingRules.AsNoTracking().SingleAsync(p => p.Id == gold.Id)).Description);
        Assert.Equal("The owner's own words about Platinum.",
            (await db.BillingRules.AsNoTracking().SingleAsync(p => p.Id == platinum.Id)).Description);
    }

    // ---- wording ----------------------------------------------------------------------------

    [Theory]
    [InlineData(@"src\IPRO.Web\Views\Home\Index.cshtml", "No credit card")]
    [InlineData(@"src\IPRO.Web\Views\Home\Index.cshtml", "terminology change to match your market")]
    [InlineData(@"src\IPRO.Web\Views\Home\Index.cshtml", "Policy review, renewal and life-event reminders")]
    [InlineData(@"src\IPRO.Web\Views\Home\Index.cshtml", "Mortgage-specific service pages")]
    [InlineData(@"src\IPRO.Web\Views\Home\Index.cshtml", "with an article library")]
    [InlineData(@"src\IPRO.Web\Views\Home\Index.cshtml", "added as prospects")]
    [InlineData(@"src\IPRO.Web\Views\Home\Index.cshtml", "creates a prospect record")]
    [InlineData(@"src\IPRO.Web\Views\Home\Index.cshtml", "Hosting, domain and SSL are ours")]
    [InlineData(@"src\IPRO.Web\Views\Home\Index.cshtml", "export them whenever you want")]
    [InlineData(@"src\IPRO.Web\Views\Home\Accountants.cshtml", "A professional website, client portal,")]
    [InlineData(@"src\IPRO.Web\Views\Home\Accountants.cshtml", "Try the product")]
    [InlineData(@"src\IPRO.Web\Views\Home\Accountants.cshtml", "in your own name")]
    [InlineData(@"src\IPRO.Web\Views\Home\Mortgage.cshtml", "A professional website, client portal,")]
    [InlineData(@"src\IPRO.Web\Views\Home\Mortgage.cshtml", "document workflow")]
    [InlineData(@"src\IPRO.Web\Views\Home\Mortgage.cshtml", "Try the product")]
    [InlineData(@"src\IPRO.Web\Views\Home\Mortgage.cshtml", "in your own name")]
    [InlineData(@"src\IPRO.Web\Models\RegistrationWelcomeTemplate.cs", "video tutorials")]
    [InlineData(@"src\IPRO.Web\Models\RegistrationWelcomeTemplate.cs", "use this temporary domain right away")]
    [InlineData(@"src\IPRO.Web\Views\Shared\_LegalTerms.cshtml", "SendGrid")]
    [InlineData(@"DOCS\legal\terms-of-service.md", "SendGrid")]
    public void A_promise_the_product_does_not_keep_is_no_longer_made(string file, string phrase)
    {
        Assert.DoesNotContain(phrase, File.ReadAllText(FindRepoFile(file)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void One_number_to_call_is_true_because_the_number_is_published()
    {
        Assert.Equal("1-416-363-2220", PlatformContact.SupportPhone);
        Assert.Equal("tel:+14163632220", PlatformContact.SupportPhoneHref);
        foreach (var file in new[] { @"src\IPRO.Web\Views\Home\Index.cshtml", @"src\IPRO.Web\Views\Home\Accountants.cshtml", @"src\IPRO.Web\Views\Home\Mortgage.cshtml", @"src\IPRO.Web\Views\Home\_LandingFooter.cshtml" })
            Assert.Contains("PlatformContact.SupportPhone", File.ReadAllText(FindRepoFile(file)));
        // The claim stays, now that it is true.
        Assert.Contains("One number to call", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml")));
        Assert.Contains("PlatformContact.SupportPhone", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Models\RegistrationWelcomeTemplate.cs")));
    }

    [Fact]
    public void The_replacement_wording_says_what_is_true()
    {
        var home = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));
        Assert.Contains("Free preview, no card needed", home);
        Assert.Contains("are written for your market", home);
        Assert.Contains("on Platinum", home);

        foreach (var file in new[] { @"src\IPRO.Web\Views\Home\Accountants.cshtml", @"src\IPRO.Web\Views\Home\Mortgage.cshtml" })
        {
            var page = File.ReadAllText(FindRepoFile(file));
            Assert.Contains("on Platinum", page);                      // the client portal is a Platinum feature
            Assert.Contains("Replies come straight to you", page);    // mail leaves from IPRO's sender; the adviser is the reply-to
            Assert.Contains("See your site before you register", page);
        }

        Assert.Contains("Microsoft", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Shared\_LegalTerms.cshtml")));
        Assert.Contains("subscription is active", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Models\RegistrationWelcomeTemplate.cs")));
    }

    [Fact]
    public void The_annual_saving_is_only_claimed_when_the_prices_say_so()
    {
        foreach (var file in new[] { @"src\IPRO.Web\Views\Home\Index.cshtml", @"src\IPRO.Web\Views\Account\Register.cshtml" })
        {
            var view = File.ReadAllText(FindRepoFile(file));
            Assert.Contains("two months free", view);
            Assert.Contains("MonthlyPrice * 10", view);   // the label is conditional on the real prices
        }
    }

    [Fact]
    public void The_preview_shows_the_ai_assistant_only_for_a_plan_that_includes_it()
    {
        var card = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Preview\_MockAiAssistantCard.cshtml"));
        Assert.Contains("aiTiers.Contains(Model.Package)", card);
    }

    [Fact]
    public void A_prospects_name_in_the_preview_link_is_scrubbed_from_telemetry()
    {
        // The preview form is a GET, so the visitor's name and company ride in the URL, and the page
        // says "Nothing is saved".
        var t = new RequestTelemetry { Url = new Uri("https://app.iproadvisers.com/Preview/Show?firstName=Jane&lastName=Doe&companyName=Doe%20Consulting&businessType=Generic") };
        new SensitiveDataTelemetryInitializer().Initialize(t);
        var url = t.Url.ToString();
        Assert.DoesNotContain("Jane", url);
        Assert.DoesNotContain("Doe", url);
        Assert.Contains("businessType=Generic", url);
    }

    // ---- harness ----------------------------------------------------------------------------

    private static async Task SeedStarterContentAsync(IPRODbContext db)
    {
        await WebsiteTemplateSeeder.SeedAsync(db);
        await WebsiteStarterContentSeeder.SeedAsync(db);
        await WebsiteStarterContentSeeder.SeedNavV2AdditionsAsync(db);
        await WebsiteStarterContentSeeder.SeedGenericEditionAsync(db);
        await WebsiteStarterFormSeeder.SeedAsync(db);
        await WebsiteStarterArticleSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();
    }

    private static async Task<AgentUser> SeedAgentAsync(IPRODbContext db, string businessType)
    {
        var rule = new BillingRule { PackageName = ($"T496-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t496-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Nora", LastName = "Field", CompanyName = "Field Accounting",
            DomainName = ($"t496-{Guid.NewGuid():N}")[..24],
            Country = "Canada", Province = "Ontario",
            PackageId = rule.Id, BusinessType = businessType
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IPRO.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
