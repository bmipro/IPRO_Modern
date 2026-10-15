using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using IPRO.Admin.Models;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPRO.IntegrationTests.SiteHandoverReturn572Tests;

namespace IPRO.IntegrationTests;

// 577 (2026-10-14): "Make the package" refused the owner's own site. Four starter articles under
// Resources are named in the menu by their whole title ("Questions Worth Asking Before You Choose an
// Advisor", 51 characters), and the contract's menu label is at most 40. iPro made those labels itself
// (WebsiteStarterResourcesHelper), so most customers' sites would have been refused the same way.
//
// Now such a label goes to the builder shortened at a word, and the hand-over keeps the page's own.
// On the way back, a label that comes back exactly as it went is the page's own again, as the site has
// it THEN (the principle of 576's locked boxes: what the designer could not have meant to change is not
// changed); a label the designer changed is written. The review says which.
public class MenuLabels577Tests
{
    private static readonly string Ellipsis = ((char)0x2026).ToString();

    private const string FirstMeeting = "How to Get the Most Out of Your First Meeting";
    private const string LifeInsurance = "Do You Actually Have Enough Life Insurance?";
    private const string Questions = "Questions Worth Asking Before You Choose an Advisor";
    private const string RrspOrTfsa = "RRSP or TFSA: Which Should You Prioritize?";
    private const string SelfEmployed = "Planning for Retirement When You Are Self-Employed";
    private const string Pension = "What Happens to Your Pension When You Change Jobs?";

    [Fact]
    public void A_long_menu_label_is_shortened_at_a_word_and_says_so()
    {
        // The owner's four, as the builder now shows them.
        Assert.Equal("How to Get the Most Out of Your First" + Ellipsis, SiteHandoverBuilder.BuilderLabel(FirstMeeting));
        Assert.Equal("Do You Actually Have Enough Life" + Ellipsis, SiteHandoverBuilder.BuilderLabel(LifeInsurance));
        Assert.Equal("Questions Worth Asking Before You" + Ellipsis, SiteHandoverBuilder.BuilderLabel(Questions));
        Assert.Equal("RRSP or TFSA: Which Should You" + Ellipsis, SiteHandoverBuilder.BuilderLabel(RrspOrTfsa));

        // A label that fits goes exactly as it is, spaces and all.
        Assert.Equal("About", SiteHandoverBuilder.BuilderLabel("About"));
        Assert.Equal(" Forty characters exactly, no more, no. ", SiteHandoverBuilder.BuilderLabel(" Forty characters exactly, no more, no. "));
        // One that is long only for its spaces is written on one line and needs no cut.
        Assert.Equal("Tax planning for small business owners", SiteHandoverBuilder.BuilderLabel("Tax  planning   for small  business\nowners  "));
        // A word that ends in a comma or a colon at the cut loses it; nothing is cut inside a word.
        Assert.Equal("Wills, trusts, estates, powers, gifts" + Ellipsis, SiteHandoverBuilder.BuilderLabel("Wills, trusts, estates, powers, gifts, and more besides"));
        // Where whole words would keep less than half of the room (one very long word), or nothing but
        // marks, the cut is inside, and never through half of a character.
        Assert.Equal(new string('a', 39) + Ellipsis, SiteHandoverBuilder.BuilderLabel(new string('a', 60)));
        Assert.Equal("Tax Superannuationandretirementstrategi" + Ellipsis, SiteHandoverBuilder.BuilderLabel("Tax Superannuationandretirementstrategies explained"));
        var emoji = char.ConvertFromUtf32(0x1F600);
        Assert.Equal(new string('b', 38) + Ellipsis, SiteHandoverBuilder.BuilderLabel(new string('b', 38) + emoji + new string('c', 10)));
        var marks = new string('-', 21) + " " + string.Concat(Enumerable.Repeat(emoji, 20));
        Assert.Equal(marks[..38] + Ellipsis, SiteHandoverBuilder.BuilderLabel(marks));

        foreach (var label in new[] { FirstMeeting, LifeInsurance, Questions, RrspOrTfsa, SelfEmployed, new string('x', 100) })
        {
            Assert.InRange(SiteHandoverBuilder.BuilderLabel(label).Length, 1, BuilderContract.MaxNavigationLabel);
        }
    }

    [Fact]
    public async Task A_site_with_long_menu_labels_is_handed_over_and_the_hand_over_keeps_their_own()
    {
        var fixture = SiteHandover572Tests.FixtureSite.Load();
        var about = fixture.Pages.Single(p => p.Slug == "about");
        about.NavigationLabel = Questions;
        // Long only for its spaces: it goes on one line, which is tidying, not shortening. And a short
        // label with a double space goes exactly as it is, and is not shortened either.
        fixture.Pages.Single(p => p.Slug == "contact").NavigationLabel = "Tax  planning   for small  business\nowners  ";
        fixture.Pages.Single(p => p.IsHomePage).NavigationLabel = "Our  Home";

        var handover = await SiteHandoverBuilder.BuildAsync(fixture.Input(), fixture.Fetch);

        Assert.True(handover.CanHandOver, string.Join("\n", handover.Blockers));
        var sent = JsonNode.Parse(handover.TemplateJson)!["pages"]!.AsArray().ToDictionary(p => p!["slug"]!.GetValue<string>(), p => p!["navigationLabel"]!.GetValue<string>());
        Assert.Equal("Questions Worth Asking Before You" + Ellipsis, sent["about"]);
        Assert.Equal("Tax planning for small business owners", sent["contact"]);
        Assert.Equal(new[]
        {
            $"One menu label is longer than the 40 characters the builder takes, so the builder shows it shortened: \"{Questions}\" as \"Questions Worth Asking Before You{Ellipsis}\". " +
            "Left unchanged in the builder, a label comes back as the page's own; a label changed in the builder is used."
        }, handover.Notes);

        // The hand-over's record of that page holds both; every other page's reads as it did before.
        var record = handover.Pages.Single(p => p.Slug == "about");
        Assert.Equal((Questions, "Questions Worth Asking Before You" + Ellipsis), (record.Label, record.SentLabel));
        var json = JsonSerializer.Serialize(handover.Pages);
        var others = JsonNode.Parse(json)!.AsArray().Where(p => p!["Slug"]!.GetValue<string>() != "about").ToList();
        Assert.NotEmpty(others);
        Assert.All(others, p => Assert.False(p!.AsObject().ContainsKey("Label") || p.AsObject().ContainsKey("SentLabel")));
        Assert.Equal(record, JsonSerializer.Deserialize<List<HandoverPage>>(json)!.Single(p => p.Slug == "about"));
    }

    [Fact]
    public async Task A_title_too_long_or_missing_is_said_with_its_length_and_where_to_change_it()
    {
        var fixture = SiteHandover572Tests.FixtureSite.Load();
        var title = "A guide to " + new string('w', 125);
        fixture.Pages.Single(p => p.Slug == "about").Title = title;
        fixture.Pages.Single(p => p.Slug == "contact").Title = "   ";

        var handover = await SiteHandoverBuilder.BuildAsync(fixture.Input(), fixture.Fetch);

        Assert.False(handover.CanHandOver);
        Assert.Contains($"\"{title[..60]}...\" has a title of 136 characters, and the builder takes at most 120. Shorten it in the page editor (its Page Title field).", handover.Blockers);
        Assert.Contains("The page at /contact has no title. Give it one in the page editor (its Page Title field).", handover.Blockers);
    }

    [Fact]
    public void A_package_whose_label_or_title_is_refused_says_how_long_it_is()
    {
        string Refusal(Action<JsonObject> change)
        {
            var (template, files) = BuilderImport571Tests.Designer();
            change(Page(template, "about"));
            var package = BuilderPackageReader.Read(BuilderImport571Tests.Zip(template, files), BuilderImportContext.DesignerTemplate());
            return package.Problems.Single(p => p.Rule == "3").Message;
        }

        Assert.Equal("The menu label (navigationLabel) is 1 to 40 characters (and not only spaces); this one is 41 characters long.",
            Refusal(p => p["navigationLabel"] = new string('a', 41)));
        Assert.Equal("The menu label (navigationLabel) is 1 to 40 characters (and not only spaces); this one is only spaces.",
            Refusal(p => p["navigationLabel"] = "   "));
        Assert.Equal("The menu label (navigationLabel) is 1 to 40 characters (and not only spaces); this one is empty.",
            Refusal(p => p["navigationLabel"] = ""));
        Assert.Equal("The title is 1 to 120 characters (and not only spaces); this one is 121 characters long.",
            Refusal(p => p["title"] = new string('t', 121)));
    }

    [Fact]
    public async Task The_owners_starter_articles_go_out_and_come_back_with_the_labels_they_should_have()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var blob = new BuilderImport571Tests.MemoryBlob();
        var audit = new RecordingAudit();
        var seeded = await SeedFittingSiteAsync(db, blob);
        var site = seeded.Site;
        var limit = AgentStorage.LimitBytes(50);

        // Resources and its starter articles, as WebsiteStarterResourcesHelper makes them: each page
        // named in the menu by the article's whole title.
        var resources = new WebsitePage
        {
            AgentWebsiteId = site.Id, Title = "Resources", Slug = "resources", NavigationLabel = "Resources", SortOrder = 3,
            Blocks = { new WebsiteContentBlock { BlockType = WebsiteBlockTypes.Text, Heading = "Resources", Body = "Helpful articles.", SortOrder = 0 } }
        };
        db.Add(resources);
        await db.SaveChangesAsync();
        var slugs = new Dictionary<string, string>
        {
            [FirstMeeting] = "how-to-get-the-most-out-of-your-first-meeting",
            [LifeInsurance] = "do-you-actually-have-enough-life-insurance",
            [Questions] = "questions-worth-asking-before-you-choose-an-advisor",
            [RrspOrTfsa] = "rrsp-or-tfsa-which-should-you-prioritize",
            [SelfEmployed] = "planning-for-retirement-when-you-are-self-employed",
            [Pension] = "what-happens-to-your-pension-when-you-change-jobs"
        };
        var order = 0;
        foreach (var (title, slug) in slugs)
        {
            db.Add(new WebsitePage
            {
                AgentWebsiteId = site.Id, ParentPageId = resources.Id, Title = title, Slug = slug, NavigationLabel = title, MetaTitle = title, SortOrder = order++,
                Blocks = { new WebsiteContentBlock { BlockType = WebsiteBlockTypes.ArticleContent, SettingsJson = new WebsiteArticleContentSettings { ArticleId = seeded.ArticleId }.ToJson(), SortOrder = 0 } }
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // ---- out: the site is no longer refused ----
        var handover = await SiteHandoverService.BuildAsync(db, blob, site, SiteHandoverService.AllowedBlockTypes(true, true, true), limit);
        Assert.True(handover.CanHandOver, string.Join("\n", handover.Blockers));
        var labelsOut = JsonNode.Parse(handover.TemplateJson)!["pages"]!.AsArray()
            .ToDictionary(p => p!["slug"]!.GetValue<string>(), p => p!["navigationLabel"]!.GetValue<string>());
        Assert.All(labelsOut.Values, label => Assert.InRange(label.Length, 1, BuilderContract.MaxNavigationLabel));
        Assert.Equal("Questions Worth Asking Before You" + Ellipsis, labelsOut[slugs[Questions]]);
        Assert.Equal("Resources", labelsOut["resources"]);
        Assert.Contains(handover.Notes, n => n.StartsWith("6 menu labels are longer than the 40 characters the builder takes, so the builder shows them shortened: "));
        var record = await SiteHandoverService.RecordAsync(db, site, handover, "owner");
        Assert.Equal(6, SiteHandoverService.List<HandoverPage>(record.PagesJson).Count(p => p.SentLabel != null));
        db.ChangeTracker.Clear();

        // ---- meanwhile, in iPro ----
        var rows = await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id).Include(p => p.Blocks).ToDictionaryAsync(p => p.Slug);
        rows[slugs[LifeInsurance]].NavigationLabel = "Enough life insurance?";      // and the designer changes it too
        rows[slugs[Questions]].NavigationLabel = "Choosing an adviser";            // the designer leaves it
        rows[slugs[Pension]].NavigationLabel = "Pensions and job changes";         // the designer leaves it,
        rows[slugs[Pension]].Blocks.Single().IsVisible = false;                     // and its section is hidden too
        rows[slugs[RrspOrTfsa]].NavigationLabel = "RRSP or TFSA?";                 // the designer moves the page
        db.RemoveRange(rows[slugs[SelfEmployed]].Blocks);                          // removed in iPro, comes back from the builder
        db.Remove(rows[slugs[SelfEmployed]]);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // ---- what the builder sends back ----
        var returned = Return(handover.Zip, template =>
        {
            Page(template, slugs[LifeInsurance])["navigationLabel"] = "Life insurance check";
            Page(template, slugs[RrspOrTfsa])["slug"] = "rrsp-or-tfsa";            // a shorter address; its label as it was given
        });
        var package = BuilderPackageReader.Read(returned, await BuilderImportService.ContextAsync(db, site, limit, BuilderPackageReader.PeekHandoverId(returned)));
        Assert.True(package.IsValid, string.Join("\n", package.Problems.Select(p => p.Message)));
        var import = await BuilderImportService.StoreAsync(db, blob, site, package, "return.zip", "owner");
        db.ChangeTracker.Clear();

        // ---- the plan ----
        var plan = (await BuilderImportService.PlanAsync(db, import))!.Plan!;
        Assert.Empty(plan.Problems);
        PlannedPage At(string slug) => plan.Pages.Single(p => p.Page.Slug == slug && p.Change != PageChange.Removed);
        string Short(string title) => SiteHandoverBuilder.BuilderLabel(title);

        // Left as it went: the page's own label.
        var first = At(slugs[FirstMeeting]);
        Assert.Equal((PageChange.Replaced, FirstMeeting, Short(FirstMeeting), true, false),
            (first.Change, first.Page.NavigationLabel, first.LabelSent, first.LabelKept, first.ChangedSinceHandover));
        // Changed in iPro AND in the builder: the builder's is written, and the review says the
        // customer's change is replaced.
        var life = At(slugs[LifeInsurance]);
        Assert.Equal(("Life insurance check", Short(LifeInsurance), false, true), (life.Page.NavigationLabel, life.LabelSent, life.LabelKept, life.ChangedSinceHandover));
        // Changed in iPro only: the customer's label stays, and nothing of the page is said to be replaced.
        var questions = At(slugs[Questions]);
        Assert.Equal(("Choosing an adviser", true, false), (questions.Page.NavigationLabel, questions.LabelKept, questions.ChangedSinceHandover));
        // Its label and more changed in iPro: the customer's label stays, and the page IS said to be
        // changed, for the rest of what the customer changed is replaced.
        var pension = At(slugs[Pension]);
        Assert.Equal(("Pensions and job changes", true, true), (pension.Page.NavigationLabel, pension.LabelKept, pension.ChangedSinceHandover));
        // Given a new address in the builder: still that page's label (the customer's, as the site has
        // it), on the new address; and the page left behind is not said to lose the customer's work.
        var rrsp = At("rrsp-or-tfsa");
        Assert.Equal((PageChange.Added, "RRSP or TFSA?", true), (rrsp.Change, rrsp.Page.NavigationLabel, rrsp.LabelKept));
        var left = plan.Pages.Single(p => p.Page.Slug == slugs[RrspOrTfsa]);
        Assert.Equal((PageChange.Removed, true, false), (left.Change, left.RemovedInBuilder, left.ChangedSinceHandover));
        // Removed in iPro meanwhile and brought back: its label as it was.
        var selfEmployed = At(slugs[SelfEmployed]);
        Assert.Equal((PageChange.Added, SelfEmployed, true), (selfEmployed.Change, selfEmployed.Page.NavigationLabel, selfEmployed.LabelKept));
        // A label that was never shortened is the package's, as before.
        Assert.Equal(("Resources", "", false), (At("resources").Page.NavigationLabel, At("resources").LabelSent, At("resources").LabelKept));

        // ---- the review says it ----
        var review = (BuilderImportReviewViewModel)((ViewResult)await Controller(db, blob, audit).Review(import.Id, null, null, null, null)).Model!;
        db.ChangeTracker.Clear();
        var html = WebUtility.HtmlDecode(await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", review));
        Assert.Contains($"Menu label: the builder showed it shortened, as \"{Short(FirstMeeting)}\". It came back unchanged, so the page keeps its own: \"{FirstMeeting}\".", html);
        Assert.Contains($"Menu label: the builder showed it shortened, as \"{Short(LifeInsurance)}\", and the designer changed it to \"Life insurance check\". The menu shows that from now on.", html);
        Assert.Contains("is replaced by the package's version, apart from its menu label, which stays as the site has it.", html);
        Assert.DoesNotContain("Menu label: the builder showed it shortened, as \"Resources", html);

        // ---- in ----
        var result = await BuilderImportService.ApplyAsync(db, await db.WebsiteBuilderImports.SingleAsync(), new HashSet<int>(), null, "owner", reviewedPlan: plan.Fingerprint);
        Assert.True(result.Ok, string.Join(" ", result.Problems));
        db.ChangeTracker.Clear();
        var after = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == site.Id).ToDictionaryAsync(p => p.Slug, p => p.NavigationLabel);
        Assert.Equal(FirstMeeting, after[slugs[FirstMeeting]]);
        Assert.Equal("Life insurance check", after[slugs[LifeInsurance]]);
        Assert.Equal("Choosing an adviser", after[slugs[Questions]]);
        Assert.Equal("Pensions and job changes", after[slugs[Pension]]);
        Assert.Equal("RRSP or TFSA?", after["rrsp-or-tfsa"]);
        Assert.False(after.ContainsKey(slugs[RrspOrTfsa]));
        Assert.Equal(SelfEmployed, after[slugs[SelfEmployed]]);
        Assert.DoesNotContain(after.Values, label => label.EndsWith(Ellipsis, StringComparison.Ordinal));
    }

    [Fact]
    public void The_plan_finds_each_shortened_label_and_only_where_it_can_tell()
    {
        var shortQuestions = SiteHandoverBuilder.BuilderLabel(Questions);
        var site = new[] { SitePage(1, "home", "Home", home: true), SitePage(2, "questions", Questions), SitePage(3, "other", "Questions Worth Asking Before You Choose a Lawyer") };
        var labels = new[]
        {
            new ShortenedLabel("questions", 2, Questions, shortQuestions),
            new ShortenedLabel("other", 3, "Questions Worth Asking Before You Choose a Lawyer", shortQuestions)
        };

        // Each page at its own address: each keeps its own label.
        var plan = Plan(site, labels, PackagePage("home", "Home", true), PackagePage("questions", shortQuestions), PackagePage("other", shortQuestions));
        Assert.Equal(Questions, Single(plan, "questions").Page.NavigationLabel);
        Assert.Equal("Questions Worth Asking Before You Choose a Lawyer", Single(plan, "other").Page.NavigationLabel);

        // Both given new addresses, both still carrying one shortened label: nothing tells which is
        // which, so each comes back with the label it carries, and the review does not claim otherwise.
        plan = Plan(site, labels, PackagePage("home", "Home", true), PackagePage("q1", shortQuestions), PackagePage("q2", shortQuestions));
        Assert.All(new[] { Single(plan, "q1"), Single(plan, "q2") }, p => Assert.Equal((shortQuestions, "", false), (p.Page.NavigationLabel, p.LabelSent, p.LabelKept)));

        // A page that comes back at its own address is that page, whatever label it carries: it never
        // takes the label of a page that did not come back (the security review's case: Contact came
        // back carrying the shortened label of a removed page, and took that page's whole label).
        plan = Plan(site, labels, PackagePage("home", "Home", true), PackagePage("other", shortQuestions));
        Assert.Equal(("Questions Worth Asking Before You Choose a Lawyer", true), (Single(plan, "other").Page.NavigationLabel, Single(plan, "other").LabelKept));
        var withContact = new[] { SitePage(1, "home", "Home", home: true), SitePage(2, "questions", Questions), SitePage(4, "contact", "Contact") };
        plan = Plan(withContact, labels.Take(1).ToArray(), PackagePage("home", "Home", true), PackagePage("contact", shortQuestions));
        Assert.Equal((shortQuestions, "", false), (Single(plan, "contact").Page.NavigationLabel, Single(plan, "contact").LabelSent, Single(plan, "contact").LabelKept));

        // A page with no menu label of its own (the page editor never saves one) keeps what came back,
        // and the review is told which label the builder was given.
        var blank = new[] { SitePage(1, "home", "Home", home: true), SitePage(2, "questions", Questions, label: "") };
        plan = Plan(blank, new[] { new ShortenedLabel("questions", 2, "", shortQuestions) }, PackagePage("home", "Home", true), PackagePage("questions", shortQuestions));
        Assert.Equal((shortQuestions, shortQuestions, false), (Single(plan, "questions").Page.NavigationLabel, Single(plan, "questions").LabelSent, Single(plan, "questions").LabelKept));

        // With no shortened label on record (a label that went whole; any site built from scratch) the
        // package's label is written, as it always has been.
        plan = Plan(site, Array.Empty<ShortenedLabel>(), PackagePage("home", "Home", true), PackagePage("questions", "Questions"));
        Assert.Equal(("Questions", ""), (Single(plan, "questions").Page.NavigationLabel, Single(plan, "questions").LabelSent));
    }

    [Fact]
    public void A_page_made_the_home_page_in_the_builder_does_not_take_the_old_home_pages_label()
    {
        // The home page lands on the site's home row whatever either side calls it (571). The function
        // reviews' case: the old home page's label went shortened, and the designer made About the home
        // page; the builder gives the old home page another address (the door refuses a page at "home"
        // that is not the home page), and About may keep its own address or take "home". Either way
        // About keeps its own label, with no word about a shortened one, and the old home page, back as
        // a new page, keeps the home page's own label.
        const string Welcome = "Smith and Jones Financial Planning and Wealth Advice";
        var shortWelcome = SiteHandoverBuilder.BuilderLabel(Welcome);
        var site = new[] { SitePage(1, "home", Welcome, home: true), SitePage(2, "about", "About") };
        foreach (var aboutAt in new[] { "about", "home" })
        {
            var plan = Plan(site, new[] { new ShortenedLabel("home", 1, Welcome, shortWelcome) },
                PackagePage(aboutAt, "About", true), PackagePage("smith-and-jones", shortWelcome));

            var about = plan.Pages.Single(p => p.Page.IsHomePage);
            Assert.Equal((aboutAt, 1, "About", "", false), (about.Page.Slug, about.Page.Id, about.Page.NavigationLabel, about.LabelSent, about.LabelKept));
            var oldHome = Single(plan, "smith-and-jones");
            Assert.Equal((PageChange.Added, Welcome, true), (oldHome.Change, oldHome.Page.NavigationLabel, oldHome.LabelKept));
        }
        // When the designer also changed the old home page's label, nothing tells it apart: each page
        // gets the label it came back with, and About is not said to have had a shortened one.
        var changedToo = Plan(site, new[] { new ShortenedLabel("home", 1, Welcome, shortWelcome) },
            PackagePage("about", "About", true), PackagePage("smith-and-jones", "Smith and Jones"));
        Assert.Equal(("About", ""), (changedToo.Pages.Single(p => p.Page.IsHomePage).Page.NavigationLabel, changedToo.Pages.Single(p => p.Page.IsHomePage).LabelSent));
        Assert.Equal("Smith and Jones", Single(changedToo, "smith-and-jones").Page.NavigationLabel);

        // And the home page that only changed its address in the builder is still the home page, label and all.
        var renamed = Plan(site, new[] { new ShortenedLabel("home", 1, Welcome, shortWelcome) }, PackagePage("welcome", shortWelcome, true), PackagePage("about", "About"))
            .Pages.Single(p => p.Page.IsHomePage);
        Assert.Equal((1, Welcome, true), (renamed.Page.Id, renamed.Page.NavigationLabel, renamed.LabelKept));
    }

    [Fact]
    public void A_page_given_a_new_address_leaves_its_old_row_red_only_when_more_than_its_label_changed()
    {
        // The old row of a page moved in the builder is "removed in the builder". When the customer's one
        // change to it was the menu label, that label goes on with the page at its new address: nothing
        // is lost, so the row is not marked. When more changed, it is.
        var shortQuestions = SiteHandoverBuilder.BuilderLabel(Questions);
        var site = new[] { SitePage(1, "home", "Home", home: true), SitePage(2, "questions", "Choosing an adviser") };
        foreach (var onlyLabel in new[] { true, false })
        {
            var plan = Plan(site, new[] { new ShortenedLabel("questions", 2, Questions, shortQuestions, onlyLabel) },
                new BuilderPage[] { PackagePage("home", "Home", true), PackagePage("choosing", shortQuestions) }, changed: new HashSet<int> { 2 });
            Assert.Equal(("Choosing an adviser", true), (Single(plan, "choosing").Page.NavigationLabel, Single(plan, "choosing").LabelKept));
            var left = plan.Pages.Single(p => p.Page.Slug == "questions");
            Assert.Equal((PageChange.Removed, true, !onlyLabel), (left.Change, left.RemovedInBuilder, left.ChangedSinceHandover));
        }
    }

    [Fact]
    public async Task The_review_says_what_became_of_each_shortened_label()
    {
        var shortFirst = SiteHandoverBuilder.BuilderLabel(FirstMeeting);
        var shortLife = SiteHandoverBuilder.BuilderLabel(LifeInsurance);
        var shortQuestions = SiteHandoverBuilder.BuilderLabel(Questions);
        var site = new[]
        {
            SitePage(1, "home", "Home", home: true), SitePage(2, "first", FirstMeeting), SitePage(3, "life", LifeInsurance),
            SitePage(4, "questions", Questions, label: "")    // no label of its own: the page editor never saves one
        };
        var plan = Plan(site, new[]
            {
                new ShortenedLabel("first", 2, FirstMeeting, shortFirst), new ShortenedLabel("life", 3, LifeInsurance, shortLife),
                new ShortenedLabel("questions", 4, "", shortQuestions)
            },
            new BuilderPage[] { PackagePage("home", "Home", true), PackagePage("first", shortFirst), PackagePage("life", "Life insurance check"), PackagePage("questions", shortQuestions) },
            changed: new HashSet<int> { 2 });
        var review = new BuilderImportReviewViewModel
        {
            Agent = new AgentUser { Id = 4, UserName = "sample", CompanyName = "Sample Planning" },
            Website = new AgentWebsite { Id = 9 },
            Import = new WebsiteBuilderImport { Id = 1, FileName = "return.zip", HandoverId = "h_0123456789abcdef" },
            Package = new BuilderPackage(),
            Plan = plan
        };

        var html = WebUtility.HtmlDecode(await BuilderImportScreens571Tests.RenderAsync("~/Views/BuilderImports/Review.cshtml", review));

        Assert.Contains($"Menu label: the builder showed it shortened, as \"{shortFirst}\". It came back unchanged, so the page keeps its own: \"{FirstMeeting}\".", html);
        Assert.Contains($"Menu label: the builder showed it shortened, as \"{shortLife}\", and the designer changed it to \"Life insurance check\". The menu shows that from now on.", html);
        Assert.Contains($"Menu label: the builder showed it shortened, as \"{shortQuestions}\". The page has no menu label of its own on the site, so the menu shows this one from now on; " +
            "change it on the customer's Website Menu screen if it should read otherwise.", html);
        // The page changed in iPro since, whose label stays: the red line says the label is not replaced.
        Assert.Contains("is replaced by the package's version, apart from its menu label, which stays as the site has it.", html);
    }

    [Fact]
    public void A_label_taken_from_the_site_is_part_of_what_the_review_promised()
    {
        // "Bring it in" refuses when the plan is no longer the one the review showed (574). A kept
        // label is read from the site at that moment, so a label changed in between changes the plan.
        var shortQuestions = SiteHandoverBuilder.BuilderLabel(Questions);
        var labels = new[] { new ShortenedLabel("questions", 2, Questions, shortQuestions) };
        var pages = new[] { PackagePage("home", "Home", true), PackagePage("questions", shortQuestions) };
        var before = Plan(new[] { SitePage(1, "home", "Home", home: true), SitePage(2, "questions", Questions) }, labels, pages);
        var after = Plan(new[] { SitePage(1, "home", "Home", home: true), SitePage(2, "questions", "Choosing an adviser") }, labels, pages);
        Assert.Equal("Choosing an adviser", Single(after, "questions").Page.NavigationLabel);
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    // ---------------- helpers ----------------

    private static PlannedPage Single(SiteImportPlan plan, string slug) => plan.Pages.Single(p => p.Page.Slug == slug && p.Change != PageChange.Removed);

    private static WebsitePage SitePage(int id, string slug, string title, bool home = false, string? label = null) => new()
    {
        Id = id, AgentWebsiteId = 9, Title = title, Slug = slug, NavigationLabel = label ?? title, IsHomePage = home, SortOrder = id,
        Blocks = { new WebsiteContentBlock { Id = id * 10, WebsitePageId = id, BlockType = WebsiteBlockTypes.Text, Heading = title, SortOrder = 0 } }
    };

    private static BuilderPage PackagePage(string slug, string label, bool home = false) =>
        new(slug, null, slug, label, home, 0, new[] { new BuilderBlock(WebsiteBlockTypes.Text, "Words", "", "", "", "", "", "", 0, true, "{}", 0, 0) });

    // The plan for a return of a hand-over that carried every page of the site. changed: the rows the
    // customer changed in iPro since.
    private static SiteImportPlan Plan(IReadOnlyList<WebsitePage> site, IReadOnlyList<ShortenedLabel> labels, params BuilderPage[] pages) =>
        Plan(site, labels, pages, changed: null);

    private static SiteImportPlan Plan(IReadOnlyList<WebsitePage> site, IReadOnlyList<ShortenedLabel> labels, BuilderPage[] pages, IReadOnlySet<int>? changed) =>
        SiteImportPlan.Build(
            new BuilderTemplate("sample", "modern", "h_0123456789abcdef", string.Empty, string.Empty, pages),
            new Dictionary<string, string>(), new AgentWebsite { Id = 9 }, site, new HashSet<int>(),
            handedOverPageIds: site.Select(p => p.Id).ToHashSet(),
            handedOverRowBySlug: site.ToDictionary(p => p.Slug, p => p.Id, StringComparer.OrdinalIgnoreCase),
            changedSinceHandover: changed,
            carriedTypes: BuilderContract.BlockTypes.ToHashSet(StringComparer.Ordinal),
            shortenedLabels: labels);
}
