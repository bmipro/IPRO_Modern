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
using Xunit;

namespace IPRO.IntegrationTests;

// 549 (2026-10-04). The owner hopes to sign a bakery (L'Avenue Boulangerie: a Bread & Pastries menu and a
// Drinks and Meals menu, prices like "$3.50 / $3.75 / $4.10") and asked about a menu option. He chose a
// generic block "that would looks as good for a bakery and accountant", with three layouts. The website
// builder had no price anywhere: Services is a list of names. The Price List / Menu block is sections of
// items -- a name, a price written as the business writes it, an optional description, badge and photo,
// and an Available switch -- drawn as a classic menu, photo cards or two compact columns, in all three
// templates, marked up for search engines as a Menu or an OfferCatalog.
public class PriceList549Tests
{
    [Fact]
    public void The_block_is_registered_with_its_three_layouts()
    {
        Assert.Contains(WebsiteBlockTypes.PriceList, WebsiteBlockTypes.All);
        Assert.Equal("Price List / Menu", WebsiteBlockTypes.DisplayName(WebsiteBlockTypes.PriceList));
        Assert.Equal(new[] { "list", "cards", "compact" }, WebsiteBlockLayoutVariants.AllowedFor(WebsiteBlockTypes.PriceList));
        Assert.Equal("cards", WebsiteBlockLayoutVariants.Normalize(WebsiteBlockTypes.PriceList, "Cards"));
        Assert.Equal(string.Empty, WebsiteBlockLayoutVariants.Normalize(WebsiteBlockTypes.PriceList, "masonry"));
    }

    [Fact]
    public void Cleaning_keeps_what_a_menu_needs_and_stores_nothing_it_must_not()
    {
        var posted = new WebsitePriceListSettings
        {
            Kind = "something else",
            Sections =
            {
                new WebsitePriceListSection
                {
                    Title = "  Hot Drinks  ", Note = "Oat milk +$0.75",
                    Items =
                    {
                        new WebsitePriceListItem { Name = " Americano ", Price = " $3.50 / $3.75 / $4.10 ", Badge = "Popular" },
                        new WebsitePriceListItem { Name = "   ", Price = "$9" },                        // no name: dropped
                        new WebsitePriceListItem { Name = new string('x', 300), Description = new string('d', 900), Available = false }
                    }
                },
                new WebsitePriceListSection(),                                                           // empty: dropped
                new WebsitePriceListSection { Title = "Coming soon" }                                    // a title alone is kept
            }
        };

        var clean = WebsitePriceListSettings.Clean(posted, url => url ?? string.Empty);

        Assert.Equal(PriceListKinds.Prices, clean.Kind);
        Assert.Equal(new[] { "Hot Drinks", "Coming soon" }, clean.Sections.Select(s => s.Title));
        var items = clean.Sections[0].Items;
        Assert.Equal(2, items.Count);
        Assert.Equal("Americano", items[0].Name);
        Assert.Equal("$3.50 / $3.75 / $4.10", items[0].Price);
        Assert.Equal(WebsitePriceListItem.NameMax, items[1].Name.Length);
        Assert.Equal(WebsitePriceListItem.DescriptionMax, items[1].Description.Length);
        Assert.False(items[1].Available);

        // The caps: 30 sections, 100 items a section, 400 in all.
        var big = new WebsitePriceListSettings();
        for (var s = 0; s < 40; s++)
            big.Sections.Add(new WebsitePriceListSection { Title = $"S{s}", Items = Enumerable.Range(0, 150).Select(i => new WebsitePriceListItem { Name = $"I{i}" }).ToList() });
        var capped = WebsitePriceListSettings.Clean(big, url => url ?? string.Empty);
        Assert.Equal(WebsitePriceListSettings.MaxSections, capped.Sections.Count);
        Assert.Equal(WebsitePriceListSettings.MaxItemsPerSection, capped.Sections[0].Items.Count);
        Assert.Equal(WebsitePriceListSettings.MaxItems, capped.Sections.Sum(s => s.Items.Count));

        Assert.False(WebsitePriceListSettings.TryParse("", out _));
        Assert.False(WebsitePriceListSettings.TryParse("not json", out _));
        Assert.False(WebsitePriceListSettings.TryParse("[1,2]", out _));
        Assert.True(WebsitePriceListSettings.TryParse("{\"Sections\":[]}", out _));
    }

    [Theory]
    [InlineData("$4.25", "4.25")]
    [InlineData("4", "4")]
    [InlineData("$ 12.50", "12.50")]
    [InlineData("12 $", "12")]
    [InlineData("from $25", null)]
    [InlineData("$3.50 / $3.75 / $4.10", null)]
    [InlineData("$12 a dozen", null)]
    [InlineData("", null)]
    public void Only_a_plain_single_amount_goes_to_search_engines_as_a_number(string price, string? expected) =>
        Assert.Equal(expected, PriceListText.SingleAmount(price));

    [Fact]
    public async Task A_new_block_starts_with_an_example_list_to_overwrite()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, pageId) = await SeedSiteAsync(db);

        Assert.IsType<RedirectToActionResult>(await NewController(db, agentId).AddBlock(pageId, WebsiteBlockTypes.PriceList));

        var block = await db.WebsiteContentBlocks.AsNoTracking().SingleAsync(b => b.WebsitePageId == pageId);
        Assert.Equal(WebsiteBlockTypes.PriceList, block.BlockType);
        Assert.Equal("Our prices", block.Heading);
        Assert.Equal(string.Empty, block.Body);
        var settings = WebsitePriceListSettings.FromJson(block.SettingsJson);
        Assert.True(settings.HasItems);
        Assert.Equal(new[] { "First item", "Second item" }, settings.Sections.Single().Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Saving_stores_the_cleaned_list_and_a_broken_post_never_empties_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, pageId) = await SeedSiteAsync(db);
        await NewController(db, agentId).AddBlock(pageId, WebsiteBlockTypes.PriceList);
        var blockId = await db.WebsiteContentBlocks.Where(b => b.WebsitePageId == pageId).Select(b => b.Id).SingleAsync();
        db.ChangeTracker.Clear();

        // The bakery's bread menu, as the editor posts it -- with one picture address that must not be stored.
        const string bakery = """
            {"Kind":"prices","Sections":[
              {"Title":"Baked fresh daily from the oven","Note":"","ImageUrl":"/images/landing/icon-website.svg","Items":[
                {"Name":"Baguette","Price":"$4.25","Description":"Baked fresh daily","Badge":"","ImageUrl":"javascript:alert(1)","Available":true},
                {"Name":"Gourmet Flat Bread","Price":"$7.50 Medium, $10.00 Large","Description":"","Badge":"New","ImageUrl":"https://example.test/flat.jpg","Available":true},
                {"Name":"Sourdough Loaf - Olive","Price":"$8.75","Description":"","Badge":"","ImageUrl":"//evil.test/x.jpg","Available":false}]}]}
            """;
        await Save(db, agentId, blockId, layoutVariant: "Cards", kind: "menu", json: bakery);

        var stored = await db.WebsiteContentBlocks.AsNoTracking().SingleAsync(b => b.Id == blockId);
        Assert.Equal("cards", stored.LayoutVariant);
        var list = WebsitePriceListSettings.FromJson(stored.SettingsJson);
        Assert.Equal(PriceListKinds.Menu, list.Kind);
        var section = list.Sections.Single();
        Assert.Equal("/images/landing/icon-website.svg", section.ImageUrl);
        Assert.Equal(new[] { "Baguette", "Gourmet Flat Bread", "Sourdough Loaf - Olive" }, section.Items.Select(i => i.Name));
        Assert.Equal(string.Empty, section.Items[0].ImageUrl);                               // javascript: dropped
        Assert.Equal("https://example.test/flat.jpg", section.Items[1].ImageUrl);
        Assert.Equal(string.Empty, section.Items[2].ImageUrl);                               // protocol-relative dropped
        Assert.Equal("$7.50 Medium, $10.00 Large", section.Items[1].Price);
        Assert.False(section.Items[2].Available);

        // A post without the list, or with a broken one, keeps the list (the kind still follows the form).
        await Save(db, agentId, blockId, layoutVariant: "cards", kind: "prices", json: "");
        await Save(db, agentId, blockId, layoutVariant: "cards", kind: "prices", json: "{broken");
        var kept = WebsitePriceListSettings.FromJson((await db.WebsiteContentBlocks.AsNoTracking().SingleAsync(b => b.Id == blockId)).SettingsJson);
        Assert.Equal(3, kept.Sections.Single().Items.Count);
        Assert.Equal(PriceListKinds.Prices, kept.Kind);

        // An empty list is a real choice, and is stored.
        await Save(db, agentId, blockId, layoutVariant: "cards", kind: "prices", json: "{\"Sections\":[]}");
        Assert.False(WebsitePriceListSettings.FromJson((await db.WebsiteContentBlocks.AsNoTracking().SingleAsync(b => b.Id == blockId)).SettingsJson).HasItems);
    }

    [Theory]
    [InlineData("tel:416 333-4455", "tel:416 333-4455")]
    [InlineData("tel:+1 (416) 333-4455", "tel:+1 (416) 333-4455")]
    [InlineData("mailto:info@lavenuebakery.example", "mailto:info@lavenuebakery.example")]
    // 558: the bakery writes its number with a bracket first; a number or an address typed on its own is a link too.
    [InlineData("tel:(416)-886-0458", "tel:(416)-886-0458")]
    [InlineData("(416)-886-0458", "tel:(416)-886-0458")]
    [InlineData("416 886 0458", "tel:416 886 0458")]
    [InlineData("info@lavenuebakery.example", "mailto:info@lavenuebakery.example")]
    [InlineData("12345", "")]
    [InlineData("call us", "")]
    [InlineData("/contact", "/contact")]
    [InlineData("https://order.example.test/lavenue", "https://order.example.test/lavenue")]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("tel:javascript:alert(1)", "")]
    [InlineData("mailto:<script>@x.y", "")]
    public async Task A_button_may_call_a_number_or_start_an_email_and_nothing_else_new(string link, string expected)
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, pageId) = await SeedSiteAsync(db);
        await NewController(db, agentId).AddBlock(pageId, WebsiteBlockTypes.PriceList);
        var blockId = await db.WebsiteContentBlocks.Where(b => b.WebsitePageId == pageId).Select(b => b.Id).SingleAsync();
        db.ChangeTracker.Clear();

        await Save(db, agentId, blockId, layoutVariant: "", kind: "menu", json: "", buttonText: "Call to order", buttonUrl: link);

        Assert.Equal(expected, (await db.WebsiteContentBlocks.AsNoTracking().SingleAsync(b => b.Id == blockId)).ButtonUrl);
    }

    [Fact]
    public void The_editor_the_three_templates_and_the_guide_carry_it()
    {
        var editor = Read(@"src\IPRO.Web\Views\WebsitePages\Edit.cshtml");
        Assert.Contains("<input type=\"hidden\" name=\"priceListJson\" value=\"@priceList.ToJson()\" />", editor);
        Assert.Contains("<select name=\"priceListKind\" class=\"form-select\">", editor);
        Assert.Contains("document.querySelectorAll('[data-price-list]').forEach(root => {", editor);
        Assert.Contains("field.value = JSON.stringify({ Kind: data.Kind || 'prices', Sections: sections });", editor);
        Assert.Contains("if (form) form.addEventListener('submit', write);", editor);
        Assert.Contains("<option value=\"compact\" selected=\"@(block.LayoutVariant == \"compact\" ? \"selected\" : null)\">Compact (two columns, for long lists)</option>", editor);

        foreach (var template in new[] { "_ClassicManagedPage", "_ModernManagedPage", "_EditorialManagedPage" })
        {
            var src = Read($@"src\IPRO.Web\Views\PublicWebsite\{template}.cshtml");
            Assert.Contains("if (IPRO.Entities.WebsitePriceListSettings.FromJson(block.SettingsJson).HasItems)", src);
            Assert.Contains("@await Html.PartialAsync(\"_PriceList\", block)", src);
        }

        var partial = Read(@"src\IPRO.Web\Views\PublicWebsite\_PriceList.cshtml");
        Assert.Contains("<div class=\"pl pl-@layout\" itemscope itemtype=\"@listType\">", partial);
        Assert.Contains("var listType = isMenu ? \"https://schema.org/Menu\" : \"https://schema.org/OfferCatalog\";", partial);
        Assert.Contains("var amount = IPRO.Entities.PriceListText.SingleAmount(item.Price);", partial);
        Assert.Contains("<div class=\"pl-unavailable\">Not available right now</div>", partial);
        Assert.Contains(".pl-compact .pl-items { grid-template-columns: repeat(2, minmax(0, 1fr));", partial);
        Assert.Contains(".pl-cards .pl-items { grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));", partial);

        var guide = Read(@"DOCS\04_WEBSITE_BUILDER.md");
        Assert.Contains("**Price List / Menu** shows what you sell and what it costs", guide);
        Assert.Contains("- **Price List / Menu**: **List**", guide);
    }

    // ---- harness -----------------------------------------------------------------------------------

    private static async Task Save(IPRODbContext db, int agentId, int blockId, string layoutVariant, string kind, string json,
        string buttonText = "", string buttonUrl = "")
    {
        var result = await NewController(db, agentId).UpdateBlock(blockId, "Bread & Pastries", "Menu", "", "", buttonText, buttonUrl, true,
            layoutVariant: layoutVariant, priceListKind: kind, priceListJson: json);
        Assert.IsType<RedirectToActionResult>(result);
        db.ChangeTracker.Clear();
    }

    private static async Task<(int AgentId, int PageId)> SeedSiteAsync(IPRODbContext db)
    {
        var agent = new AgentUser
        {
            UserName = $"pl-{Guid.NewGuid():N}"[..20], Email = $"pl-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Menu", LastName = "Owner", DomainName = $"pl-{Guid.NewGuid():N}"[..24]
        };
        db.Add(agent);
        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "T", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true };
        db.Add(website);
        await db.SaveChangesAsync();
        var page = new WebsitePage { AgentWebsiteId = website.Id, Title = "Menu", Slug = "menu", IsPublished = true, SortOrder = 0 };
        db.Add(page);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (agent.Id, page.Id);
    }

    private static WebsitePagesController NewController(IPRODbContext db, int agentId)
    {
        var controller = new WebsitePagesController(db, new PackageEntitlementService(new UnitOfWork(db), db), new NullBlob());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
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

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
