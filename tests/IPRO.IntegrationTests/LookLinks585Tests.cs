using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.Business.SiteImport;
using IPRO.DataAccess.Repositories;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 585 (the owner's bench walk-through, 2026-10-19): a look at what was sent from the builder ("See it on
// my site", the adviser's review, SuperAdmin's 571 look) drew the site's menu, buttons and footer with
// the site's own addresses ("/about"). On iPro's host those are iPro's pages: a click left the look for
// a "Page not found". Every link to one of the site's own pages now stays in the look, at the same
// address with the same key and choices and page=<that page>; a page the look does not have says so.
public class LookLinks585Tests
{
    private const string Look = "/PublicWebsite/PreviewBuilderImport?id=14&key=k";

    [Theory]
    [InlineData("<a href=\"/about\">About</a>", "<a href=\"/PublicWebsite/PreviewBuilderImport?id=14&amp;key=k&amp;page=about\">About</a>")]
    [InlineData("<a class=\"b\" href=\"/Contact#form\">Go</a>", "<a class=\"b\" href=\"/PublicWebsite/PreviewBuilderImport?id=14&amp;key=k&amp;page=contact#form\">Go</a>")]
    [InlineData("<a href=\"/\">Home</a>", "<a href=\"/PublicWebsite/PreviewBuilderImport?id=14&amp;key=k\">Home</a>")]
    [InlineData("<a href=\"/services/\">S</a>", "<a href=\"/PublicWebsite/PreviewBuilderImport?id=14&amp;key=k&amp;page=services\">S</a>")]
    // The look's own address (a blog's "read more" is written against the request's path) stays on the page shown.
    [InlineData("<a href=\"/PublicWebsite/PreviewBuilderImport?post=5\">More</a>", "<a href=\"/PublicWebsite/PreviewBuilderImport?id=14&amp;key=k&amp;page=home\">More</a>")]
    public void A_link_to_one_of_the_sites_own_pages_stays_in_the_look(string html, string expected) =>
        Assert.Equal(expected, new LookLinks(Look, "home").Rewrite(html));

    [Theory]
    [InlineData("<a href=\"https://example.test/about\">x</a>")]                       // another site
    [InlineData("<a href=\"//cdn.example.test/x\">x</a>")]
    [InlineData("<a href=\"mailto:someone@example.test\">x</a>")]
    [InlineData("<a href=\"tel:+15550100\">x</a>")]
    [InlineData("<a href=\"#top\">x</a>")]
    [InlineData("<a href=\"/PublicWebsite/PreviewBuilderImport?id=14&amp;key=k&amp;page=about\">x</a>")]   // already in the look
    [InlineData("<a href=\"/portal/Account/Login\">x</a>")]                            // not one of the site's pages
    [InlineData("<link rel=\"stylesheet\" href=\"/css/site.css\" />")]                 // not a link a visitor clicks
    [InlineData("<form action=\"/PublicWebsite/SubmitLead\" method=\"post\"></form>")]
    public void Anything_else_is_left_as_it_is(string html) =>
        Assert.Equal(html, new LookLinks(Look, "home").Rewrite(html));

    [Fact]
    public async Task A_look_at_a_package_keeps_its_menu_and_buttons_inside_the_look_and_says_so_of_a_missing_page()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var seeded = await SiteHandoverReturn572Tests.SeedFittingSiteAsync(db, blob);
        var handover = await SiteHandoverService.BuildAsync(db, blob, seeded.Site, SiteHandoverService.AllowedBlockTypes(true, true, true), 0);
        await SiteHandoverService.RecordAsync(db, seeded.Site, handover, "owner");
        var zip = SiteHandoverReturn572Tests.Return(handover.Zip, t =>
        {
            var hero = SiteHandoverReturn572Tests.Block(t, "home", 0);
            hero["buttonText"] = "About the bakery";
            hero["buttonUrl"] = "/about";
        });
        var package = BuilderPackageReader.Read(zip, await BuilderImportService.ContextAsync(db, seeded.Site, 0, handover.HandoverId));
        Assert.True(package.IsValid, string.Join(" ", package.Problems.Select(p => p.Message)));
        var import = await BuilderImportService.StoreAsync(db, blob, seeded.Site, package, "site.zip", "owner");
        var key = await BuilderImportService.LookKeyAsync(db, import);
        db.ChangeTracker.Clear();

        var portal = Portal(db, blob);
        var shown = Assert.IsType<ViewResult>(await portal.PreviewBuilderImport(import.Id, key, null, null, null, null));
        var html = await RenderAsync(shown);
        var look = $"/PublicWebsite/PreviewBuilderImport?id={import.Id}&amp;key={key}";
        Assert.Contains($"href=\"{look}&amp;page=about\"", html);                 // the menu, and the hero's button
        Assert.Contains($"href=\"{look}&amp;page=contact\"", html);
        Assert.DoesNotContain("href=\"/about\"", html);
        Assert.DoesNotContain("href=\"/contact\"", html);
        // Every link a visitor can click to an address on this host stays in the look.
        foreach (Match link in Regex.Matches(html, "<a\\b[^>]*?\\shref=\"(/[^/\"][^\"]*|/)\""))
            Assert.StartsWith("/PublicWebsite/PreviewBuilderImport?id=", link.Groups[1].Value);

        // A page the look does not have: the home page, and the banner says so.
        var missing = Portal(db, blob);
        Assert.IsType<ViewResult>(await missing.PreviewBuilderImport(import.Id, key, "no-such-page", null, null, null));
        Assert.StartsWith("There is no page at /no-such-page in this look, so it shows the home page.", (string)missing.ViewData["PreviewBanner"]!);
    }

    private static PublicWebsiteController Portal(IPRO.DataAccess.IPRODbContext db, IPRO.Utility.IBlobStorageService blob) => new(
        db, new PackageEntitlementService(new UnitOfWork(db), db), null!, NullLogger<PublicWebsiteController>.Instance, new ConfigurationBuilder().Build(),
        DataProtectionProvider.Create($"look-links-{Guid.NewGuid():N}"), blob, new MemoryCache(new MemoryCacheOptions()), null!)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    // The look's view rendered as the portal renders it, with what the action put in its ViewData.
    private static async Task<string> RenderAsync(ViewResult result)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions
        {
            ApplicationName = typeof(PublicWebsiteController).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(RepoRoot(), "src", "IPRO.Web"),
            EnvironmentName = "Production"
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(PublicWebsiteController).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Path = "/PublicWebsite/PreviewBuilderImport";
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "render"));
        var route = new Microsoft.AspNetCore.Routing.RouteData();
        route.Values["controller"] = "PublicWebsite";   // where the page's partials are looked for
        route.Values["action"] = "PreviewBuilderImport";
        var actionContext = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var engine = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Mvc.Razor.IRazorViewEngine>();
        var found = engine.GetView(executingFilePath: null, viewPath: result.ViewName!, isMainPage: true);
        Assert.True(found.Success, $"{result.ViewName} was not found among the portal's compiled views");
        await using var writer = new StringWriter();
        await found.View!.RenderAsync(new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(actionContext, found.View, result.ViewData,
            new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NoTempData()), writer, new Microsoft.AspNetCore.Mvc.ViewFeatures.HtmlHelperOptions()));
        return writer.ToString();
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public System.Collections.Generic.IDictionary<string, object> LoadTempData(HttpContext context) => new System.Collections.Generic.Dictionary<string, object>();
        public void SaveTempData(HttpContext context, System.Collections.Generic.IDictionary<string, object> values) { }
    }
}
