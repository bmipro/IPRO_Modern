using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.IntegrationTests.Accessibility;
using Xunit;

namespace IPRO.IntegrationTests;

// 667 (2026-11-19). The owner, trying 661 on staging: iPro Advisers Inc. has one Google review, yet the Reviews
// blocks he added to his About and Testimonials pages said "5.0 (0 reviews on Google)", and the Google review
// link he had saved in My Profile was not used: he had to paste it into the block again. The block's rating and
// count are typed by hand and start at 5.0 and 0. Now: no stars and no rating line until a count is typed; an
// empty "Review us" link takes the Profile's link (under 661's rule: link saved and switch on); and the portal
// and the guides say step by step where Google keeps that link (666).
public class ReviewBadge667Tests
{
    private const string ProfileLink = "https://g.page/r/abc667/review";

    [Fact]
    public void The_rating_shows_only_once_a_count_is_typed_and_is_never_stored()
    {
        Assert.False(new WebsiteReviewSettings().ShowsRating);                       // a new block: 5.0 and 0
        Assert.False(new WebsiteReviewSettings { Rating = 4.9m, ReviewCount = 0 }.ShowsRating);
        Assert.True(new WebsiteReviewSettings { Rating = 5m, ReviewCount = 1 }.ShowsRating);
        Assert.DoesNotContain("ShowsRating", new WebsiteReviewSettings { ReviewCount = 3 }.ToJson());
        Assert.Equal(3, WebsiteReviewSettings.FromJson(new WebsiteReviewSettings { ReviewCount = 3 }.ToJson()).ReviewCount);
    }

    [Fact]
    public void An_empty_review_us_link_takes_the_profile_link_and_only_for_google()
    {
        Assert.Equal(ProfileLink, new WebsiteReviewSettings().WithProfileLink(ProfileLink).WriteReviewUrl);
        Assert.Equal(ProfileLink, new WebsiteReviewSettings().WithProfileLink("  " + ProfileLink + " ").WriteReviewUrl);
        // The block's own link wins.
        Assert.Equal("https://g.page/r/own/review", new WebsiteReviewSettings { WriteReviewUrl = "https://g.page/r/own/review" }.WithProfileLink(ProfileLink).WriteReviewUrl);
        // A Facebook block is not given a Google link; nothing, or something that is not a web address, adds nothing.
        Assert.Equal(string.Empty, new WebsiteReviewSettings { Platform = ReviewPlatforms.Facebook }.WithProfileLink(ProfileLink).WriteReviewUrl);
        Assert.Equal(string.Empty, new WebsiteReviewSettings().WithProfileLink(null).WriteReviewUrl);
        Assert.Equal(string.Empty, new WebsiteReviewSettings().WithProfileLink("javascript:alert(1)").WriteReviewUrl);
    }

    [Fact]
    public async Task The_website_uses_the_profile_link_only_when_it_is_saved_and_the_switch_is_on()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var accountant = await SeedAgentAsync(db, "Accountants");
        var adviser = await SeedAgentAsync(db, "Insurance / Financial");

        Assert.Null(await GoogleReviewPreference.WebsiteLinkAsync(db, accountant, "Accountants"));            // nothing saved yet
        await GoogleReviewPreference.SetAsync(db, accountant, ProfileLink, askEnabled: true);
        Assert.Equal(ProfileLink, await GoogleReviewPreference.WebsiteLinkAsync(db, accountant, "Accountants"));
        await GoogleReviewPreference.SetAsync(db, accountant, ProfileLink, askEnabled: false);
        Assert.Null(await GoogleReviewPreference.WebsiteLinkAsync(db, accountant, "Accountants"));            // switched off

        // A financial adviser who saved the link but never turned the switch on gets no review request on the site.
        await GoogleReviewPreference.SetAsync(db, adviser, ProfileLink, askEnabled: false);
        Assert.Null(await GoogleReviewPreference.WebsiteLinkAsync(db, adviser, "Insurance / Financial"));
    }

    public static TheoryData<string, string> DesignsAndLayouts()
    {
        var data = new TheoryData<string, string>();
        foreach (var design in PublicSitePages.Designs)
        {
            data.Add(design, "");
            data.Add(design, "banner");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(DesignsAndLayouts))]
    public async Task A_block_with_no_count_shows_no_stars_and_offers_the_profile_link(string design, string layout)
    {
        var site = PublicSitePages.Build(design);
        var block = site.Pages.Single(p => p.Slug == "home").Blocks.Single(b => b.BlockType == WebsiteBlockTypes.Reviews);
        block.LayoutVariant = layout;
        block.SettingsJson = new WebsiteReviewSettings().ToJson();                           // as a new block starts: 5.0 and 0
        var model = site.Model("home");
        model.GoogleReviewLink = ProfileLink;

        var html = await PublicSitePages.RenderAsync(model);
        Assert.DoesNotContain("0 reviews on Google", html);
        Assert.DoesNotContain("<i class=\"fas fa-star\"></i>", html);
        Assert.Contains("href=\"" + ProfileLink + "\"", html);
        Assert.Contains("Review us on Google", html);

        // With a count typed, the rating is back; without a Profile link, no "Review us" link is invented.
        block.SettingsJson = new WebsiteReviewSettings { Rating = 4.8m, ReviewCount = 37 }.ToJson();
        model = site.Model("home");
        html = await PublicSitePages.RenderAsync(model);
        Assert.Contains("4.8", html);
        Assert.Contains("37 reviews on Google", html);
        Assert.Contains("<i class=\"fas fa-star\"></i>", html);
        Assert.DoesNotContain("Review us on Google", html);
    }

    [Fact]
    public void The_controllers_pass_the_link_and_the_portal_and_guides_say_where_google_keeps_it()
    {
        foreach (var (design, name) in new[] { ("_ModernManagedPage", "mp"), ("_ClassicManagedPage", "cp"), ("_EditorialManagedPage", "ep") })
        {
            var view = Read($@"src\IPRO.Web\Views\PublicWebsite\{design}.cshtml");
            Assert.Contains($"var {name}Review = IPRO.Entities.WebsiteReviewSettings.FromJson(block.SettingsJson).WithProfileLink(Model.GoogleReviewLink);", view);
            Assert.Equal(2, Count(view, $"@if ({name}Review.ShowsRating)"));
        }
        Assert.Contains("GoogleReviewLink = googleReviewLink", Read(@"src\IPRO.Web\Controllers\PublicWebsiteController.cs"));
        var pages = Read(@"src\IPRO.Web\Controllers\WebsitePagesController.cs");
        Assert.Contains("GoogleReviewLink = page.Blocks.Any(b => b.IsVisible && b.BlockType == WebsiteBlockTypes.Reviews)", pages);
        Assert.Contains("ViewBag.ProfileGoogleReviewLink = await IPRO.DataAccess.GoogleReviewPreference.WebsiteLinkAsync(", pages);

        var steps = Read(@"src\IPRO.Web\Views\Shared\_GoogleReviewLinkSteps.cshtml");
        Assert.Contains("<summary>How do I find my Google review link?</summary>", steps);
        Assert.Contains("Click <strong>Ask for reviews</strong>. On some screens it is <strong>Read reviews</strong>, then <strong>Get more reviews</strong>.", steps);
        Assert.Equal(1, Count(Read(@"src\IPRO.Web\Views\Account\Profile.cshtml"), "@await Html.PartialAsync(\"_GoogleReviewLinkSteps\")"));
        var editor = Read(@"src\IPRO.Web\Views\WebsitePages\Edit.cshtml");
        Assert.Equal(1, Count(editor, "@await Html.PartialAsync(\"_GoogleReviewLinkSteps\")"));
        Assert.Contains("While the count is 0, your site shows no stars, only the links.", editor);

        foreach (var guide in new[] { @"DOCS\15_TESTIMONIALS.md", @"DOCS\04_WEBSITE_BUILDER.md" })
            Assert.Contains("**Where to find your Google review link.** On a computer, sign in to the Google account that manages your business", Read(guide));
    }

    // -------------------------------------------------------------------------- helpers --

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string businessType)
    {
        var agent = new AgentUser
        {
            UserName = $"rb-{Guid.NewGuid():N}"[..20], Email = $"rb-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Review", LastName = "Badge", DomainName = $"rb-{Guid.NewGuid():N}"[..24], BusinessType = businessType
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
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
