using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 478 (2026-09-11), the second vertical landing page: app.iproadvisers.com/mortgage, built from
// the designer's package (mortgage-page-v1, the one with the corrected four-column footer). Same
// template as /accountants: the platform's header, the shared footer and pricing partials, the live
// mortgage starter-site preview in the frame, register and preview links with the business type.
// ipromortgages.com lands here once it is registered and bound (477).
public class MortgageLandingPageTests
{
    [Fact]
    public async Task The_page_renders_for_everyone_with_the_public_packages_in_the_platforms_order()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        db.AddRange(
            new BillingRule { PackageName = $"Gold {stamp}", MonthlyPrice = 60m, IsActive = true },
            new BillingRule { PackageName = $"Retired {stamp}", MonthlyPrice = 10m, IsActive = false },
            new BillingRule { PackageName = $"Trial {stamp}", MonthlyPrice = 0m, IsActive = true, IsTrialPackage = true },
            new BillingRule { PackageName = $"Hidden {stamp}", MonthlyPrice = 5m, IsActive = true, IsHiddenTestPackage = true });
        await db.SaveChangesAsync();

        var controller = new HomeController(db, new ConfigurationBuilder().Build());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) } };

        var result = Assert.IsType<ViewResult>(await controller.Mortgage());
        var packages = Assert.IsAssignableFrom<List<BillingRule>>(result.Model);
        Assert.Contains(packages, p => p.PackageName == $"Gold {stamp}");
        Assert.DoesNotContain(packages, p => p.PackageName == $"Retired {stamp}");
        Assert.DoesNotContain(packages, p => p.PackageName == $"Trial {stamp}");
        Assert.DoesNotContain(packages, p => p.PackageName == $"Hidden {stamp}");

        // A signed-in agent still sees the landing page (the home redirects them to the dashboard; a
        // marketing page they were sent to must not).
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "test")) } };
        Assert.IsType<ViewResult>(await controller.Mortgage());
    }

    [Fact]
    public void The_view_is_the_designers_page_with_the_platform_slots_filled()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Mortgage.cshtml"));

        // The designer's copy, verbatim where it matters.
        Assert.Contains("The mortgage practice clients find, trust, and return.", view);
        Assert.Contains("Stay visible and organized through every deal.", view);
        Assert.Contains("A site that understands mortgages from day one.", view);
        Assert.Contains("Start with a website built for mortgage advisers.", view);
        Assert.Contains("Websites and Client Management for Mortgage Advisers in Canada | IPRO Advisers", view);

        // The links the brief said we would wire.
        Assert.Contains("/Account/Register?businessType=Mortgage", view);
        Assert.Contains("/Preview/Show?businessType=Mortgage", view);
        Assert.Contains("ViewData[\"BusinessType\"] = \"Mortgage\"", view);   // the pricing partial's register links

        // The four slots are filled by the platform, not left as placeholders or offline stand-ins.
        Assert.DoesNotContain("{{", view);
        Assert.DoesNotContain("Northline", view);
        Assert.Contains("<partial name=\"_LandingPricing\"", view);
        Assert.Contains("<partial name=\"_LandingFooter\"", view);
        Assert.Contains("/Preview/Site?businessType=Mortgage", view);   // the live preview in the frame
        Assert.Contains("/images/ipro-advisers-logo.png", view);        // the platform's header
        Assert.Contains("/Account/Login", view);
        Assert.DoesNotContain("/Home/Terms", view);

        // No scripts from anywhere, and the stylesheet is the one both vertical pages share.
        Assert.DoesNotContain("<script src=\"http", view);
        Assert.Contains("/css/landing-page.css", view);
        Assert.DoesNotContain("accountants-page.css", view);
    }

    [Fact]
    public void The_shared_footer_and_pricing_partials_carry_the_live_links()
    {
        // The designer's corrected four-column footer, shared by every vertical page. Its legal links
        // are the live routes (/terms and /privacy); the first cut of /accountants linked /Home/Terms
        // and /Home/Privacy, which answer 404 in production.
        var footer = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\_LandingFooter.cshtml"));
        Assert.Contains("class=\"container footer-grid\"", footer);   // the four-column footer, as the designer built it
        foreach (var href in new[] { "/terms", "/privacy", "/accountants", "/mortgage", "/Account/Login", "/Preview" })
            Assert.Contains("href=\"" + href + "\"", footer);
        Assert.Contains("facebook.com/p/iPRO-100071151034796", footer);
        Assert.Contains("youtube.com/@@AllAdvisers", footer);   // @@ is Razor's literal @
        Assert.DoesNotContain("/Home/Terms", footer);
        Assert.DoesNotContain("/Home/Privacy", footer);

        // The live package cards, one partial for every vertical page, register links with the page's
        // business type.
        var pricing = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\_LandingPricing.cshtml"));
        Assert.Contains("@foreach (var package in Model)", pricing);
        Assert.Contains("MonthlyPrice", pricing);
        Assert.Contains("ViewData[\"BusinessType\"]", pricing);
        Assert.Contains("#i2-pricing", pricing);
    }

    [Fact]
    public void The_stylesheet_and_assets_are_shared_by_the_vertical_pages()
    {
        // One stylesheet for every vertical page: the designer's mortgage-page-v1 sheet (the corrected
        // footer, the phone-width fix) plus the platform's slot styles.
        var css = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\wwwroot\css\landing-page.css"));
        Assert.Contains("--navy: #173b55", css);
        Assert.Contains("grid-template-columns: minmax(0, 1fr)", css);
        Assert.Contains(".footer-grid {", css);
        Assert.Contains(".pricing-card {", css);
        Assert.Contains(".preview-live {", css);
        Assert.False(File.Exists(FindRepoFile(@"src\IPRO.Web\wwwroot\css\accountants-page.css")), "the per-page stylesheet should be gone");

        foreach (var asset in new[] { "icon-website.svg", "icon-portal.svg", "icon-followups.svg", "icon-newsletter.svg" })
            Assert.True(File.Exists(FindRepoFile(@"src\IPRO.Web\wwwroot\images\landing\" + asset)), asset + " is missing");
    }

    [Fact]
    public void The_short_addresses_are_routed_and_the_mortgage_domain_will_land_here()
    {
        var action = typeof(HomeController).GetMethod("Mortgage");
        Assert.NotNull(action);
        var routes = action!.GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>().Select(a => a.Template).ToList();
        Assert.Contains("/mortgage", routes);
        Assert.Contains("/mortgages", routes);   // the designer's README named the page /mortgages

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:AliasHosts"] = "www.ipromortgages.com=/mortgage,ipromortgages.com=/mortgage",
            ["App:BaseUrl"] = "https://app.iproadvisers.com"
        }).Build();
        Assert.Equal("https://app.iproadvisers.com/mortgage", PlatformAliasHosts.RedirectTarget(config, "www.ipromortgages.com"));
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
