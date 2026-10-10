using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace IPRO.IntegrationTests;

// 560 (2026-10-10). The owner: "I am also thinking out having a google review section. It seems it
// is important for some potential clients." The Reviews block showed stars, a count and a link, all
// typed by hand, and never a review. The owner can now paste in up to six reviews they picked (name,
// stars, words); they show as cards under the rating, with an optional "Review us" link. Nothing is
// fetched from Google: no key, no running cost, and the owner chooses what appears.
public class ReviewQuotes560Tests
{
    [Fact]
    public void Picked_reviews_are_kept_as_typed_within_bounds()
    {
        var quotes = WebsiteReviewSettings.QuotesFromEntries(new (string?, int, string?)[]
        {
            (" Dana K. ", 5, "  Best croissants in Toronto!  "),
            ("No words", 4, "   "),                                   // a row without text is no review
            (null, 9, "Lovely staff."),                               // stars outside 1 to 5 read as 5
            (new string('n', 200), 1, new string('t', 900)),
        });
        Assert.Equal(3, quotes.Count);
        Assert.Equal(("Dana K.", 5, "Best croissants in Toronto!"), (quotes[0].Name, quotes[0].Stars, quotes[0].Text));
        Assert.Equal((string.Empty, 5, "Lovely staff."), (quotes[1].Name, quotes[1].Stars, quotes[1].Text));
        Assert.Equal(WebsiteReviewSettings.QuoteNameMaxLength, quotes[2].Name.Length);
        Assert.Equal(WebsiteReviewSettings.QuoteTextMaxLength, quotes[2].Text.Length);
        Assert.Equal(1, quotes[2].Stars);

        // Never more than six.
        Assert.Equal(WebsiteReviewSettings.MaxQuotes,
            WebsiteReviewSettings.QuotesFromEntries(Enumerable.Range(1, 10).Select(i => ((string?)$"R{i}", 5, (string?)$"Review {i}"))).Count);

        // A block saved before 560 has none; they survive a save and a read; a stored link must be a web address.
        Assert.Empty(WebsiteReviewSettings.FromJson("{\"Platform\":\"Google\",\"Rating\":4.8,\"ReviewCount\":112}").Quotes);
        var back = WebsiteReviewSettings.FromJson(new WebsiteReviewSettings { Rating = 4.8m, ReviewCount = 112, Quotes = quotes, WriteReviewUrl = "https://g.page/r/abc/review" }.ToJson());
        Assert.Equal(3, back.Quotes.Count);
        Assert.Equal("https://g.page/r/abc/review", back.WriteReviewUrl);
        Assert.Equal(4.8m, back.Rating);
        Assert.Equal(string.Empty, WebsiteReviewSettings.FromJson("{\"WriteReviewUrl\":\"javascript:alert(1)\"}").WriteReviewUrl);
    }

    [Fact]
    public async Task The_block_editor_saves_them_and_a_save_without_the_rows_keeps_them()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, pageId) = await SeedSiteAsync(db);
        await NewController(db, agentId).AddBlock(pageId, WebsiteBlockTypes.Reviews);
        var blockId = await db.WebsiteContentBlocks.Where(b => b.WebsitePageId == pageId).Select(b => b.Id).SingleAsync();
        db.ChangeTracker.Clear();

        // The editor's form: six rows, two filled.
        var form = new Dictionary<string, StringValues>
        {
            ["reviewQuotesPosted"] = "1",
            ["reviewQuoteName"] = new[] { "Dana K.", "", "Sam", "", "", "" },
            ["reviewQuoteStars"] = new[] { "5", "5", "4", "5", "5", "5" },
            ["reviewQuoteText"] = new[] { "Best croissants in Toronto!", "", "A neighbourhood gem.", "", "", "" },
            ["reviewWriteUrl"] = "https://g.page/r/abc/review",
        };
        await Save(NewController(db, agentId, form), blockId);
        db.ChangeTracker.Clear();
        var saved = WebsiteReviewSettings.FromJson((await db.WebsiteContentBlocks.AsNoTracking().SingleAsync(b => b.Id == blockId)).SettingsJson);
        Assert.Equal(new[] { "Dana K.", "Sam" }, saved.Quotes.Select(q => q.Name));
        Assert.Equal(new[] { 5, 4 }, saved.Quotes.Select(q => q.Stars));
        Assert.Equal("A neighbourhood gem.", saved.Quotes[1].Text);
        Assert.Equal("https://g.page/r/abc/review", saved.WriteReviewUrl);
        Assert.Equal(4.8m, saved.Rating);

        // A save that does not carry the rows (not the editor's form) changes the rating and keeps the reviews.
        await Save(NewController(db, agentId), blockId, rating: 4.9m);
        db.ChangeTracker.Clear();
        var kept = WebsiteReviewSettings.FromJson((await db.WebsiteContentBlocks.AsNoTracking().SingleAsync(b => b.Id == blockId)).SettingsJson);
        Assert.Equal(4.9m, kept.Rating);
        Assert.Equal(2, kept.Quotes.Count);
        Assert.Equal("https://g.page/r/abc/review", kept.WriteReviewUrl);

        // The editor's form with every row emptied removes them; a link that is not a web address is dropped.
        var cleared = new Dictionary<string, StringValues>
        {
            ["reviewQuotesPosted"] = "1",
            ["reviewQuoteName"] = new[] { "", "" }, ["reviewQuoteStars"] = new[] { "5", "5" }, ["reviewQuoteText"] = new[] { "", "" },
            ["reviewWriteUrl"] = "javascript:alert(1)",
        };
        await Save(NewController(db, agentId, cleared), blockId);
        db.ChangeTracker.Clear();
        var emptied = WebsiteReviewSettings.FromJson((await db.WebsiteContentBlocks.AsNoTracking().SingleAsync(b => b.Id == blockId)).SettingsJson);
        Assert.Empty(emptied.Quotes);
        Assert.Equal(string.Empty, emptied.WriteReviewUrl);
    }

    [Fact]
    public void All_three_designs_show_the_cards_under_the_rating_and_nothing_when_there_are_none()
    {
        foreach (var (design, name) in new[] { ("_ModernManagedPage", "mpReview"), ("_ClassicManagedPage", "cpReview"), ("_EditorialManagedPage", "epReview") })
            Assert.Equal(1, Count(Read($@"src\IPRO.Web\Views\PublicWebsite\{design}.cshtml"), $"@await Html.PartialAsync(\"_ReviewQuotes\", {name})"));

        var cards = Read(@"src\IPRO.Web\Views\PublicWebsite\_ReviewQuotes.cshtml");
        Assert.Contains("@if (Model.Quotes.Count > 0 || Model.WriteReviewUrl.Length > 0)", cards);
        Assert.Contains("<blockquote class=\"site-reviews__text\">@quote.Text</blockquote>", cards);      // encoded: never raw
        Assert.DoesNotContain("Html.Raw", cards);
        Assert.Contains("role=\"img\" aria-label=\"@quote.Stars out of 5 stars\"", cards);
        Assert.Contains("<a href=\"@Model.WriteReviewUrl\" target=\"_blank\" rel=\"noopener\">Review us on @Model.Platform", cards);
        Assert.Contains(".site-reviews__grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 18px; }", Read(@"src\IPRO.Web\Views\PublicWebsite\_ManagedPageStyles.cshtml"));

        var editor = Read(@"src\IPRO.Web\Views\WebsitePages\Edit.cshtml");
        Assert.Contains("<input type=\"hidden\" name=\"reviewQuotesPosted\" value=\"1\" />", editor);
        Assert.Contains("name=\"reviewQuoteName\"", editor);
        Assert.Contains("name=\"reviewQuoteStars\"", editor);
        Assert.Contains("name=\"reviewQuoteText\"", editor);
        Assert.Contains("name=\"reviewWriteUrl\"", editor);
        Assert.Contains("These do not update by themselves.", editor);
        var controller = Read(@"src\IPRO.Web\Controllers\WebsitePagesController.cs");
        Assert.Contains("if (Request.HasFormContentType && Request.Form.ContainsKey(\"reviewQuotesPosted\"))", controller);

        var guide = Read(@"DOCS\04_WEBSITE_BUILDER.md");
        Assert.Contains("## Show Reviews From Google", guide);
        Assert.Contains("check your regulator's rules on client testimonials", guide);
    }

    // -------------------------------------------------------------------------- helpers --

    private static async Task Save(WebsitePagesController controller, int blockId, decimal rating = 4.8m)
    {
        var result = await controller.UpdateBlock(blockId, "What our customers say", "Reviews", "", "", "", "", true,
            reviewPlatform: "Google", reviewUrl: "https://g.page/r/abc", reviewRating: rating, reviewCount: 112);
        Assert.IsType<RedirectToActionResult>(result);
    }

    private static async Task<(int AgentId, int PageId)> SeedSiteAsync(IPRODbContext db)
    {
        var agent = new AgentUser
        {
            UserName = $"rv-{Guid.NewGuid():N}"[..20], Email = $"rv-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Review", LastName = "Owner", DomainName = $"rv-{Guid.NewGuid():N}"[..24]
        };
        db.Add(agent);
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "T", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true };
        db.Add(website);
        await db.SaveChangesAsync();
        var page = new WebsitePage { AgentWebsiteId = website.Id, Title = "Home", Slug = "home", IsHomePage = true, IsPublished = true };
        db.Add(page);
        await db.SaveChangesAsync();
        return (agent.Id, page.Id);
    }

    private static WebsitePagesController NewController(IPRODbContext db, int agentId, Dictionary<string, StringValues>? form = null)
    {
        var controller = new WebsitePagesController(db, new PackageEntitlementService(new UnitOfWork(db), db), new NullBlob());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        if (form != null)
        {
            context.Request.ContentType = "application/x-www-form-urlencoded";
            context.Request.Form = new FormCollection(form);
        }
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private sealed class NullBlob : IPRO.Utility.IBlobStorageService
    {
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult($"https://blob.example.test/{containerName}/{fileName}");
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(new List<string>());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blob.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, string.Empty).Length) / part.Length;

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
