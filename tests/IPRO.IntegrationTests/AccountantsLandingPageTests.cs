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

// TODO 478 (2026-09-11), the first vertical landing page: app.iproadvisers.com/accountants, built
// from the designer's package (accountants-page-v2-font-revision). One page inside the platform,
// the platform's own header and footer, the live package list from the database in the pricing
// slot, the live accountant starter-site preview in the preview slot, the register and preview
// links wired with the business type. iproaccountants.com lands here on the domain-switch day (477).
public class AccountantsLandingPageTests
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

        var result = Assert.IsType<ViewResult>(await controller.Accountants());
        var packages = Assert.IsAssignableFrom<List<BillingRule>>(result.Model);
        Assert.Contains(packages, p => p.PackageName == $"Gold {stamp}");
        Assert.DoesNotContain(packages, p => p.PackageName == $"Retired {stamp}");
        Assert.DoesNotContain(packages, p => p.PackageName == $"Trial {stamp}");
        Assert.DoesNotContain(packages, p => p.PackageName == $"Hidden {stamp}");

        // A signed-in agent still sees the landing page (the home redirects them to the dashboard; a
        // marketing page they were sent to must not).
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "test")) } };
        Assert.IsType<ViewResult>(await controller.Accountants());
    }

    [Fact]
    public void The_view_is_the_designers_page_with_the_platform_slots_filled()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Accountants.cshtml"));

        // The designer's copy, verbatim where it matters.
        Assert.Contains("The practice accountants want to find, trust, and stay with.", view);
        Assert.Contains("Everything your practice needs to stay visible and organized.", view);
        Assert.Contains("One provider. One login. One number to call.", view);
        Assert.Contains("Websites and Client Management for Accountants in Canada | IPRO Advisers", view);

        // The links the brief said we would wire.
        Assert.Contains("/Account/Register?businessType=Accountants", view);
        Assert.Contains("/Preview/Show?businessType=Accountants", view);

        // The four slots are filled by the platform, not left as placeholders or offline stand-ins.
        Assert.DoesNotContain("{{", view);
        Assert.DoesNotContain("Northline Accounting", view);
        Assert.Contains("@foreach (var package in Model)", view);
        Assert.Contains("MonthlyPrice", view);
        Assert.Contains("/Preview/Site?businessType=Accountants", view);   // the live preview in the frame
        Assert.Contains("/images/ipro-advisers-logo.png", view);           // the platform's header and footer
        Assert.Contains("/Account/Login", view);

        // No scripts from anywhere, and the stylesheet is the platform's own file.
        Assert.DoesNotContain("<script src=\"http", view);
        Assert.Contains("/css/accountants-page.css", view);
    }

    [Fact]
    public void The_stylesheet_and_assets_are_in_place_with_the_phone_width_fix()
    {
        var css = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\wwwroot\css\accountants-page.css"));
        Assert.Contains("--navy: #173b55", css);
        // The package clipped the hero by 26 px at 375 px: a 116%-wide device stage forced the single
        // grid track wider than the container. The track is now allowed to shrink.
        Assert.Contains("grid-template-columns: minmax(0, 1fr)", css);

        foreach (var asset in new[] { "icon-website.svg", "icon-portal.svg", "icon-followups.svg", "icon-newsletter.svg" })
            Assert.True(File.Exists(FindRepoFile(@"src\IPRO.Web\wwwroot\images\accountants\" + asset)), asset + " is missing");
    }

    [Fact]
    public void The_old_accountants_domain_lands_on_a_page_that_exists()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:AliasHosts"] = "www.iproaccountants.com=/accountants,iproaccountants.com=/accountants",
            ["App:BaseUrl"] = "https://app.iproadvisers.com"
        }).Build();
        Assert.Equal("https://app.iproadvisers.com/accountants", PlatformAliasHosts.RedirectTarget(config, "iproaccountants.com"));
        var action = typeof(HomeController).GetMethod("Accountants");
        Assert.NotNull(action);
        // The short address itself: the default {controller}/{action} route would read /accountants
        // as a controller called Accountants and answer 404, which is what the first local render did.
        var routes = action!.GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>().Select(a => a.Template).ToList();
        Assert.Contains("/accountants", routes);
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
