using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 665 (found 2026-11-18 while building 528; fixed 2026-11-20). A link an adviser types for a button, a menu
// link or the announcement bar is "a page of the site" when it starts with '/'. But "//other-site" and
// "/\other-site" leave the site (browsers read a backslash as a slash), and so does "/<tab>/other-site"
// (browsers drop tabs inside an address). ButtonLinks kept anything starting with '/', and the pages'
// NormalizeUrl refused "//" but kept "/\". The header's Save also dropped a link it could not use without a
// word. Only the adviser can type these into their own site, but a slip would quietly send visitors away.
public class LinksStayOnSite665Tests
{
    public static readonly string[] Leaving = { "//other.example/x", "/\\other.example/x", "/\t/other.example", "/\n/other.example", "/a\\b", "/a b" };

    [Theory]
    [InlineData("/contact")]
    [InlineData("/")]
    [InlineData("/seminars?when=may#top")]
    [InlineData("/info-centre/what-is-payroll")]
    public void A_page_of_the_site_is_one(string path) => Assert.True(SitePaths.IsOwnPage(path));

    [Fact]
    public void A_path_that_leaves_the_site_is_not()
    {
        foreach (var path in Leaving.Append("contact").Append("").Append(null))
            Assert.False(SitePaths.IsOwnPage(path), $"'{path}' must not count as a page of the site");
    }

    [Fact]
    public void Every_rule_an_adviser_types_through_refuses_them()
    {
        var pagesUrl = Rule(typeof(WebsitePagesController));
        var newsletterUrl = Rule(typeof(NewsletterController));
        foreach (var path in Leaving)
        {
            Assert.Equal(string.Empty, ButtonLinks.Normalize(path, pagesUrl));    // header and block buttons
            Assert.Equal(string.Empty, pagesUrl(path));                          // menu links, pictures, review links, price lists
            Assert.Equal(string.Empty, newsletterUrl(path));                     // a newsletter's sidebar buttons
            Assert.Null(WebsiteAnnouncementBar.SafeLink(path));                  // the announcement bar and the pop-up's buttons
        }
        // What they keep is unchanged.
        Assert.Equal("/contact", ButtonLinks.Normalize("/contact", pagesUrl));
        Assert.Equal("tel:416-555-0199", ButtonLinks.Normalize("tel:416-555-0199", pagesUrl));
        Assert.Equal("https://order.example.test/", ButtonLinks.Normalize("https://order.example.test/", pagesUrl));
        Assert.Equal("/seminars", newsletterUrl("/seminars"));
    }

    [Fact]
    public async Task The_header_save_says_a_link_it_did_not_keep()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, websiteId) = await SeedSiteAsync(db);

        var controller = PagesController(db, agentId);
        await controller.SaveHeader("standard", "left", "medium", false, true, true, "Book now", "/\\other.example");
        Assert.StartsWith("Saved, but the button link \"/\\other.example\" was not kept", controller.TempData["Error"] as string);
        db.ChangeTracker.Clear();
        Assert.Equal(string.Empty, WebsiteHeaderSettings.FromJson((await db.AgentWebsites.SingleAsync(w => w.Id == websiteId)).HeaderSettingsJson).ButtonUrl);

        controller = PagesController(db, agentId);
        await controller.SaveHeader("standard", "left", "medium", false, true, true, "Book now", "/book");
        Assert.Equal("Header settings saved.", controller.TempData["Success"]);
        Assert.Null(controller.TempData["Error"]);
        db.ChangeTracker.Clear();
        Assert.Equal("/book", WebsiteHeaderSettings.FromJson((await db.AgentWebsites.SingleAsync(w => w.Id == websiteId)).HeaderSettingsJson).ButtonUrl);
    }

    [Fact]
    public void The_leads_screen_returns_only_to_a_page_of_the_portal()
    {
        var safe = typeof(WebsiteLeadsController).GetMethod("SafeReturnUrl", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal("/portal/WebsiteLeads", safe.Invoke(null, new object?[] { "/\\other.example" }));
        Assert.Equal("/portal/WebsiteLeads", safe.Invoke(null, new object?[] { "//other.example" }));
        Assert.Equal("/portal/WebsiteLeads?status=new", safe.Invoke(null, new object?[] { "/WebsiteLeads?status=new" }));
    }

    private static Func<string?, string> Rule(Type controller)
    {
        var method = controller.GetMethod("NormalizeUrl", BindingFlags.NonPublic | BindingFlags.Static)!;
        return value => (string)method.Invoke(null, new object?[] { value })!;
    }

    private static async Task<(int AgentId, int WebsiteId)> SeedSiteAsync(IPRODbContext db)
    {
        var agent = new AgentUser
        {
            UserName = $"ls-{Guid.NewGuid():N}"[..20],
            Email = $"ls-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Link", LastName = "Rule",
            DomainName = $"ls-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario", TrialEndsAt = null
        };
        db.Add(agent);
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "Test", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true, HeaderSettingsJson = new WebsiteHeaderSettings().ToJson() };
        db.Add(website);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, website.Id);
    }

    private static WebsitePagesController PagesController(IPRODbContext db, int agentId)
    {
        var controller = new WebsitePagesController(db, new PackageEntitlementService(new UnitOfWork(db), db), new NullBlob());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture)) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private sealed class NullBlob : IPRO.Utility.IBlobStorageService
    {
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult("https://blob.example.test/x");
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<System.Collections.Generic.List<string>> ListAsync(string containerName) => Task.FromResult(new System.Collections.Generic.List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blob.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NullTempData : ITempDataProvider
    {
        public System.Collections.Generic.IDictionary<string, object> LoadTempData(HttpContext context) => new System.Collections.Generic.Dictionary<string, object>();
        public void SaveTempData(HttpContext context, System.Collections.Generic.IDictionary<string, object> values) { }
    }
}
