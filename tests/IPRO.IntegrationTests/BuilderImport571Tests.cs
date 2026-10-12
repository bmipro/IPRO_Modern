using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 571 (2026-10-12): iPro's importer for the Template Builder (export contract v0.4). Stage 1 of the
// builder is internal use -- the owner or his designer builds a customer's site in the builder on his
// own machine -- and without an importer the file the builder writes is one iPro cannot take in.
//
// Three things are pinned here:
//   1. the door: a package is taken only when it passes every rule CONTRACT.md states, each refusal
//      names its section, and iPro's own fixtures pass;
//   2. the plan: what bringing a package in does to a site (the page keeps its row and its search
//      settings, a page the package does not have is kept unless removed, the home page is the home page);
//   3. the write: bringing it in, putting the previous site back, and dropping a package unused.
public class BuilderImport571Tests
{
    // ---------------- 1. the door ----------------

    [Fact]
    public void The_return_fixture_passes_against_the_hand_over_it_answers()
    {
        var package = BuilderPackageReader.Read(Fixture("ipro-v0.4-return-fixture.zip"), FixtureHandover());

        Assert.True(package.IsValid, Listed(package));
        var template = package.Template!;
        Assert.Equal("h_2f6c9a41d0b84e5f", template.HandoverId);
        Assert.Equal(new[] { "home", "about", "contact", "resources" }, template.Pages.Select(p => p.Slug));
        Assert.Equal(3, package.Images.Count);
        var article = template.Pages.Single(p => p.Slug == "resources").Blocks.Single(b => b.Type == WebsiteBlockTypes.ArticleContent);
        Assert.Equal(101, article.ArticleId);
        Assert.Equal(101, WebsiteArticleContentSettings.FromJson(article.SettingsJson).ArticleId);
        var hero = template.Pages[0].Blocks[0];
        Assert.Equal("Plan with confidence, at every stage", hero.Heading);
        Assert.Equal(45, WebsiteHeroSettings.FromJson(hero.SettingsJson).OverlayStrength);
        Assert.Equal(4, WebsiteBlogSettings.FromJson(template.Pages[0].Blocks[2].SettingsJson).PostCount);
        Assert.Equal(45, template.Pages.Single(p => p.Slug == "contact").Blocks.Single(b => b.Type == WebsiteBlockTypes.Form).WebsiteFormId);
    }

    [Fact]
    public void A_return_is_refused_while_iPro_has_handed_nothing_over()
    {
        // Stage 1 has no exporter yet, so no site has been handed to the builder: a package that
        // claims a hand-over has nothing to be checked against.
        var package = BuilderPackageReader.Read(Fixture("ipro-v0.4-return-fixture.zip"), BuilderImportContext.DesignerTemplate());

        Assert.False(package.IsValid);
        Assert.Contains(package.Problems, p => p.Rule == "7" && p.Message.Contains("has not handed this site over"));
    }

    [Fact]
    public void The_package_iPro_sends_is_not_a_package_iPro_takes()
    {
        var package = BuilderPackageReader.Read(Fixture("ipro-v0.4-handover-fixture.zip"), FixtureHandover());

        Assert.False(package.IsValid);
        Assert.Contains(package.Problems, p => p.Rule == "9" && p.Message.Contains("sends to the builder"));
    }

    [Fact]
    public void A_site_built_from_scratch_passes()
    {
        var (template, files) = Designer();
        var package = BuilderPackageReader.Read(Zip(template, files), BuilderImportContext.DesignerTemplate());

        Assert.True(package.IsValid, Listed(package));
        Assert.Equal(string.Empty, package.Template!.HandoverId);
        Assert.Equal(4, package.Template.Pages.Count);
        Assert.Equal(64, package.Sha256.Length);
        Assert.All(package.Images.Values, i => Assert.Equal(64, i.Sha256.Length));
    }

    [Fact]
    public void Another_contract_version_is_refused_on_that_alone()
    {
        var (template, files) = Designer();
        template["contractVersion"] = "0.3";
        template["somethingNew"] = 1;   // would be a second problem under 0.4's rules
        var package = BuilderPackageReader.Read(Zip(template, files), BuilderImportContext.DesignerTemplate());

        var problem = Assert.Single(package.Problems);
        Assert.Equal("0", problem.Rule);
        Assert.Contains("\"0.4\" only", problem.Message);
    }

    [Fact]
    public void The_shape_is_strict_every_key_no_other_and_never_null()
    {
        Refused("an unknown top-level key", t => t["extra"] = "x", "2", "does not have: \"extra\"");
        Refused("a missing top-level key", t => t.Remove("logoAltText"), "2", "missing \"logoAltText\"");
        Refused("an unknown key in a page", t => Page(t, "about")["seoTitle"] = "x", "3", "does not have: \"seoTitle\"");
        Refused("a missing key in a block", t => Block(t, "home", 0).Remove("body"), "4", "missing \"body\"");
        Refused("an unknown key in a block", t => Block(t, "home", 0)["css"] = "x", "4", "does not have: \"css\"");
        Refused("an unknown key in settings", t => Block(t, "home", 1)["settings"]!["columns"] = 3, "4", "does not have: \"columns\"");
        // The 11 October review found the builder writing null where the contract wants "".
        Refused("a null imageRef", t => Block(t, "home", 1)["imageRef"] = null, "4", "\"imageRef\" is null");
        Refused("a null logoRef", t => t["logoRef"] = null, "2", "\"logoRef\" is null");
        Refused("a number where text belongs", t => Block(t, "home", 0)["heading"] = 5, "4", "\"heading\" must be text");
        Refused("text where a number belongs", t => Block(t, "home", 0)["sortOrder"] = "0", "4", "\"sortOrder\" must be a whole number");
        Refused("a fraction for a whole number", t => Page(t, "about")["sortOrder"] = 1.5, "3", "\"sortOrder\" must be a whole number");
        Refused("text for a yes or no", t => Block(t, "home", 0)["isVisible"] = "true", "4", "\"isVisible\" must be true or false");
        Refused("a block that is not an object", t => ((JsonArray)Page(t, "home")["blocks"]!)[0] = "Hero", "4", "must be a JSON object");
    }

    [Fact]
    public void Template_level_rules()
    {
        Refused("a template name with capitals", t => t["templateName"] = "Prairie", "2", "templateName is 3 to 60 characters");
        Refused("a template name of two letters", t => t["templateName"] = "ab", "2", "templateName is 3 to 60 characters");
        Refused("an unknown theme", t => t["theme"] = "dark", "2", "\"theme\" is \"dark\"");
        Refused("a malformed handover id", t => t["handoverId"] = "h_123", "9.2", "16 lower-case hexadecimal");
        Refused("a logo that is not a listed image", t => t["logoRef"] = "assets/other.png", "2", "is not one of the images listed");
        Refused("a logo with no description", t => t["logoAltText"] = "", "2", "logoAltText must describe it");
        Refused("a description with no logo", t => t["logoRef"] = "", "2", "logoAltText must be empty");
        Refused("a logo description of 81 characters", t => t["logoAltText"] = new string('a', 81), "2", "at most 80");
        Refused("no pages", t => t["pages"] = new JsonArray(), "2", "has 0 pages");
        Refused("an asset with the wrong type", t => t["assets"]![0]!["type"] = "image/jpeg", "2", "its type must be \"image/png\"");
        Refused("an asset listed twice", t => ((JsonArray)t["assets"]!).Add(JsonNode.Parse(t["assets"]![0]!.ToJsonString())), "1, rule 7", "listed in assets[] twice");
        Refused("an asset ref outside assets/", t => t["assets"]![0]!["ref"] = "images/adviser-logo.png", "1, rule 4", "is not \"assets/\" followed by");
    }

    [Fact]
    public void Page_rules()
    {
        Refused("a slug with a capital", t => Page(t, "about")["slug"] = "About", "3", "not an address a page can have");
        // A pattern matches the whole value: a trailing line break is not part of a slug.
        Refused("a slug with a trailing line break", t => Page(t, "about")["slug"] = "about\n", "3", "not an address a page can have");
        Refused("a slug of 81 characters", t => Page(t, "about")["slug"] = new string('a', 81), "3", "not an address a page can have");
        Refused("two pages with one slug", t => Page(t, "about")["slug"] = "contact", "3", "More than one page has the slug \"contact\"");
        Refused("a reserved address", t => Page(t, "about")["slug"] = "portal", "3", "iPro keeps for itself");
        Refused("the home address on another page", t => Page(t, "about")["slug"] = "home", "3", "More than one page has the slug \"home\"");
        Refused("two home pages", t => Page(t, "about")["isHomePage"] = true, "3", "2 pages are marked as the home page");
        Refused("no home page", t => Page(t, "home")["isHomePage"] = false, "3", "No page is marked as the home page");
        Refused("a home page under another page", t => Page(t, "home")["parentSlug"] = "about", "3", "home page is a top-level page");
        Refused("an unknown parent", t => Page(t, "about")["parentSlug"] = "team", "3", "no page with that slug");
        Refused("a page under itself", t => Page(t, "about")["parentSlug"] = "about", "3", "names itself as its parent");
        Refused("a parent that is a number", t => Page(t, "about")["parentSlug"] = 3, "3", "parentSlug is null for a top-level page");
        Refused("a title of spaces", t => Page(t, "about")["title"] = "   ", "3", "The title is 1 to 120 characters");
        Refused("a title of 121 characters", t => Page(t, "about")["title"] = new string('a', 121), "3", "The title is 1 to 120 characters");
        Refused("a menu label of 41 characters", t => Page(t, "about")["navigationLabel"] = new string('a', 41), "3", "1 to 40 characters");
        Refused("a negative sort order", t => Page(t, "about")["sortOrder"] = -1, "3", "must be 0 or more");
        Refused("two pages side by side with one sort order", t => Page(t, "about")["sortOrder"] = 2, "3", "share the sortOrder 2");
        Refused("a page with no sections", t => Page(t, "about")["blocks"] = new JsonArray(), "3", "has 0 sections");
        Refused("two sections with one sort order", t => Block(t, "home", 1)["sortOrder"] = 0, "4", "Two sections of the page have the sortOrder 0");
    }

    [Fact]
    public void The_home_address_belongs_to_the_home_page()
    {
        // iPro's page editor keeps "home" for the home page; a second page there could never be saved.
        Refused("a home page called welcome beside a page called home", t =>
        {
            Page(t, "home")["isHomePage"] = false;
            Page(t, "about")["isHomePage"] = true;
        }, "3", "The address \"home\" belongs to the home page");
    }

    [Fact]
    public void A_menu_goes_three_levels_and_no_page_is_its_own_ancestor()
    {
        var (template, files) = Designer();
        Page(template, "contact")["parentSlug"] = "about";
        Page(template, "resources")["parentSlug"] = "contact";
        Page(template, "contact")["sortOrder"] = 0;
        Page(template, "resources")["sortOrder"] = 0;
        Assert.True(BuilderPackageReader.Read(Zip(template, files), BuilderImportContext.DesignerTemplate()).IsValid, "three levels are allowed");

        Refused("four levels", t =>
        {
            Page(t, "contact")["parentSlug"] = "about";
            Page(t, "resources")["parentSlug"] = "contact";
            AddPage(t, "deep", parent: "resources");
        }, "3", "4 levels deep");
        Refused("a circle", t =>
        {
            Page(t, "about")["parentSlug"] = "contact";
            Page(t, "contact")["parentSlug"] = "about";
        }, "3", "form a circle");
    }

    [Fact]
    public void Block_rules_common_to_every_type()
    {
        Refused("an unknown type", t => Block(t, "home", 0)["type"] = "Gallery", "4", "is not a section type the builder may send");
        Refused("a type in the wrong case", t => Block(t, "home", 0)["type"] = "hero", "4", "is not a section type the builder may send");
        Refused("a heading of 201 characters", t => Block(t, "home", 0)["heading"] = new string('a', 201), "4", "heading is 201 characters");
        Refused("a subheading of 301 characters", t => Block(t, "home", 0)["subheading"] = new string('a', 301), "4", "subheading is 301 characters");
        Refused("a body of 10 001 characters", t => Block(t, "home", 3)["body"] = new string('a', 10_001), "4", "body is 10001 characters");
        Refused("button words of 61 characters", t => Block(t, "home", 0)["buttonText"] = new string('a', 61), "4", "buttonText is 61 characters");
        Refused("an image that is not listed", t => Block(t, "home", 0)["imageRef"] = "assets/missing.png", "10, rule 1", "is not one of the images listed");
        Refused("a negative sort order", t => Block(t, "home", 0)["sortOrder"] = -2, "4", "must be 0 or more");
        Refused("settings that are a list", t => Block(t, "home", 1)["settings"] = new JsonArray(), "4", "settings must be an object");
        // Every limit counts UTF-16 code units: 100 emoji are 200 units, and one more is over.
        var emoji = char.ConvertFromUtf32(0x1F600);
        Assert.Equal(2, emoji.Length);
        Refused("101 emoji in a heading", t => Block(t, "home", 0)["heading"] = string.Concat(Enumerable.Repeat(emoji, 101)), "4", "heading is 202 characters");
        Taken("100 emoji in a heading", t => Block(t, "home", 0)["heading"] = string.Concat(Enumerable.Repeat(emoji, 100)));
    }

    [Fact]
    public void A_button_link_is_a_page_an_https_address_or_a_mail_or_phone_link()
    {
        foreach (var good in new[] { "", "/", "/contact", "/our-services", "https://example.com/book", "mailto:hello@example.com", "tel:(416) 555-0199" })
        {
            Taken($"buttonUrl '{good}'", t => Block(t, "home", 0)["buttonUrl"] = good);
        }
        foreach (var bad in new[] { "http://example.com", "javascript:alert(1)", "//evil.example", "/\\evil.example", "contact", "HTTPS://example.com", "https://", "tel:", "data:text/html,x" })
        {
            Refused($"buttonUrl '{bad}'", t => Block(t, "home", 0)["buttonUrl"] = bad, "4", "is not a link a button may carry");
        }
        Refused("a link of 501 characters", t => Block(t, "home", 0)["buttonUrl"] = "/" + new string('a', 500), "4", "buttonUrl is 501 characters");
    }

    [Fact]
    public void Each_type_has_its_required_fields_its_variants_and_its_settings()
    {
        // Hero (4.1)
        Refused("a Hero with no heading", t => Block(t, "home", 0)["heading"] = " ", "4.1", "A Hero section needs a heading");
        Refused("a Hero with a layout variant", t => Block(t, "home", 0)["layoutVariant"] = "split", "4", "A Hero section has no layoutVariant");
        Refused("a Hero missing a setting", t => ((JsonObject)Block(t, "home", 0)["settings"]!).Remove("height"), "4.1", "missing \"height\"");
        Refused("a Hero layout that does not exist", t => Block(t, "home", 0)["settings"]!["layout"] = "diagonal", "4.1", "\"layout\" is \"diagonal\"");
        Refused("a Hero overlay of 86", t => Block(t, "home", 0)["settings"]!["overlayStrength"] = 86, "4.1", "from 0 to 85");
        // Text (4.2)
        Refused("a Text with nothing in it", t => { Block(t, "home", 3)["body"] = ""; }, "4.2", "needs a heading or a body");
        Refused("a Text variant that does not exist", t => Block(t, "home", 3)["layoutVariant"] = "image-top", "4", "is not one a Text section has");
        Refused("a Text button with words and no link", t => Block(t, "home", 3)["buttonText"] = "Read more", "4.2", "or neither");
        Refused("a Text button with a link and no words", t => Block(t, "home", 3)["buttonUrl"] = "/about", "4.2", "or neither");
        Taken("a Text button with both", t => { Block(t, "home", 3)["buttonText"] = "Read more"; Block(t, "home", 3)["buttonUrl"] = "/about"; });
        Refused("Text settings that are not empty", t => Block(t, "home", 3)["settings"] = new JsonObject { ["x"] = 1 }, "4", "does not have: \"x\"");
        // Services (4.3)
        Refused("a Services with no body", t => Block(t, "home", 1)["body"] = "", "4.3", "one service per line");
        Refused("a Services variant in capitals", t => Block(t, "home", 1)["layoutVariant"] = "CARDS", "4", "is not one a Services section has");
        Refused("a Services with an image", t => Block(t, "home", 1)["imageRef"] = "assets/hero-home.png", "10, rule 7", "does not use imageRef");
        Refused("a Services with a button", t => Block(t, "home", 1)["buttonText"] = "More", "4", "does not use buttonText");
        // A long service line and an empty one are guidance, not a reason to refuse.
        Taken("a Services line of 120 characters and an empty line", t => Block(t, "home", 1)["body"] = new string('a', 120) + "\n\nSecond");
        // CallToAction (4.4)
        Refused("a CallToAction with no heading", t => Block(t, "about", 1)["heading"] = "", "4.4", "needs a heading");
        Refused("a CallToAction with an image", t => Block(t, "about", 1)["imageRef"] = "assets/hero-home.png", "10, rule 7", "does not use imageRef");
        Taken("a CallToAction with no button", t => { Block(t, "about", 1)["buttonText"] = ""; Block(t, "about", 1)["buttonUrl"] = ""; });
        // ContactForm (4.5): showPhoto is always written.
        Refused("a ContactForm with no showPhoto", t => Block(t, "contact", 0)["settings"] = new JsonObject(), "4.5", "missing \"showPhoto\"");
        Refused("a ContactForm showPhoto that is text", t => Block(t, "contact", 0)["settings"]!["showPhoto"] = "yes", "4.5", "must be true or false");
        // Blog (4.8)
        Refused("a Blog of 51 posts", t => Block(t, "home", 2)["settings"]!["postCount"] = 51, "4.8", "from 1 to 50");
        Refused("a Blog of 0 posts", t => Block(t, "home", 2)["settings"]!["postCount"] = 0, "4.8", "from 1 to 50");
        Refused("a Blog with no showImages", t => ((JsonObject)Block(t, "home", 2)["settings"]!).Remove("showImages"), "4.8", "missing \"showImages\"");
        Refused("a Blog with a button", t => Block(t, "home", 2)["buttonUrl"] = "/blog", "4", "does not use buttonUrl");
    }

    [Fact]
    public void An_article_or_form_section_exists_only_in_a_site_iPro_handed_over()
    {
        Refused("an ArticleContent in a site built from scratch", t => AddBlock(t, "resources", "ArticleContent", new JsonObject { ["articleId"] = 101 }), "4.7", "only in a site iPro handed to the builder");
        Refused("a Form in a site built from scratch", t => AddBlock(t, "resources", "Form", new JsonObject { ["websiteFormId"] = 45 }), "4.9", "only in a site iPro handed to the builder");
    }

    [Fact]
    public void A_return_is_held_to_what_its_hand_over_offered()
    {
        var (template, files) = Returned();
        Assert.True(BuilderPackageReader.Read(Zip(template, files), FixtureHandover()).IsValid);

        RefusedReturn("an article outside the catalogue", t => Block(t, "resources", 1)["settings"]!["articleId"] = 999, "7", "Article 999 is not one of this customer's published articles");
        RefusedReturn("a form outside the catalogue", t => Block(t, "contact", 1)["settings"]!["websiteFormId"] = 7, "7", "Form 7 is not one of this customer's active forms");
        RefusedReturn("an article id of zero", t => Block(t, "resources", 1)["settings"]!["articleId"] = 0, "4.7", "must be 1 or more");
        RefusedReturn("an article section with a heading", t => Block(t, "resources", 1)["heading"] = "Read this", "4", "does not use heading");
        RefusedReturn("another hand-over's id", t => t["handoverId"] = "h_0000000000000000", "7", "This site was handed over as h_2f6c9a41d0b84e5f");
        RefusedReturn("a package that says it was built from scratch", t => t["handoverId"] = "", "7", "says it was built from scratch");

        // A package whose blocks are all fine, against a hand-over that did not offer the blog.
        var narrow = FixtureHandover() with
        {
            AllowedBlockTypes = new HashSet<string>(BuilderContract.BlockTypes.Where(x => x != WebsiteBlockTypes.Blog))
        };
        var refused = BuilderPackageReader.Read(Zip(template, files), narrow);
        Assert.Contains(refused.Problems, p => p.Page == "home" && p.Block == 2 && p.Message.Contains("outside this customer's package"));
    }

    [Fact]
    public void A_problem_names_its_page_and_the_block_by_its_sort_order()
    {
        var (template, files) = Designer();
        Block(template, "about", 1)["heading"] = "";
        var package = BuilderPackageReader.Read(Zip(template, files), BuilderImportContext.DesignerTemplate());

        var problem = Assert.Single(package.Problems);
        Assert.Equal("about", problem.Page);
        Assert.Equal(1, problem.Block);   // contract section 11.5: the block's sortOrder
        Assert.Equal("Page \"about\", section 1", problem.Where);
    }

    [Fact]
    public void Every_problem_is_reported_not_only_the_first()
    {
        var (template, files) = Designer();
        Block(template, "home", 0)["heading"] = "";
        Block(template, "home", 1)["body"] = "";
        Page(template, "about")["slug"] = "portal";
        var package = BuilderPackageReader.Read(Zip(template, files), BuilderImportContext.DesignerTemplate());

        Assert.Equal(3, package.Problems.Count);
        Assert.Null(package.Template);
    }

    // ---------------- the ZIP ----------------

    [Fact]
    public void The_zip_holds_template_json_and_images_and_nothing_else()
    {
        var (template, files) = Designer();
        var json = template.ToJsonString();

        RefusedZip("no template.json", Zip((string?)null, files), "1, rule 1", "no template.json");
        RefusedZip("template.json in a folder", Zip((string?)null, files.Append(("site/template.json", Encoding.UTF8.GetBytes(json)))), "1, rule 3", "inside a folder");
        RefusedZip("a byte-order mark", Zip(json, files, bom: true), "1, rule 1", "byte-order mark");
        RefusedZip("JSON that does not parse", Zip("{ \"contractVersion\": ", files), "1, rule 1", "not valid JSON");
        RefusedZip("bytes that are not UTF-8", Zip((string?)null, files.Append(("template.json", new byte[] { 0x7B, 0xFF, 0xFE, 0x7D }))), "1, rule 1", "not valid UTF-8");
        RefusedZip("a text file beside it", Zip(json, files.Append(("readme.txt", new byte[] { 1 }))), "1, rule 3", "may not be in a package");
        RefusedZip("an SVG among the images", Zip(json, files.Append(("assets/logo.svg", Encoding.UTF8.GetBytes("<svg/>")))), "1, rule 3", "not an image type");
        RefusedZip("a folder under assets", Zip(json, files.Append(("assets/home/hero.png", Png()))), "1, rule 2", "The assets folder is flat");
        RefusedZip("a capital in an image name", Zip(json, files.Append(("assets/Hero.png", Png()))), "1, rule 4", "lower-case letters, digits and hyphens");
        RefusedZip("an image name of 101 characters", Zip(json, files.Append(("assets/" + new string('a', 97) + ".png", Png()))), "1, rule 4", "at most 100 characters");
        RefusedZip("an image nothing lists", Zip(json, files.Append(("assets/extra.png", Png()))), "1, rule 7", "assets[] does not list it");
        RefusedZip("a listed image that is not there", Zip(json, files.Where(f => f.Name != "assets/hero-home.png")), "1, rule 7", "the file is not in the package");
        RefusedZip("a file that is not what its name says", Zip(json, files.Select(f => f.Name == "assets/hero-home.png" ? (f.Name, Encoding.ASCII.GetBytes("GIF89a-not-a-png")) : f)), "1, rule 6", "is not a real PNG image");
        RefusedZip("a path that climbs out", Zip(json, files.Append(("../evil.png", Png()))), "1, rule 4", "is not a file name a package may hold");
        RefusedZip("not a ZIP at all", Encoding.ASCII.GetBytes("this is not a zip file"), "1", "not a ZIP package");
    }

    [Fact]
    public void A_folder_entry_is_ignored_and_so_is_an_image_nothing_uses()
    {
        var (template, files) = Designer();
        // Listed in assets[] and present, but no block and no logo points at it.
        ((JsonArray)template["assets"]!).Add(new JsonObject { ["ref"] = "assets/spare.png", ["type"] = "image/png" });
        var zip = Zip(template.ToJsonString(), files.Append(("assets/spare.png", Png(7))), folders: new[] { "assets/" });

        var package = BuilderPackageReader.Read(zip, BuilderImportContext.DesignerTemplate());

        Assert.True(package.IsValid, Listed(package));
        Assert.Equal(new[] { "assets/spare.png" }, package.IgnoredAssets);
        Assert.DoesNotContain("assets/spare.png", package.Images.Keys);
    }

    [Fact]
    public void Sizes_are_capped_as_section_1_says()
    {
        var (template, files) = Designer();
        var big = Png(1).Concat(new byte[(int)BuilderContract.MaxAssetBytes]).ToArray();   // 8 MB and a few bytes
        RefusedZip("an image over 8 MB",
            Zip(template.ToJsonString(), files.Select(f => f.Name == "assets/hero-home.png" ? (f.Name, big) : f)), "1, rule 5", "is larger than 8 MB");

        var huge = new byte[BuilderContract.MaxZipBytes + 1];
        RefusedZip("a package over 40 MB", huge, "1, rule 5", "at most 40 MB");
    }

    [Fact]
    public void New_images_must_fit_in_the_storage_left_and_an_image_already_stored_costs_nothing()
    {
        var (template, files) = Designer();
        var zip = Zip(template, files);
        var open = BuilderPackageReader.Read(zip, BuilderImportContext.DesignerTemplate());
        var bytes = open.Images.Values.Sum(i => i.Length);

        var tight = BuilderPackageReader.Read(zip, BuilderImportContext.DesignerTemplate(remainingBytes: bytes - 1));
        Assert.Contains(tight.Problems, p => p.Rule == "10, rule 4" && p.Message.Contains("storage left"));

        Assert.True(BuilderPackageReader.Read(zip, BuilderImportContext.DesignerTemplate(remainingBytes: bytes)).IsValid);

        // Section 10, rule 3: matched by content, so an image the site already stores needs no room.
        var known = open.Images.Values.Select(i => i.Sha256).ToHashSet();
        var linked = BuilderPackageReader.Read(zip, BuilderImportContext.DesignerTemplate(known, remainingBytes: 0));
        Assert.True(linked.IsValid, Listed(linked));
        Assert.Empty(linked.NewImages(known));
    }

    [Fact]
    public void The_same_image_under_two_names_is_one_new_image()
    {
        var (template, files) = Designer();
        var hero = files.Single(f => f.Name == "assets/hero-home.png").Bytes;
        ((JsonArray)template["assets"]!).Add(new JsonObject { ["ref"] = "assets/hero-again.png", ["type"] = "image/png" });
        Block(template, "resources", 0)["imageRef"] = "assets/hero-again.png";
        var package = BuilderPackageReader.Read(Zip(template.ToJsonString(), files.Append(("assets/hero-again.png", hero))), BuilderImportContext.DesignerTemplate());

        Assert.True(package.IsValid, Listed(package));
        Assert.Equal(4, package.Images.Count);
        Assert.Equal(3, package.NewImages(new HashSet<string>()).Count);
    }

    [Fact]
    public void The_addresses_iPro_keeps_for_itself_stay_in_step_with_the_router()
    {
        // Program.cs decides which first path segments a customer's own domain never gives to the
        // customer's site. A page with one of those names could not be reached, so the importer
        // refuses it; this keeps the two lists from drifting.
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "IPRO.Web", "Program.cs"));
        var start = program.IndexOf("static bool IsNeverShadowedPrefix", StringComparison.Ordinal);
        var end = program.IndexOf("_ => false", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var names = System.Text.RegularExpressions.Regex.Matches(
                string.Join("\n", program[start..end].Split('\n').Where(line => !line.TrimStart().StartsWith("//"))), "\"([a-z-]+)\"")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(names.Count >= 15, "the router's list was not found");

        Assert.Empty(names.Except(BuilderContract.ReservedSlugs));
        Assert.Contains("portal", BuilderContract.ReservedSlugs);
        // Every reserved name is refused, and each could otherwise be a slug.
        Assert.All(BuilderContract.ReservedSlugs.Where(name => name != "t"), name => Assert.Matches(BuilderContract.Slug, name));
    }

    // ---------------- 2. the plan ----------------

    [Fact]
    public void On_an_empty_site_every_page_is_added_with_its_sections_images_and_parents()
    {
        var (template, files) = Designer();
        Page(template, "resources")["parentSlug"] = "about";
        Page(template, "resources")["sortOrder"] = 0;
        var package = BuilderPackageReader.Read(Zip(template, files), BuilderImportContext.DesignerTemplate());
        var urls = package.Images.Keys.ToDictionary(k => k, k => "https://blob.example.test/website-media/" + k[7..]);
        var site = new AgentWebsite { Id = 9, LogoUrl = "" };

        var plan = SiteImportPlan.Build(package.Template!, urls, site, Array.Empty<WebsitePage>(), new HashSet<int>());

        Assert.Empty(plan.Problems);
        Assert.All(plan.Pages, p => Assert.Equal(PageChange.Added, p.Change));
        Assert.Equal(new[] { "home", "about", "contact", "resources" }, plan.PagesAfter.OrderBy(p => p.ParentPageId.HasValue).ThenBy(p => p.SortOrder).Select(p => p.Slug));
        var home = plan.PagesAfter.Single(p => p.IsHomePage);
        Assert.Equal("home", home.Slug);
        Assert.True(home.IsPublished && home.ShowInNavigation);
        Assert.Equal("https://blob.example.test/website-media/hero-home.png", home.Blocks.First().ImageUrl);
        Assert.Equal("split", WebsiteHeroSettings.FromJson(home.Blocks.First().SettingsJson).Layout);
        Assert.Equal("cards", home.Blocks.Single(b => b.BlockType == WebsiteBlockTypes.Services).LayoutVariant);
        var about = plan.PagesAfter.Single(p => p.Slug == "about");
        Assert.Equal(about.Id, plan.PagesAfter.Single(p => p.Slug == "resources").ParentPageId);
        Assert.True(plan.PagesAfter.All(p => p.Id < 0), "a page that does not exist yet has a temporary id");
        Assert.Equal(plan.PagesAfter.SelectMany(p => p.Blocks).Count(), plan.PagesAfter.SelectMany(p => p.Blocks).Select(b => b.Id).Distinct().Count());
        Assert.Equal(LogoChange.Set, plan.Logo);
        Assert.Equal("https://blob.example.test/website-media/adviser-logo.png", plan.LogoUrlAfter);
    }

    [Fact]
    public void A_page_the_package_has_keeps_its_row_and_search_settings_and_other_pages_stay()
    {
        var package = DesignerPackage();
        var current = new List<WebsitePage>
        {
            ExistingPage(1, "home", "Welcome", home: true, order: 0, WebsiteBlockTypes.Hero, WebsiteBlockTypes.Reviews),
            ExistingPage(2, "about", "About us", home: false, order: 1, WebsiteBlockTypes.Text),
            ExistingPage(3, "menu", "Menu", home: false, order: 2, WebsiteBlockTypes.PriceList),
            ExistingPage(4, "find-us", "Find us", home: false, order: 3, WebsiteBlockTypes.Maps)
        };
        current[1].MetaTitle = "About the bakery";
        current[1].MetaDescription = "Who bakes the bread.";
        current[1].OldAddresses = "about.html";
        current[1].ShowInNavigation = false;
        current[1].IsPublished = false;

        var plan = SiteImportPlan.Build(package.Template!, Urls(package), new AgentWebsite { Id = 9, LogoUrl = "https://blob.example.test/agent-logos/old.png" }, current, new HashSet<int>());

        Assert.Empty(plan.Problems);
        var about = plan.Pages.Single(p => p.Page.Slug == "about");
        Assert.Equal(PageChange.Replaced, about.Change);
        Assert.Equal(2, about.Page.Id);
        Assert.Equal("About", about.Page.Title);
        Assert.Equal("About the bakery", about.Page.MetaTitle);
        Assert.Equal("Who bakes the bread.", about.Page.MetaDescription);
        Assert.Equal("about.html", about.Page.OldAddresses);
        Assert.False(about.Page.ShowInNavigation);
        Assert.False(about.Page.IsPublished);

        // What the builder cannot carry is named, so nobody finds out afterwards.
        var home = plan.Pages.Single(p => p.Page.IsHomePage);
        Assert.Equal(new[] { WebsiteBlockTypes.Reviews }, home.SectionsLost);
        Assert.Empty(about.SectionsLost);

        // The site's other pages stay, after the package's pages in the menu, in the order they had.
        var menu = plan.Pages.Single(p => p.Page.Slug == "menu");
        var findUs = plan.Pages.Single(p => p.Page.Slug == "find-us");
        Assert.Equal(PageChange.Kept, menu.Change);
        Assert.Equal(WebsiteBlockTypes.PriceList, menu.Page.Blocks.Single().BlockType);
        var lastFromPackage = plan.Pages.Where(p => p.Change != PageChange.Kept).Max(p => p.Page.SortOrder);
        Assert.Equal(lastFromPackage + 1, menu.Page.SortOrder);
        Assert.Equal(lastFromPackage + 2, findUs.Page.SortOrder);
        Assert.Equal(plan.PagesAfter.Count, plan.PagesAfter.Select(p => p.SortOrder).Distinct().Count());

        Assert.Equal(LogoChange.Replaced, plan.Logo);
    }

    [Fact]
    public void A_page_can_be_removed_and_what_sat_under_it_moves_up()
    {
        var package = DesignerPackage();
        var current = new List<WebsitePage>
        {
            ExistingPage(1, "home", "Home", home: true, order: 0, WebsiteBlockTypes.Hero),
            ExistingPage(5, "old-section", "Old section", home: false, order: 5, WebsiteBlockTypes.Text),
            ExistingPage(6, "old-child", "Old child", home: false, order: 0, WebsiteBlockTypes.Gallery),
            ExistingPage(7, "old-grandchild", "Old grandchild", home: false, order: 0, WebsiteBlockTypes.Text)
        };
        current[2].ParentPageId = 5;
        current[3].ParentPageId = 6;

        var plan = SiteImportPlan.Build(package.Template!, Urls(package), new AgentWebsite { Id = 9 }, current, new HashSet<int> { 5, 6, 1, 999 });

        Assert.Empty(plan.Problems);
        Assert.Equal(new[] { 5, 6 }, plan.Pages.Where(p => p.Change == PageChange.Removed).Select(p => p.Page.Id).OrderBy(id => id));
        Assert.Equal(new[] { WebsiteBlockTypes.Gallery }, plan.Pages.Single(p => p.Page.Id == 6).SectionsLost);
        // The home page is in the package: a tick against it removes nothing.
        Assert.Equal(PageChange.Replaced, plan.Pages.Single(p => p.Page.Id == 1).Change);
        var grandchild = plan.Pages.Single(p => p.Page.Id == 7);
        Assert.Equal(PageChange.Kept, grandchild.Change);
        Assert.Null(grandchild.Page.ParentPageId);
        Assert.DoesNotContain(plan.PagesAfter, p => p.Id is 5 or 6);
    }

    [Fact]
    public void The_home_page_is_the_home_page_whatever_either_side_calls_it()
    {
        var (template, files) = Designer();
        Page(template, "home")["slug"] = "welcome";
        var package = BuilderPackageReader.Read(Zip(template, files), BuilderImportContext.DesignerTemplate());
        Assert.True(package.IsValid, Listed(package));
        var current = new List<WebsitePage>
        {
            ExistingPage(1, "home", "Home", home: true, order: 0, WebsiteBlockTypes.Hero),
            ExistingPage(2, "about", "About", home: false, order: 1, WebsiteBlockTypes.Text)
        };
        current[0].MetaTitle = "Best bakery in town";

        var plan = SiteImportPlan.Build(package.Template!, Urls(package), new AgentWebsite { Id = 9 }, current, new HashSet<int>());

        Assert.Empty(plan.Problems);
        var home = plan.Pages.Single(p => p.Page.IsHomePage);
        Assert.Equal(PageChange.Replaced, home.Change);
        Assert.Equal(1, home.Page.Id);
        Assert.Equal("welcome", home.Page.Slug);
        Assert.Equal("Best bakery in town", home.Page.MetaTitle);
        Assert.Single(plan.PagesAfter, p => p.IsHomePage);

        // ...and when the site already has another page at that address, it says so instead of colliding.
        current.Add(ExistingPage(3, "welcome", "Welcome offer", home: false, order: 2, WebsiteBlockTypes.Text));
        var blocked = SiteImportPlan.Build(package.Template!, Urls(package), new AgentWebsite { Id = 9 }, current, new HashSet<int>());
        Assert.Contains(blocked.Problems, p => p.Contains("already has a page at \"/welcome\""));
        var cleared = SiteImportPlan.Build(package.Template!, Urls(package), new AgentWebsite { Id = 9 }, current, new HashSet<int> { 3 });
        Assert.Empty(cleared.Problems);
    }

    [Fact]
    public void A_kept_page_cannot_be_pushed_below_the_third_level()
    {
        var (template, files) = Designer();
        Page(template, "contact")["parentSlug"] = "about";
        Page(template, "contact")["sortOrder"] = 0;
        Page(template, "resources")["parentSlug"] = "contact";
        Page(template, "resources")["sortOrder"] = 0;
        var package = BuilderPackageReader.Read(Zip(template, files), BuilderImportContext.DesignerTemplate());
        Assert.True(package.IsValid, Listed(package));
        var current = new List<WebsitePage>
        {
            ExistingPage(1, "home", "Home", home: true, order: 0, WebsiteBlockTypes.Hero),
            ExistingPage(2, "resources", "Resources", home: false, order: 1, WebsiteBlockTypes.Text),
            ExistingPage(3, "guides", "Guides", home: false, order: 0, WebsiteBlockTypes.Text)
        };
        current[2].ParentPageId = 2;   // sits under Resources, which the package moves to the third level

        var plan = SiteImportPlan.Build(package.Template!, Urls(package), new AgentWebsite { Id = 9 }, current, new HashSet<int>());

        Assert.Contains(plan.Problems, p => p.Contains("\"Guides\" would end up 4 levels deep"));
    }

    [Fact]
    public void A_site_built_from_scratch_without_a_logo_leaves_the_sites_logo_alone()
    {
        var (template, files) = Designer();
        template["logoRef"] = "";
        template["logoAltText"] = "";
        var assets = (JsonArray)template["assets"]!;
        assets.Remove(assets.Single(a => a!["ref"]!.GetValue<string>() == "assets/adviser-logo.png"));
        var package = BuilderPackageReader.Read(Zip(template.ToJsonString(), files.Where(f => f.Name != "assets/adviser-logo.png")), BuilderImportContext.DesignerTemplate());
        Assert.True(package.IsValid, Listed(package));

        var plan = SiteImportPlan.Build(package.Template!, Urls(package), new AgentWebsite { Id = 9, LogoUrl = "https://blob.example.test/agent-logos/kept.png" },
            Array.Empty<WebsitePage>(), new HashSet<int>());

        Assert.Equal(LogoChange.Unchanged, plan.Logo);
        Assert.Equal("https://blob.example.test/agent-logos/kept.png", plan.LogoUrlAfter);
    }

    // ---------------- 3. the write ----------------

    [Fact]
    public async Task Storing_a_package_puts_its_new_images_in_the_library_and_touches_no_page()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await SeedSiteAsync(db);
        var blob = new MemoryBlob();
        var zip = Zip(Designer());

        var context = await BuilderImportService.ContextAsync(db, site, AgentStorage.LimitBytes(50));
        var package = BuilderPackageReader.Read(zip, context);
        Assert.True(package.IsValid, Listed(package));
        var import = await BuilderImportService.StoreAsync(db, blob, site, package, "exports/bakery-20261012.zip", "bahman");

        Assert.Equal(WebsiteBuilderImportStatus.Pending, import.Status);
        Assert.Equal("bakery-20261012.zip", import.FileName);
        Assert.Equal(4, import.PageCount);
        Assert.Equal(3, import.NewImageCount);
        Assert.Equal(package.Images.Values.Sum(i => i.Length), import.NewImageBytes);
        Assert.Equal("bahman", import.UploadedBy);
        var library = await db.WebsiteMediaAssets.AsNoTracking().Where(a => a.AgentWebsiteId == site.Id).ToListAsync();
        Assert.Equal(3, library.Count);
        Assert.All(library, a => { Assert.Equal(64, a.ContentSha256.Length); Assert.Equal("image/png", a.ContentType); Assert.True(blob.Files.ContainsKey(a.BlobUrl)); });
        Assert.Equal(2, await db.WebsitePages.CountAsync(p => p.AgentWebsiteId == site.Id));   // the site as seeded
        // The images count against the customer's storage like any other upload.
        Assert.Equal(import.NewImageBytes, await AgentStorage.TotalBytesAsync(db, site.AgentUserId));

        // The same package again (the normal loop: edit in the builder, export, upload): nothing new is stored.
        var again = BuilderPackageReader.Read(zip, await BuilderImportService.ContextAsync(db, site, AgentStorage.LimitBytes(50)));
        var second = await BuilderImportService.StoreAsync(db, blob, site, again, "bakery-20261012.zip", "bahman");
        Assert.Equal(0, second.NewImageCount);
        Assert.Equal(3, await db.WebsiteMediaAssets.CountAsync(a => a.AgentWebsiteId == site.Id));
        Assert.All(BuilderImportService.StoredImages(second), i => Assert.False(i.Created));
        Assert.Equal(BuilderImportService.StoredImages(import).Select(i => i.Url), BuilderImportService.StoredImages(second).Select(i => i.Url));
    }

    [Fact]
    public async Task Bringing_a_package_in_writes_the_plan_and_the_previous_site_can_be_put_back()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await SeedSiteAsync(db);
        var before = await SiteAsync(db, site.Id);
        var blob = new MemoryBlob();
        var package = BuilderPackageReader.Read(Zip(Designer()), await BuilderImportService.ContextAsync(db, site, 0));
        var import = await BuilderImportService.StoreAsync(db, blob, site, package, "site.zip", "bahman");
        db.ChangeTracker.Clear();

        import = await db.WebsiteBuilderImports.SingleAsync(i => i.Id == import.Id);
        var result = await BuilderImportService.ApplyAsync(db, import, new HashSet<int>(), templateId: null, "bahman");
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        db.ChangeTracker.Clear();

        var after = await SiteAsync(db, site.Id);
        Assert.Equal(new[] { "home", "about", "contact", "resources", "menu" }, after.Pages.Select(p => p.Slug));
        var home = after.Pages.Single(p => p.IsHomePage);
        Assert.Equal(before.Pages.Single(p => p.IsHomePage).Id, home.Id);   // the same row: its visits and links stay
        Assert.Equal("Best bakery in town", home.MetaTitle);
        Assert.Equal(new[] { WebsiteBlockTypes.Hero, WebsiteBlockTypes.Services, WebsiteBlockTypes.Blog, WebsiteBlockTypes.Text }, home.Blocks.OrderBy(b => b.SortOrder).Select(b => b.BlockType));
        var hero = home.Blocks.OrderBy(b => b.SortOrder).First();
        Assert.Equal("Plan with confidence, at every stage", hero.Heading);
        Assert.True(blob.Files.ContainsKey(hero.ImageUrl), "the hero points at the image stored in the library");
        Assert.Equal("Retirement planning\nInsurance review\nEducation savings", home.Blocks.Single(b => b.BlockType == WebsiteBlockTypes.Services).Body);
        Assert.True(WebsiteContactFormSettings.FromJson(after.Pages.Single(p => p.Slug == "contact").Blocks.Single().SettingsJson).ShowPhoto);
        var menu = after.Pages.Single(p => p.Slug == "menu");
        Assert.Equal(before.Pages.Single(p => p.Slug == "menu").Blocks.Single().SettingsJson, menu.Blocks.Single().SettingsJson);
        Assert.True(blob.Files.ContainsKey(after.Site.LogoUrl));
        var applied = await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == import.Id);
        Assert.Equal(WebsiteBuilderImportStatus.Applied, applied.Status);
        Assert.NotNull(applied.AppliedAt);
        Assert.False(string.IsNullOrWhiteSpace(applied.SnapshotJson));

        // Twice is refused: the package is on the site.
        var twice = await BuilderImportService.ApplyAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == import.Id), new HashSet<int>(), null, "bahman");
        Assert.False(twice.Ok);
        db.ChangeTracker.Clear();

        // The previous site, back as it was: every page, section, setting and the logo.
        var restored = await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == import.Id));
        Assert.True(restored.Ok, string.Join("; ", restored.Problems));
        db.ChangeTracker.Clear();
        var back = await SiteAsync(db, site.Id);
        Assert.Equal(Describe(before), Describe(back));
        Assert.Equal(before.Pages.Select(p => p.Id), back.Pages.Select(p => p.Id));
        Assert.Equal(WebsiteBuilderImportStatus.Restored, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync(i => i.Id == import.Id)).Status);
        Assert.False((await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == import.Id))).Ok);
    }

    [Fact]
    public async Task Removing_a_page_and_switching_the_design_are_part_of_bringing_it_in_and_come_back_too()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await SeedSiteAsync(db);
        var other = new WebsiteTemplate { TemplateKey = "classic-sidebar", Name = "Classic", IsActive = true };
        db.Add(other);
        await db.SaveChangesAsync();
        var before = await SiteAsync(db, site.Id);
        var menuId = before.Pages.Single(p => p.Slug == "menu").Id;
        var package = BuilderPackageReader.Read(Zip(Designer()), await BuilderImportService.ContextAsync(db, site, 0));
        var import = await BuilderImportService.StoreAsync(db, new MemoryBlob(), site, package, "site.zip", "bahman");
        db.ChangeTracker.Clear();

        Assert.False((await BuilderImportService.ApplyAsync(db, await db.WebsiteBuilderImports.SingleAsync(), new HashSet<int>(), templateId: 987654, "bahman")).Ok);
        db.ChangeTracker.Clear();
        var result = await BuilderImportService.ApplyAsync(db, await db.WebsiteBuilderImports.SingleAsync(), new HashSet<int> { menuId }, other.Id, "bahman");
        Assert.True(result.Ok, string.Join("; ", result.Problems));
        db.ChangeTracker.Clear();

        var after = await SiteAsync(db, site.Id);
        Assert.DoesNotContain(after.Pages, p => p.Slug == "menu");
        Assert.Equal(other.Id, after.Site.TemplateId);

        Assert.True((await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync())).Ok);
        db.ChangeTracker.Clear();
        var back = await SiteAsync(db, site.Id);
        Assert.Equal(Describe(before), Describe(back));   // the removed page is back, with its sections
        Assert.Equal(before.Site.TemplateId, back.Site.TemplateId);
    }

    [Fact]
    public async Task Only_the_newest_package_on_the_site_can_be_taken_back_out()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await SeedSiteAsync(db);
        var blob = new MemoryBlob();
        var first = await BuilderImportService.StoreAsync(db, blob, site,
            BuilderPackageReader.Read(Zip(Designer()), await BuilderImportService.ContextAsync(db, site, 0)), "one.zip", "bahman");
        Assert.True((await BuilderImportService.ApplyAsync(db, first, new HashSet<int>(), null, "bahman")).Ok);
        var (changed, files) = Designer();
        Block(changed, "home", 0)["heading"] = "Second version";
        var second = await BuilderImportService.StoreAsync(db, blob, site,
            BuilderPackageReader.Read(Zip(changed, files), await BuilderImportService.ContextAsync(db, site, 0)), "two.zip", "bahman");
        Assert.True((await BuilderImportService.ApplyAsync(db, second, new HashSet<int>(), null, "bahman")).Ok);
        db.ChangeTracker.Clear();

        var refused = await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == first.Id));
        Assert.False(refused.Ok);
        Assert.Contains("newer package", refused.Problems.Single());
        Assert.Equal(second.Id, (await BuilderImportService.LatestAppliedAsync(db, site.Id))!.Id);

        // Taking the second out leaves the first on the site, and the first can then be taken out.
        Assert.True((await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == second.Id))).Ok);
        db.ChangeTracker.Clear();
        Assert.Equal("Plan with confidence, at every stage", (await SiteAsync(db, site.Id)).Pages.Single(p => p.IsHomePage).Blocks.OrderBy(b => b.SortOrder).First().Heading);
        Assert.True((await BuilderImportService.RestoreAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == first.Id))).Ok);
    }

    [Fact]
    public async Task Dropping_a_package_takes_its_unused_images_back_out_and_keeps_the_ones_in_use()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await SeedSiteAsync(db);
        var blob = new MemoryBlob();
        var package = BuilderPackageReader.Read(Zip(Designer()), await BuilderImportService.ContextAsync(db, site, 0));
        var import = await BuilderImportService.StoreAsync(db, blob, site, package, "site.zip", "bahman");
        var stored = BuilderImportService.StoredImages(import);
        // One of its images was meanwhile put on a page by hand (the media library is the customer's).
        var used = stored.Single(i => i.Ref == "assets/hero-home.png").Url;
        var menu = await db.WebsiteContentBlocks.FirstAsync(b => b.WebsitePage.AgentWebsiteId == site.Id && b.BlockType == WebsiteBlockTypes.PriceList);
        menu.ImageUrl = used;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.True(await BuilderImportService.DiscardAsync(db, blob, await db.WebsiteBuilderImports.SingleAsync()));
        db.ChangeTracker.Clear();

        var library = await db.WebsiteMediaAssets.AsNoTracking().Where(a => a.AgentWebsiteId == site.Id).Select(a => a.BlobUrl).ToListAsync();
        Assert.Equal(new[] { used }, library);
        Assert.Equal(2, blob.Deleted.Count);
        Assert.DoesNotContain(used, blob.Deleted);
        Assert.Equal(WebsiteBuilderImportStatus.Discarded, (await db.WebsiteBuilderImports.AsNoTracking().SingleAsync()).Status);
        // Dropped is final: it can be neither brought in nor dropped again.
        Assert.False(await BuilderImportService.DiscardAsync(db, blob, await db.WebsiteBuilderImports.SingleAsync()));
        Assert.False((await BuilderImportService.ApplyAsync(db, await db.WebsiteBuilderImports.SingleAsync(), new HashSet<int>(), null, "bahman")).Ok);
    }

    [Fact]
    public async Task Dropping_one_package_leaves_the_images_another_package_links()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await SeedSiteAsync(db);
        var blob = new MemoryBlob();
        var zip = Zip(Designer());
        var first = await BuilderImportService.StoreAsync(db, blob, site, BuilderPackageReader.Read(zip, await BuilderImportService.ContextAsync(db, site, 0)), "one.zip", "bahman");
        var second = await BuilderImportService.StoreAsync(db, blob, site, BuilderPackageReader.Read(zip, await BuilderImportService.ContextAsync(db, site, 0)), "two.zip", "bahman");
        db.ChangeTracker.Clear();

        // The first package stored the images; the second links them and is still waiting.
        Assert.True(await BuilderImportService.DiscardAsync(db, blob, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == first.Id)));

        Assert.Empty(blob.Deleted);
        Assert.Equal(3, await db.WebsiteMediaAssets.CountAsync(a => a.AgentWebsiteId == site.Id));
        Assert.True((await BuilderImportService.ApplyAsync(db, await db.WebsiteBuilderImports.SingleAsync(i => i.Id == second.Id), new HashSet<int>(), null, "bahman")).Ok);
    }

    [Fact]
    public void The_table_is_created_at_start_up_by_both_apps_and_goes_with_the_adviser()
    {
        var repair = File.ReadAllText(Path.Combine(RepoRoot(), "src", "IPRO.DataAccess", "StartupSchemaRepair.cs"));
        Assert.Contains("CREATE TABLE IF NOT EXISTS `WebsiteBuilderImports`", repair);
        Assert.Contains("ADD COLUMN `ContentSha256`", repair);
        foreach (var app in new[] { "IPRO.Web", "IPRO.Admin" })
        {
            Assert.Contains("StartupSchemaRepair.EnsureWebsiteBuilderImportSchemaAsync(db)", File.ReadAllText(Path.Combine(RepoRoot(), "src", app, "Program.cs")));
        }
        Assert.Contains("WebsiteBuilderImports", AgentDataEraser.CoveredTables);
    }

    [Fact]
    public async Task The_table_the_repair_creates_is_the_table_the_model_expects()
    {
        // The test database is built from the model; production's is built by the start-up repair.
        // Dropping the table and the column and running the repair proves the two agree: the full
        // round of store, bring in and put back then runs on the repaired schema.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE `WebsiteBuilderImports`");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE `WebsiteMediaAssets` DROP COLUMN `ContentSha256`");
        await StartupSchemaRepair.EnsureWebsiteBuilderImportSchemaAsync(db);
        await StartupSchemaRepair.EnsureWebsiteBuilderImportSchemaAsync(db);   // and it can run again

        var site = await SeedSiteAsync(db);
        var package = BuilderPackageReader.Read(Zip(Designer()), await BuilderImportService.ContextAsync(db, site, 0));
        var import = await BuilderImportService.StoreAsync(db, new MemoryBlob(), site, package, "site.zip", "bahman");
        Assert.True((await BuilderImportService.ApplyAsync(db, import, new HashSet<int>(), null, "bahman")).Ok);
        Assert.True((await BuilderImportService.RestoreAsync(db, import)).Ok);
    }

    // ---------------- helpers ----------------

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    internal static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(RepoRoot(), "tests", "IPRO.IntegrationTests", "Fixtures", "builder", name));

    // The hand-over the return fixture answers (contract section 8): its id, the types it offered and
    // the ids in its catalogue, read from the hand-over fixture itself.
    private static BuilderImportContext FixtureHandover()
    {
        using var zip = new ZipArchive(new MemoryStream(Fixture("ipro-v0.4-handover-fixture.zip")), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("handover.json")!.Open());
        var handover = JsonNode.Parse(reader.ReadToEnd())!;
        return new BuilderImportContext(
            handover["handoverId"]!.GetValue<string>(),
            handover["allowedBlockTypes"]!.AsArray().Select(t => t!.GetValue<string>()).ToHashSet(),
            handover["catalog"]!["articles"]!.AsArray().Select(a => a!["id"]!.GetValue<int>()).ToHashSet(),
            handover["catalog"]!["forms"]!.AsArray().Select(f => f!["id"]!.GetValue<int>()).ToHashSet(),
            new HashSet<string>(),
            handover["uploads"]!["remainingBytes"]!.GetValue<long>());
    }

    // The return fixture, opened: its template.json as a tree to change, and its files.
    internal static (JsonObject Template, List<(string Name, byte[] Bytes)> Files) Returned()
    {
        using var zip = new ZipArchive(new MemoryStream(Fixture("ipro-v0.4-return-fixture.zip")), ZipArchiveMode.Read);
        JsonObject? template = null;
        var files = new List<(string, byte[])>();
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            if (entry.FullName == "template.json") template = JsonNode.Parse(Encoding.UTF8.GetString(copy.ToArray()))!.AsObject();
            else files.Add((entry.FullName, copy.ToArray()));
        }
        return (template!, files);
    }

    // The same site as one built from scratch in the builder: no hand-over, so without the two
    // sections that point at a customer's own article and form.
    internal static (JsonObject Template, List<(string Name, byte[] Bytes)> Files) Designer()
    {
        var (template, files) = Returned();
        template["handoverId"] = "";
        foreach (var page in template["pages"]!.AsArray())
        {
            var blocks = page!["blocks"]!.AsArray();
            foreach (var block in blocks.Where(b => b!["type"]!.GetValue<string>() is "ArticleContent" or "Form").ToList()) blocks.Remove(block);
        }
        return (template, files);
    }

    internal static BuilderPackage DesignerPackage()
    {
        var package = BuilderPackageReader.Read(Zip(Designer()), BuilderImportContext.DesignerTemplate());
        Assert.True(package.IsValid, Listed(package));
        return package;
    }

    private static Dictionary<string, string> Urls(BuilderPackage package) =>
        package.Images.Keys.ToDictionary(k => k, k => "https://blob.example.test/website-media/" + k[7..]);

    private static JsonObject Page(JsonObject template, string slug) =>
        template["pages"]!.AsArray().Single(p => p!["slug"]!.GetValue<string>() == slug)!.AsObject();

    private static JsonObject Block(JsonObject template, string slug, int index) =>
        Page(template, slug)["blocks"]!.AsArray()[index]!.AsObject();

    private static void AddPage(JsonObject template, string slug, string? parent)
    {
        template["pages"]!.AsArray().Add(new JsonObject
        {
            ["slug"] = slug, ["parentSlug"] = parent, ["title"] = slug, ["navigationLabel"] = slug, ["isHomePage"] = false, ["sortOrder"] = 0,
            ["blocks"] = new JsonArray(NewBlock("Text", 0, new JsonObject(), body: "Some words."))
        });
    }

    private static void AddBlock(JsonObject template, string slug, string type, JsonObject settings)
    {
        var blocks = Page(template, slug)["blocks"]!.AsArray();
        blocks.Add(NewBlock(type, blocks.Count, settings));
    }

    private static JsonObject NewBlock(string type, int sortOrder, JsonObject settings, string body = "") => new()
    {
        ["type"] = type, ["heading"] = "", ["subheading"] = "", ["body"] = body, ["imageRef"] = "", ["buttonText"] = "", ["buttonUrl"] = "",
        ["layoutVariant"] = "", ["sortOrder"] = sortOrder, ["isVisible"] = true, ["settings"] = settings
    };

    internal static byte[] Zip((JsonObject Template, List<(string Name, byte[] Bytes)> Files) site) => Zip(site.Template, site.Files);

    internal static byte[] Zip(JsonObject template, IEnumerable<(string Name, byte[] Bytes)> files) => Zip(template.ToJsonString(), files);

    // A package: template.json (null = none) and the files given, in a ZIP.
    internal static byte[] Zip(string? templateJson, IEnumerable<(string Name, byte[] Bytes)> files, bool bom = false, string[]? folders = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var folder in folders ?? Array.Empty<string>()) zip.CreateEntry(folder);
            if (templateJson != null)
            {
                var bytes = new UTF8Encoding(false).GetBytes(templateJson);
                Write(zip, "template.json", bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray() : bytes);
            }
            foreach (var (name, bytes) in files) Write(zip, name, bytes);
        }
        return buffer.ToArray();
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    // A file that starts as a PNG does; the seed makes two of them differ.
    private static byte[] Png(byte seed = 0) => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, seed, seed, 1, 2 };

    private static string Listed(BuilderPackage package) =>
        "problems:\n" + string.Join("\n", package.Problems.Select(p => $"[{p.Rule}] {p.Where}: {p.Message}"));

    private static void Refused(string what, Action<JsonObject> change, string rule, string says)
    {
        var (template, files) = Designer();
        change(template);
        Expect(what, BuilderPackageReader.Read(Zip(template, files), BuilderImportContext.DesignerTemplate()), rule, says);
    }

    private static void RefusedReturn(string what, Action<JsonObject> change, string rule, string says)
    {
        var (template, files) = Returned();
        change(template);
        Expect(what, BuilderPackageReader.Read(Zip(template, files), FixtureHandover()), rule, says);
    }

    private static void RefusedZip(string what, byte[] zip, string rule, string says) =>
        Expect(what, BuilderPackageReader.Read(zip, BuilderImportContext.DesignerTemplate()), rule, says);

    private static void Expect(string what, BuilderPackage package, string rule, string says)
    {
        Assert.False(package.IsValid, $"{what}: the package was taken");
        Assert.Null(package.Template);
        Assert.True(package.Problems.Any(p => p.Rule == rule && p.Message.Contains(says, StringComparison.Ordinal)),
            $"{what}: expected a problem under section {rule} saying \"{says}\".\n{Listed(package)}");
    }

    private static void Taken(string what, Action<JsonObject> change)
    {
        var (template, files) = Designer();
        change(template);
        var package = BuilderPackageReader.Read(Zip(template, files), BuilderImportContext.DesignerTemplate());
        Assert.True(package.IsValid, $"{what}: the package was refused.\n{Listed(package)}");
    }

    private static WebsitePage ExistingPage(int id, string slug, string title, bool home, int order, params string[] blockTypes) => new()
    {
        Id = id, AgentWebsiteId = 9, Slug = slug, Title = title, NavigationLabel = title, IsHomePage = home, SortOrder = order,
        ShowInNavigation = true, IsPublished = true,
        Blocks = blockTypes.Select((type, i) => new WebsiteContentBlock { Id = id * 100 + i, WebsitePageId = id, BlockType = type, Heading = title, SortOrder = i, IsVisible = true }).ToList()
    };

    // A live site as a customer has it: a home page with search settings and a page the builder
    // cannot carry (a price list).
    internal static async Task<AgentWebsite> SeedSiteAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"bi-{Guid.NewGuid():N}"[..20], Email = "builder-import@example.com", FirstName = "Bake", LastName = "House",
            CompanyName = "Sample Bakery", DomainName = $"bi-{Guid.NewGuid():N}"[..24], PackageId = rule.Id
        };
        var template = await db.WebsiteTemplates.FirstOrDefaultAsync(t => t.TemplateKey == "modern-professional")
                       ?? new WebsiteTemplate { TemplateKey = "modern-professional", Name = "Modern", IsActive = true, IsDefault = true };
        db.Add(agent);
        if (template.Id == 0) db.Add(template);
        await db.SaveChangesAsync();
        var site = new AgentWebsite
        {
            AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true, SiteTitle = "Sample Bakery",
            LogoUrl = "https://blob.example.test/agent-logos/old-logo.png", CustomDomain = $"bi-{Guid.NewGuid():N}"[..14] + ".example.test"
        };
        db.Add(site);
        await db.SaveChangesAsync();
        var home = new WebsitePage
        {
            AgentWebsiteId = site.Id, Title = "Home", Slug = "home", NavigationLabel = "Home", IsHomePage = true, SortOrder = 0,
            MetaTitle = "Best bakery in town", MetaDescription = "Fresh bread every morning.", OldAddresses = "index.html"
        };
        var menu = new WebsitePage { AgentWebsiteId = site.Id, Title = "Menu", Slug = "menu", NavigationLabel = "Menu", SortOrder = 1 };
        db.AddRange(home, menu);
        await db.SaveChangesAsync();
        db.AddRange(
            new WebsiteContentBlock { WebsitePageId = home.Id, BlockType = WebsiteBlockTypes.Hero, Heading = "Welcome", Body = "Line one\r\nLine two", SortOrder = 0, SettingsJson = new WebsiteHeroSettings { Layout = "centered" }.ToJson() },
            new WebsiteContentBlock { WebsitePageId = home.Id, BlockType = WebsiteBlockTypes.Reviews, Heading = "Reviews", SortOrder = 1, LayoutVariant = "badge", IsVisible = false },
            new WebsiteContentBlock { WebsitePageId = menu.Id, BlockType = WebsiteBlockTypes.PriceList, Heading = "Our menu", SortOrder = 0, SettingsJson = "{\"Kind\":\"menu\"}" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.Id == site.Id);
    }

    private sealed record SiteState(AgentWebsite Site, List<WebsitePage> Pages);

    private static async Task<SiteState> SiteAsync(IPRODbContext db, int siteId) => new(
        await db.AgentWebsites.AsNoTracking().SingleAsync(w => w.Id == siteId),
        await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == siteId).Include(p => p.Blocks).OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync());

    // Everything about a site a visitor or the owner could tell apart, as text.
    private static string Describe(SiteState state) => state.Site.LogoUrl + "|" + state.Site.TemplateId + "\n" + string.Join("\n", state.Pages.Select(p =>
        $"{p.Slug}|{p.Title}|{p.NavigationLabel}|{p.MetaTitle}|{p.MetaDescription}|{p.OldAddresses}|{p.IsHomePage}|{p.ShowInNavigation}|{p.IsPublished}|{p.SortOrder}|parent={state.Pages.FirstOrDefault(x => x.Id == p.ParentPageId)?.Slug}\n" +
        string.Join("\n", p.Blocks.OrderBy(b => b.SortOrder).Select(b =>
            $"   {b.BlockType}|{b.Heading}|{b.Subheading}|{b.Body}|{b.ImageUrl}|{b.ButtonText}|{b.ButtonUrl}|{b.SettingsJson}|{b.LayoutVariant}|{b.SortOrder}|{b.IsVisible}"))));

    internal sealed class MemoryBlob : IBlobStorageService
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public readonly List<string> Deleted = new();

        public async Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate)
        {
            using var copy = new MemoryStream();
            await fileStream.CopyToAsync(copy);
            var url = $"https://blob.example.test/{containerName}/{Guid.NewGuid():N}_{fileName}";
            Files[url] = copy.ToArray();
            return url;
        }

        public Task<bool> DeleteAsync(string blobUrl)
        {
            Deleted.Add(blobUrl);
            return Task.FromResult(Files.Remove(blobUrl));
        }

        public Task<Stream?> DownloadAsync(string blobUrl) =>
            Task.FromResult<Stream?>(Files.TryGetValue(blobUrl, out var bytes) ? new MemoryStream(bytes) : null);

        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(Files.Keys.ToList());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blob.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }
}
