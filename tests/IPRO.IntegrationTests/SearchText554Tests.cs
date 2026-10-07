using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IPRO.IntegrationTests;

// 554 (2026-10-07). Checking the first customer who is not an adviser -- a bakery -- after its domain
// moved, three things turned out to be true of EVERY customer site:
//
//   1. Its home page's search result was titled "Home" and described as "Home - professional service
//      and support."; five of seven pages said "Professional service and client support.". Starter
//      pages are born with both search fields filled, and a filled field is used as it is.
//   2. Every page but the home page answered HEAD with 404, so a link checker or an uptime monitor
//      reported the site broken while browsers were fine.
//   3. On a phone, a menu item with a tag and three prices broke its name mid-word ("America / no").
public class SearchText554Tests
{
    private static AgentWebsite Bakery(string tagLine = PublicSeoText.StarterTagLine) => new()
    {
        SiteTitle = "L'Avenue Boulangerie Inc.",
        TagLine = tagLine,
        AgentUser = new AgentUser { FirstName = "Lena", LastName = "Baker", City = "North York", Province = "Ontario" },
    };

    private static WebsitePage Page(string title, string slug, bool home = false, params WebsiteContentBlock[] blocks) => new()
    {
        Title = title, Slug = slug, IsHomePage = home,
        // As every starter page is born.
        MetaTitle = title, MetaDescription = $"{title} - professional service and support.",
        Blocks = new List<WebsiteContentBlock>(blocks),
    };

    private static WebsiteContentBlock Block(string type, string heading, string subheading, string body, int order = 0) =>
        new() { BlockType = type, Heading = heading, Subheading = subheading, Body = body, SortOrder = order };

    [Fact]
    public void A_starter_value_nobody_touched_is_not_what_a_search_result_shows()
    {
        var site = Bakery();
        var name = PublicSeoText.SiteName(site);
        var home = Page("Home", "home", home: true,
            Block(WebsiteBlockTypes.Hero, "L'Avenue Boulangerie", "Simplicity, Quality &amp; Excellence", "<p>Your Local P&acirc;tisserie in the GTA.</p>"));

        // The bakery's home page: its name, and its own opening words.
        Assert.Equal("L'Avenue Boulangerie Inc.", PublicSeoText.Title(home, name));
        Assert.Equal("Simplicity, Quality & Excellence. Your Local Pâtisserie in the GTA.", PublicSeoText.Description(home, site));

        // An inner page: "<Page> | <Business>".
        var gallery = Page("Gallery", "gallery");
        Assert.Equal("Gallery | L'Avenue Boulangerie Inc.", PublicSeoText.Title(gallery, name));
        // No words of its own and only the starter tagline: plain facts, never "professional service".
        Assert.Equal("Gallery - L'Avenue Boulangerie Inc., North York, Ontario", PublicSeoText.Description(gallery, site));
        Assert.Equal("L'Avenue Boulangerie Inc., North York, Ontario", PublicSeoText.Description(Page("Home", "home", home: true), site));

        Assert.False(PublicSeoText.HasOwnTitle(home));
        Assert.False(PublicSeoText.HasOwnDescription(home));
        Assert.True(PublicSeoText.IsStarterSentence("Request a meeting - professional service and support."));
        Assert.True(PublicSeoText.IsStarterSentence(PublicSeoText.StarterTagLine));
    }

    [Fact]
    public void What_the_owner_typed_is_always_used()
    {
        var site = Bakery("Fresh bread on Avenue Road since 2019");
        var page = Page("Bread & Pastries", "bread-pastries");
        page.MetaTitle = "Bread & Pastries menu";
        page.MetaDescription = "Baguettes, sourdough and croissants, baked every morning.";

        Assert.Equal("Bread & Pastries menu", PublicSeoText.Title(page, PublicSeoText.SiteName(site)));
        Assert.Equal("Baguettes, sourdough and croissants, baked every morning.", PublicSeoText.Description(page, site));
        Assert.True(PublicSeoText.HasOwnTitle(page));
        Assert.True(PublicSeoText.HasOwnDescription(page));

        // A tagline the owner wrote stands in for a page with no words of its own.
        Assert.Equal("Fresh bread on Avenue Road since 2019", PublicSeoText.Description(Page("Contact", "contact"), site));

        // A site with no title of its own goes by the person's name; a missing page says so.
        var unnamed = new AgentWebsite { AgentUser = new AgentUser { FirstName = "Lena", LastName = "Baker" } };
        Assert.Equal("Lena Baker", PublicSeoText.SiteName(unnamed));
        Assert.Equal("Page not found | Lena Baker", PublicSeoText.Title(null, "Lena Baker", pageNotFound: true));
        Assert.Equal("That page could not be found on Lena Baker.", PublicSeoText.Description(null, unnamed, pageNotFound: true));
    }

    [Fact]
    public void A_pages_opening_words_are_read_the_way_a_visitor_reads_them()
    {
        // The first block's lines; a line the next block repeats is said once; hidden blocks and
        // blocks that hold no sentences (a map's address, a video's link) are not read.
        var menu = Page("Menu", "menu", false,
            Block(WebsiteBlockTypes.Maps, "Find us", "", "1850 Avenue Road", 0),
            Block(WebsiteBlockTypes.PriceList, "Bread", "Our menu", "", 1),
            Block(WebsiteBlockTypes.PriceList, "Drinks", "Our menu", "Seasonal pastries made fresh every day, and coffee from our house espresso.", 2),
            Block(WebsiteBlockTypes.Text, "More", "", "This third block is not needed.", 3));
        Assert.Equal("Our menu. Seasonal pastries made fresh every day, and coffee from our house espresso.", PublicSeoText.OpeningWords(menu));

        var hidden = Page("About", "about", false, Block(WebsiteBlockTypes.Text, "", "", "Hidden words."));
        hidden.Blocks.First().IsVisible = false;
        Assert.Equal(string.Empty, PublicSeoText.OpeningWords(hidden));

        // A page whose heading is its only sentence.
        var drinks = Page("Drinks and Meals", "drinks-and-meals", false,
            Block(WebsiteBlockTypes.Hero, "A selection of drinks using our house espresso", "", ""));
        Assert.Equal("A selection of drinks using our house espresso", PublicSeoText.OpeningWords(drinks));

        // Cut at a word, at about what a search result shows.
        var longText = string.Join(" ", System.Linq.Enumerable.Repeat("croissant", 40));
        var cut = PublicSeoText.Shorten(longText);
        Assert.True(cut.Length <= PublicSeoText.DescriptionLength);
        Assert.EndsWith("croissant", cut);
    }

    [Fact]
    public void The_page_and_its_editor_read_the_one_rule()
    {
        var head = Read(@"src\IPRO.Web\Views\PublicWebsite\_PublicSeoHead.cshtml");
        Assert.Contains("PublicSeoText.Title(page, siteName, isPageNotFound)", head);
        Assert.Contains("PublicSeoText.Description(page, website, isPageNotFound)", head);
        Assert.DoesNotContain("professional services available", head);

        // The editor shows what the owner wrote; the grey text is what a search result shows today.
        var editor = Read(@"src\IPRO.Web\Views\WebsitePages\Edit.cshtml");
        Assert.Contains("name=\"metaTitle\" value=\"@ownSearchTitle\" placeholder=\"@Model.SearchTitle\"", editor);
        Assert.Contains("name=\"metaDescription\" value=\"@ownSearchDescription\" placeholder=\"@Model.SearchDescription\"", editor);
        Assert.Contains("PublicSeoText.Title(page,", Read(@"src\IPRO.Web\Controllers\WebsitePagesController.cs"));
        Assert.Contains("## Configure Page SEO and Social Sharing", Read(@"DOCS\04_WEBSITE_BUILDER.md"));
        Assert.Contains("Left empty, a search result shows", Read(@"DOCS\04_WEBSITE_BUILDER.md"));
    }

    // ----------------------------------------------------------------------------- HEAD --

    [Fact]
    public async Task A_customer_pages_HEAD_is_answered_like_its_GET_and_is_not_a_visit()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Head;
        context.Request.Path = "/gallery";
        string? seenMethod = null; var counted = true;

        await HeadRequests.AnswerAsync(context, () =>
        {
            seenMethod = context.Request.Method;
            counted = !HeadRequests.IsBeingAnswered(context);
            return Task.CompletedTask;
        }, candidate => candidate.Request.Path == "/gallery");

        Assert.Equal(HttpMethods.Get, seenMethod);                  // an action that answers GET matches
        Assert.False(counted);                                      // the page knows nobody is looking
        Assert.Equal(HttpMethods.Head, context.Request.Method);     // logged as what it was

        // Anything the pipeline does not vouch for stays a HEAD: a tracked link, a poll vote, sign-out.
        var other = new DefaultHttpContext();
        other.Request.Method = HttpMethods.Head;
        other.Request.Path = "/t/c/abc";
        await HeadRequests.AnswerAsync(other, () => { seenMethod = other.Request.Method; return Task.CompletedTask; }, candidate => false);
        Assert.Equal(HttpMethods.Head, seenMethod);
        Assert.False(HeadRequests.IsBeingAnswered(other));

        // A GET is never touched.
        var get = new DefaultHttpContext();
        get.Request.Path = "/gallery";
        await HeadRequests.AnswerAsync(get, () => Task.CompletedTask, candidate => true);
        Assert.False(HeadRequests.IsBeingAnswered(get));
    }

    [Fact]
    public void The_pipeline_vouches_with_the_same_test_that_hands_a_GET_to_the_public_site()
    {
        var program = Read(@"src\IPRO.Web\Program.cs");
        Assert.Contains("context, next, candidate => IsPublicWebsiteAddress(candidate, app.Configuration)));", program);
        Assert.Contains("HttpMethods.IsGet(context.Request.Method) && IsPublicWebsiteAddress(context, configuration);", program);
        // The address test itself asks nothing about the method.
        var address = program.Substring(program.IndexOf("static bool IsPublicWebsiteAddress", StringComparison.Ordinal));
        address = address.Substring(0, address.IndexOf("static string EnsureMySqlMigrationOptions", StringComparison.Ordinal));
        Assert.DoesNotContain("HttpMethods.", address);
        Assert.Contains("IsNeverShadowedPrefix(firstSegment)", address);
        Assert.Contains("StartsWithSegments(\"/portal\"", address);

        Assert.Contains("if (IPRO.Web.Infrastructure.HeadRequests.IsBeingAnswered(HttpContext)) return;",
            Read(@"src\IPRO.Web\Controllers\PublicWebsiteController.cs"));
    }

    // ------------------------------------------------------------------- the menu's row --

    [Fact]
    public void A_menu_row_wraps_and_a_name_keeps_its_words_whole()
    {
        var view = Read(@"src\IPRO.Web\Views\PublicWebsite\_PriceList.cshtml");
        Assert.Contains(".pl-item-line { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 8px; }", view);
        Assert.Contains(".pl-item-name { font-weight: 700; overflow-wrap: break-word; max-width: 100%; }", view);
        Assert.Contains(".pl-price { flex: none; max-width: 100%; margin-left: auto; text-align: right; font-weight: 800; }", view);
        // The rule that let a name shrink to a letter, and the half-row cap that squeezed it.
        Assert.DoesNotContain("overflow-wrap: anywhere", view);
        Assert.DoesNotContain(".pl-price { max-width: 50%; }", view);
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
