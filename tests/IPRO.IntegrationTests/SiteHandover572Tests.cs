using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using IPRO.Business.SiteImport;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// 572 (2026-10-12): iPro hands a customer's site to the Template Builder (export contract v0.4,
// section 9). 571 could take in only a site built from scratch in the builder, because iPro had no
// way to hand an existing site over. This is that way: the package the builder opens.
//
// The yardstick is the contract's own fixture: a site shaped like the one the hand-over fixture
// describes must come out as that fixture's template.json and a handover.json of the same shape.
// And what iPro hands over must be something iPro takes back.
public class SiteHandover572Tests
{
    [Fact]
    public async Task A_site_like_the_fixtures_is_handed_over_as_the_fixture_describes_it()
    {
        var fixture = FixtureSite.Load();

        var handover = await SiteHandoverBuilder.BuildAsync(fixture.Input(), fixture.Fetch);

        Assert.True(handover.CanHandOver, string.Join("\n", handover.Blockers));
        Assert.Empty(handover.Notes);
        Assert.Matches(BuilderContract.HandoverId, handover.HandoverId);
        Assert.Equal("sample-planning-group", handover.TemplateName);
        Assert.StartsWith("ipro-handover-sample-planning-group-", handover.FileName);

        // template.json: the fixture's, once the two things that are new each time are set aside
        // (the hand-over's id and the names iPro gives the image files).
        var entries = Entries(handover.Zip);
        var template = JsonNode.Parse(Encoding.UTF8.GetString(entries["template.json"]))!.AsObject();
        var shaOfRef = entries.Where(e => e.Key.StartsWith("assets/")).ToDictionary(e => e.Key, e => Sha(e.Value));
        var fixtureRefOfSha = fixture.Files.Where(f => f.Key.StartsWith("assets/")).ToDictionary(f => Sha(f.Value), f => f.Key);
        string Named(string assetRef) => assetRef.Length == 0 ? assetRef : fixtureRefOfSha[shaOfRef[assetRef]];
        Assert.Equal(handover.HandoverId, template["handoverId"]!.GetValue<string>());
        template["handoverId"] = fixture.Template["handoverId"]!.GetValue<string>();
        template["logoRef"] = Named(template["logoRef"]!.GetValue<string>());
        foreach (var block in template["pages"]!.AsArray().SelectMany(p => p!["blocks"]!.AsArray()))
        {
            block!["imageRef"] = Named(block["imageRef"]!.GetValue<string>());
        }
        template["assets"] = new JsonArray(template["assets"]!.AsArray()
            .Select(a => Named(a!["ref"]!.GetValue<string>())).OrderBy(r => r, StringComparer.Ordinal)
            .Select(r => (JsonNode)new JsonObject { ["ref"] = r, ["type"] = "image/png" }).ToArray());
        Assert.True(JsonNode.DeepEquals(fixture.Template, template),
            "template.json differs from the fixture's.\nfixture: " + fixture.Template.ToJsonString() + "\nmade:    " + template.ToJsonString());

        // handover.json: the same keys at every level, and the same catalogue.
        var made = JsonNode.Parse(Encoding.UTF8.GetString(entries["handover.json"]))!.AsObject();
        Assert.Equal(Shape(fixture.Handover), Shape(made));
        Assert.Equal("1.0", made["handoverVersion"]!.GetValue<string>());
        Assert.Equal("0.4", made["contractVersion"]!.GetValue<string>());
        Assert.Equal(handover.HandoverId, made["handoverId"]!.GetValue<string>());
        Assert.Equal(64, made["sessionToken"]!.GetValue<string>().Length);
        Assert.Equal(handover.SessionToken, made["sessionToken"]!.GetValue<string>());
        Assert.Equal("2026-10-12T15:00:00Z", made["issuedAt"]!.GetValue<string>());
        Assert.Equal("2026-10-13T03:00:00Z", made["sessionExpiresAt"]!.GetValue<string>());   // twelve hours
        Assert.Equal("Sample Planning Group", made["adviser"]!["businessName"]!.GetValue<string>());
        Assert.Equal("advisers", made["adviser"]!["vertical"]!.GetValue<string>());
        Assert.Equal(BuilderContract.BlockTypes, made["allowedBlockTypes"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(8388608, made["uploads"]!["maxFileBytes"]!.GetValue<long>());
        Assert.Equal(52428800, made["uploads"]!["remainingBytes"]!.GetValue<long>());
        var catalogue = made["catalog"]!.AsObject();
        var picture = catalogue["articles"]![0]!["imageRef"]!.GetValue<string>();
        Assert.StartsWith("catalog/", picture);
        Assert.Equal(Sha(fixture.Files["catalog/renewal-checklist.png"]), Sha(entries[picture]));
        catalogue["articles"]![0]!["imageRef"] = "catalog/renewal-checklist.png";
        Assert.True(JsonNode.DeepEquals(fixture.Handover["catalog"], catalogue),
            "the catalogue differs from the fixture's.\nfixture: " + fixture.Handover["catalog"]!.ToJsonString() + "\nmade:    " + catalogue.ToJsonString());

        // The package: the four kinds of entry section 9.1 names and nothing else; plain UTF-8 text
        // with LF line endings and a final newline.
        Assert.Equal(new[] { "assets", "catalog", "handover.json", "template.json" },
            entries.Keys.Select(k => k.Split('/')[0]).Distinct().OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(3, entries.Keys.Count(k => k.StartsWith("assets/")));
        Assert.Equal(1, entries.Keys.Count(k => k.StartsWith("catalog/")));
        foreach (var name in new[] { "handover.json", "template.json" })
        {
            Assert.NotEqual(0xEF, entries[name][0]);
            var text = Encoding.UTF8.GetString(entries[name]);
            Assert.DoesNotContain("\r", text);
            Assert.EndsWith("}\n", text);
        }
        Assert.All(entries.Keys.Where(k => k.Contains('/')), k =>
        {
            var file = k[(k.IndexOf('/') + 1)..];
            Assert.Matches(BuilderContract.AssetFileName, file);
            Assert.True(file.Length <= BuilderContract.MaxFileNameLength);
        });

        // What iPro keeps of it.
        Assert.Equal(3, handover.Delivered.Count);
        Assert.All(handover.Delivered, d => Assert.Contains("/website-media/", d.Url.Replace("/agent-logos/", "/website-media/")));
        Assert.Equal(new[] { "home", "about", "contact" }, handover.Pages.Select(p => p.Slug));
        Assert.Equal(new[] { 101, 102 }, handover.ArticleIds);
        Assert.Equal(new[] { 45 }, handover.FormIds);
    }

    [Fact]
    public async Task What_iPro_hands_over_iPro_takes_back_unchanged_or_edited_and_its_pictures_cost_nothing()
    {
        var fixture = FixtureSite.Load();
        var handover = await SiteHandoverBuilder.BuildAsync(fixture.Input(), fixture.Fetch);
        var context = new BuilderImportContext(handover.HandoverId, handover.AllowedBlockTypes.ToHashSet(), handover.ArticleIds.ToHashSet(),
            handover.FormIds.ToHashSet(), handover.Delivered.Select(d => d.Sha256).ToHashSet(), RemainingBytes: 0);
        var entries = Entries(handover.Zip);
        List<(string, byte[])> AsReturn(string json) =>
            entries.Where(e => e.Key.StartsWith("assets/")).Select(e => (e.Key, e.Value)).Append(("template.json", Encoding.UTF8.GetBytes(json))).ToList();

        var untouched = BuilderPackageReader.Read(BuilderImport571Tests.Zip((string?)null, AsReturn(Encoding.UTF8.GetString(entries["template.json"]))), context);
        Assert.True(untouched.IsValid, string.Join("\n", untouched.Problems.Select(p => p.Message)));
        Assert.Empty(untouched.NewImages(context.KnownImageHashes));   // no storage left, and none needed

        var edited = JsonNode.Parse(Encoding.UTF8.GetString(entries["template.json"]))!;
        edited["pages"]![0]!["blocks"]![0]!["heading"] = "Plan with confidence, at every stage";
        var changed = BuilderPackageReader.Read(BuilderImport571Tests.Zip((string?)null, AsReturn(edited.ToJsonString())), context);
        Assert.True(changed.IsValid, string.Join("\n", changed.Problems.Select(p => p.Message)));

        // Without this hand-over's context the same package is refused: it belongs to a hand-over.
        Assert.False(BuilderPackageReader.Read(BuilderImport571Tests.Zip((string?)null, AsReturn(edited.ToJsonString())), BuilderImportContext.DesignerTemplate()).IsValid);
    }

    [Fact]
    public async Task A_site_that_does_not_fit_is_not_handed_over_and_every_reason_is_named()
    {
        var fixture = FixtureSite.Load();
        var pages = fixture.Pages;
        // A section the builder does not have.
        pages[1].Blocks.Add(new WebsiteContentBlock { Id = 901, WebsitePageId = pages[1].Id, BlockType = WebsiteBlockTypes.Maps, SortOrder = 9, IsVisible = true });
        // A page that only shows the design's standard layout.
        pages.Add(new WebsitePage { Id = 77, AgentWebsiteId = 9, Slug = "team", Title = "Our team", NavigationLabel = "Team", SortOrder = 7 });
        // A field longer than the contract carries, a picture that is gone, a picture that is not one.
        pages[0].Blocks.First().Heading = new string('a', 230);
        pages[0].Blocks.First().ImageUrl = "https://store.blob.core.windows.net/website-media/0123456789abcdef0123456789abcdef_gone.png";
        pages[2].Blocks.Add(new WebsiteContentBlock { Id = 902, WebsitePageId = pages[2].Id, BlockType = WebsiteBlockTypes.Text, Heading = "Find us", SortOrder = 8, IsVisible = true,
            ImageUrl = "https://store.blob.core.windows.net/website-media/0123456789abcdef0123456789abcdef_drawing.svg" });
        fixture.Images["https://store.blob.core.windows.net/website-media/0123456789abcdef0123456789abcdef_drawing.svg"] = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>");

        var refused = await SiteHandoverBuilder.BuildAsync(fixture.Input(), fixture.Fetch);

        Assert.False(refused.CanHandOver);
        Assert.Empty(refused.Zip);
        Assert.Contains(refused.Blockers, b => b.Contains("\"About\" has sections the builder cannot carry yet: Map."));
        Assert.Contains(refused.Blockers, b => b.Contains("\"Our team\" has no sections of its own"));
        Assert.Contains(refused.Blockers, b => b.Contains("could not be read from iPro's storage (0123456789abcdef0123456789abcdef_gone.png)"));
        Assert.Contains(refused.Blockers, b => b.Contains("is not a JPG, PNG, GIF or WebP image"));

        // The rules that are the contract's own come from the same door a returning package uses.
        var second = FixtureSite.Load();
        second.Pages[0].Blocks.First().Heading = new string('a', 230);
        second.Pages[2].Blocks.Single(b => b.BlockType == WebsiteBlockTypes.Form).SettingsJson = new WebsiteFormSettings { WebsiteFormId = 7 }.ToJson();
        var byTheDoor = await SiteHandoverBuilder.BuildAsync(second.Input(), second.Fetch);
        Assert.False(byTheDoor.CanHandOver);
        Assert.Contains(byTheDoor.Blockers, b => b.Contains("Page \"home\", section 0") && b.Contains("heading is 230 characters"));
        Assert.Contains(byTheDoor.Blockers, b => b.Contains("Form 7 is not one of this customer's active forms"));

        // A section the customer's package no longer includes.
        var third = FixtureSite.Load();
        var withoutBlog = await SiteHandoverBuilder.BuildAsync(
            third.Input() with { AllowedBlockTypes = SiteHandoverService.AllowedBlockTypes(articles: true, blog: false, customForms: true) }, third.Fetch);
        Assert.Contains(withoutBlog.Blockers, b => b.Contains("\"Home\" has sections this customer's package no longer includes: Blog."));

        // A picture over 8 MB.
        var fourth = FixtureSite.Load();
        var heroUrl = fourth.Pages[0].Blocks.First().ImageUrl;
        fourth.Images[heroUrl] = fourth.Images[heroUrl].Concat(new byte[(int)BuilderContract.MaxAssetBytes]).ToArray();
        Assert.Contains((await SiteHandoverBuilder.BuildAsync(fourth.Input(), fourth.Fetch)).Blockers, b => b.Contains("is larger than 8 MB"));

        // No pages at all.
        var empty = FixtureSite.Load();
        empty.Pages.Clear();
        Assert.Equal(new[] { "The site has no pages yet." }, (await SiteHandoverBuilder.BuildAsync(empty.Input(), empty.Fetch)).Blockers);
    }

    [Fact]
    public async Task What_is_tidied_on_the_way_is_said_and_what_a_type_does_not_use_is_left_empty()
    {
        var fixture = FixtureSite.Load();
        var about = fixture.Pages[1];
        var text = about.Blocks.First(b => b.BlockType == WebsiteBlockTypes.Text);
        text.ButtonText = "Read more";                 // words and no link: on the live site it leads nowhere
        text.Body = "First line\r\nSecond line";       // as a browser's text box sends it
        var services = fixture.Pages[0].Blocks.Single(b => b.BlockType == WebsiteBlockTypes.Services);
        services.ButtonText = "Left over";             // a Services section draws no button and no picture
        services.ImageUrl = "https://store.blob.core.windows.net/website-media/0123456789abcdef0123456789abcdef_never-fetched.png";
        services.LayoutVariant = "CARDS ";             // the live site reads this as "cards"
        about.NavigationLabel = "";                    // the menu then shows the title
        fixture.Pages[2].SortOrder = about.SortOrder;  // two pages side by side with one order

        var handover = await SiteHandoverBuilder.BuildAsync(fixture.Input(), fixture.Fetch);

        Assert.True(handover.CanHandOver, string.Join("\n", handover.Blockers));
        Assert.Equal(new[] { "\"About\": the button \"Read more\" has no link, so it is left out." }, handover.Notes);
        var template = JsonNode.Parse(handover.TemplateJson)!;
        var pages = template["pages"]!.AsArray();
        var aboutOut = pages.Single(p => p!["slug"]!.GetValue<string>() == "about")!;
        var textOut = aboutOut["blocks"]![0]!;
        Assert.Equal("", textOut["buttonText"]!.GetValue<string>());
        Assert.Equal("", textOut["buttonUrl"]!.GetValue<string>());
        Assert.Equal("First line\nSecond line", textOut["body"]!.GetValue<string>());
        Assert.Equal("About", aboutOut["navigationLabel"]!.GetValue<string>());
        var servicesOut = pages[0]!["blocks"]!.AsArray().Single(b => b!["type"]!.GetValue<string>() == "Services")!;
        Assert.Equal("", servicesOut["buttonText"]!.GetValue<string>());
        Assert.Equal("", servicesOut["imageRef"]!.GetValue<string>());
        Assert.Equal("cards", servicesOut["layoutVariant"]!.GetValue<string>());
        Assert.Equal(new[] { 0, 1, 2 }, pages.Select(p => p!["sortOrder"]!.GetValue<int>()).OrderBy(o => o));
        Assert.Equal(3, handover.Delivered.Count);   // the picture nothing draws was never fetched
    }

    [Fact]
    public async Task The_catalogue_sends_pictures_for_the_newest_articles_only_and_the_same_picture_once()
    {
        var fixture = FixtureSite.Load();
        var picture = fixture.Files["catalog/renewal-checklist.png"];
        fixture.Articles.Clear();
        for (var i = 1; i <= 20; i++)
        {
            var url = $"https://store.blob.core.windows.net/article-media/{i:x32}_cover-{i}.png";
            fixture.Images[url] = picture.Append((byte)i).ToArray();   // twenty different files
            fixture.Articles.Add(new Article { Id = 200 + i, Title = $"Article {i}", Summary = "  Two\r\n lines  ", ImageUrl = url, IsPublished = true, PublishedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-i) });
        }

        var handover = await SiteHandoverBuilder.BuildAsync(fixture.Input(), fixture.Fetch);

        Assert.True(handover.CanHandOver, string.Join("\n", handover.Blockers));
        var entries = Entries(handover.Zip);
        Assert.Equal(SiteHandoverBuilder.CatalogueImagesMax, entries.Keys.Count(k => k.StartsWith("catalog/")));
        var articles = JsonNode.Parse(handover.HandoverJson)!["catalog"]!["articles"]!.AsArray();
        Assert.Equal(20, articles.Count);
        Assert.Equal(201, articles[0]!["id"]!.GetValue<int>());                       // newest first
        Assert.Equal("Two lines", articles[0]!["summary"]!.GetValue<string>());       // one line of plain text
        Assert.Equal(12, articles.Count(a => a!["imageRef"]!.GetValue<string>().Length > 0));
        Assert.All(articles.Skip(12), a => Assert.Equal("", a!["imageRef"]!.GetValue<string>()));
        // Every catalogue file is named by exactly one article, and every named file is there.
        Assert.Equal(entries.Keys.Where(k => k.StartsWith("catalog/")).OrderBy(k => k),
            articles.Select(a => a!["imageRef"]!.GetValue<string>()).Where(r => r.Length > 0).OrderBy(r => r));
    }

    [Fact]
    public void The_small_mappings()
    {
        Assert.Equal("modern", SiteHandoverBuilder.ThemeFor("modern-professional"));
        Assert.Equal("classic", SiteHandoverBuilder.ThemeFor("classic-sidebar"));
        Assert.Equal("editorial", SiteHandoverBuilder.ThemeFor("editorial-visual"));
        Assert.Equal("modern", SiteHandoverBuilder.ThemeFor(null));

        Assert.Equal("accountants", SiteHandoverBuilder.VerticalFor("Accountants"));
        Assert.Equal("mortgage", SiteHandoverBuilder.VerticalFor("Mortgage"));
        Assert.Equal("advisers", SiteHandoverBuilder.VerticalFor("Insurance / Financial"));
        // The contract's v0.4 has no value for any other kind of business yet.
        Assert.Equal("advisers", SiteHandoverBuilder.VerticalFor("Generic"));

        Assert.Equal("l-avenue-boulangerie", SiteHandoverBuilder.TemplateNameFor("L'Avenue Boulangerie", 4));
        Assert.Equal("site-4", SiteHandoverBuilder.TemplateNameFor("!!", 4));
        Assert.True(SiteHandoverBuilder.TemplateNameFor(new string('a', 90), 4).Length <= 60);

        var name = SiteHandoverBuilder.ImageFileName(
            "https://store.blob.core.windows.net/website-media/0123456789abcdef0123456789abcdef_Team%20Photo%20(final).JPG?v=2", new string('c', 64), "jpg");
        Assert.Equal("team-photo-final-cccccccc.jpg", name);
        Assert.Matches(BuilderContract.AssetFileName, name);
        Assert.Equal("image-cccccccc.png", SiteHandoverBuilder.ImageFileName("https://store.blob.core.windows.net/website-media/", new string('c', 64), "png"));
        Assert.True(SiteHandoverBuilder.ImageFileName("https://x/website-media/" + new string('z', 300) + ".png", new string('c', 64), "png").Length <= 100);

        Assert.Equal("png", SiteHandoverBuilder.ExtensionFor(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }));
        Assert.Equal("jpg", SiteHandoverBuilder.ExtensionFor(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0 }));
        Assert.Equal("", SiteHandoverBuilder.ExtensionFor(Encoding.ASCII.GetBytes("%PDF-1.7 not an image")));

        Assert.Equal(5, SiteHandoverService.AllowedBlockTypes(false, false, false).Count);
        Assert.Equal(8, SiteHandoverService.AllowedBlockTypes(true, true, true).Count);
        Assert.DoesNotContain(WebsiteBlockTypes.Form, SiteHandoverService.AllowedBlockTypes(true, true, false));
    }

    [Fact]
    public void Only_a_picture_in_a_public_image_container_is_read()
    {
        // A section's image address is text a customer can type. The private containers share the
        // storage account, so the address must name a public image container, in the place the
        // storage layer will read it from.
        Assert.True(SiteHandoverService.IsSiteImageAddress("https://iprostorageprod.blob.core.windows.net/website-media/0123456789abcdef0123456789abcdef_hero.png"));
        Assert.True(SiteHandoverService.IsSiteImageAddress("https://iprostorageprod.blob.core.windows.net/agent-logos/0123456789abcdef0123456789abcdef_logo.png"));
        Assert.True(SiteHandoverService.IsSiteImageAddress("https://iprostorageprod.blob.core.windows.net/starter-content/0123456789abcdef0123456789abcdef_office.jpg"));
        Assert.True(SiteHandoverService.IsSiteImageAddress("http://127.0.0.1:10000/devstoreaccount1/website-media/0123456789abcdef0123456789abcdef_hero.png"));

        foreach (var no in new[]
        {
            "", "   ", "/images/hero.png", "images/hero.png", "ftp://host/website-media/x.png", "javascript:alert(1)",
            "https://iprostorageprod.blob.core.windows.net/agent-documents/0123456789abcdef0123456789abcdef_statement.png",
            "https://iprostorageprod.blob.core.windows.net/portal-documents/x.png",
            "https://iprostorageprod.blob.core.windows.net/agent-documents/website-media/x.png",
            "https://iprostorageprod.blob.core.windows.net/website-media/../agent-documents/x.png",
            "https://iprostorageprod.blob.core.windows.net/website-media/%2e%2e/agent-documents/x.png",
            "https://iprostorageprod.blob.core.windows.net/website-media/%252e%252e/agent-documents/x.png",
            "https://iprostorageprod.blob.core.windows.net/website-media/",
            "https://iprostorageprod.blob.core.windows.net/website-media"
        })
        {
            Assert.False(SiteHandoverService.IsSiteImageAddress(no), $"'{no}' was taken for a site picture");
        }
    }

    // ---------------- helpers ----------------

    private static Dictionary<string, byte[]> Entries(byte[] zip)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            entries[entry.FullName] = copy.ToArray();
        }
        return entries;
    }

    private static string Sha(byte[] bytes) => BuilderPackageReader.Hex(System.Security.Cryptography.SHA256.HashData(bytes));

    // The keys of a JSON value at every level (a list by its first element), so two documents can be
    // compared for shape while their values differ.
    private static string Shape(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + Shape(p.Value))) + "}",
        JsonArray a => "[" + (a.Count == 0 ? "" : Shape(a[0])) + "]",
        JsonValue v => v.GetValueKind().ToString(),
        _ => "null"
    };

    // A customer's site, articles and forms as rows, made FROM the contract's hand-over fixture: the
    // site that fixture says iPro handed over.
    internal sealed class FixtureSite
    {
        public JsonObject Template = null!;
        public JsonObject Handover = null!;
        public Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        public List<WebsitePage> Pages = new();
        public List<Article> Articles = new();
        public List<HandoverForm> Forms = new();
        public Dictionary<string, byte[]> Images = new(StringComparer.Ordinal);
        public string LogoUrl = string.Empty;

        public Task<byte[]?> Fetch(string url) => Task.FromResult(Images.TryGetValue(url, out var bytes) ? bytes : null);

        public SiteHandoverBuilder.Input Input() => new(
            new AgentWebsite { Id = 9, AgentUserId = 4, LogoUrl = LogoUrl },
            "Sample Planning Group", "Insurance / Financial", "modern-professional",
            Pages, new HashSet<string>(BuilderContract.BlockTypes), Articles, Forms,
            RemainingBytes: 52428800, UtcNow: new DateTime(2026, 10, 12, 15, 0, 0, 500, DateTimeKind.Utc));

        public static FixtureSite Load()
        {
            var site = new FixtureSite();
            using (var zip = new ZipArchive(new MemoryStream(BuilderImport571Tests.Fixture("ipro-v0.4-handover-fixture.zip")), ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    using var stream = entry.Open();
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    site.Files[entry.FullName] = copy.ToArray();
                }
            }
            site.Template = JsonNode.Parse(Encoding.UTF8.GetString(site.Files["template.json"]))!.AsObject();
            site.Handover = JsonNode.Parse(Encoding.UTF8.GetString(site.Files["handover.json"]))!.AsObject();

            // Each image as iPro stores it: "<32 hex>_<name>" in a public container.
            var n = 0;
            string Stored(string packageRef, string container)
            {
                var url = $"https://store.blob.core.windows.net/{container}/{++n:x32}_{packageRef[(packageRef.IndexOf('/') + 1)..]}";
                site.Images[url] = site.Files[packageRef];
                return url;
            }
            var urlOfRef = new Dictionary<string, string>(StringComparer.Ordinal);
            string Url(string packageRef) => packageRef.Length == 0 ? string.Empty
                : urlOfRef.TryGetValue(packageRef, out var url) ? url
                : urlOfRef[packageRef] = Stored(packageRef, packageRef == site.Template["logoRef"]!.GetValue<string>() ? "agent-logos" : "website-media");
            site.LogoUrl = Url(site.Template["logoRef"]!.GetValue<string>());

            var pageId = 10;
            var blockId = 100;
            var idOfSlug = new Dictionary<string, int>();
            foreach (var page in site.Template["pages"]!.AsArray())
            {
                var row = new WebsitePage
                {
                    Id = ++pageId, AgentWebsiteId = 9,
                    Slug = page!["slug"]!.GetValue<string>(), Title = page["title"]!.GetValue<string>(),
                    NavigationLabel = page["navigationLabel"]!.GetValue<string>(), IsHomePage = page["isHomePage"]!.GetValue<bool>(),
                    // Stored orders have gaps and are in tens; the package numbers them 0, 1, 2.
                    SortOrder = page["sortOrder"]!.GetValue<int>() * 10 + 5
                };
                idOfSlug[row.Slug] = row.Id;
                foreach (var block in page["blocks"]!.AsArray())
                {
                    var type = block!["type"]!.GetValue<string>();
                    var settings = block["settings"]!;
                    row.Blocks.Add(new WebsiteContentBlock
                    {
                        Id = ++blockId, WebsitePageId = row.Id, BlockType = type,
                        Heading = block["heading"]!.GetValue<string>(), Subheading = block["subheading"]!.GetValue<string>(),
                        // A browser's text box stores a line break as CR LF.
                        Body = block["body"]!.GetValue<string>().Replace("\n", "\r\n"),
                        ImageUrl = Url(block["imageRef"]!.GetValue<string>()),
                        ButtonText = block["buttonText"]!.GetValue<string>(), ButtonUrl = block["buttonUrl"]!.GetValue<string>(),
                        LayoutVariant = block["layoutVariant"]!.GetValue<string>(),
                        SortOrder = block["sortOrder"]!.GetValue<int>() * 10, IsVisible = block["isVisible"]!.GetValue<bool>(),
                        SettingsJson = type switch
                        {
                            WebsiteBlockTypes.Hero => new WebsiteHeroSettings
                            {
                                Layout = settings["layout"]!.GetValue<string>(), ImagePosition = settings["imagePosition"]!.GetValue<string>(),
                                TextAlignment = settings["textAlignment"]!.GetValue<string>(), Height = settings["height"]!.GetValue<string>(),
                                OverlayStrength = settings["overlayStrength"]!.GetValue<int>()
                            }.ToJson(),
                            WebsiteBlockTypes.ContactForm => new WebsiteContactFormSettings { ShowPhoto = settings["showPhoto"]!.GetValue<bool>() }.ToJson(),
                            WebsiteBlockTypes.Blog => new WebsiteBlogSettings { PostCount = settings["postCount"]!.GetValue<int>(), ShowImages = settings["showImages"]!.GetValue<bool>() }.ToJson(),
                            WebsiteBlockTypes.Form => new WebsiteFormSettings { WebsiteFormId = settings["websiteFormId"]!.GetValue<int>() }.ToJson(),
                            WebsiteBlockTypes.ArticleContent => new WebsiteArticleContentSettings { ArticleId = settings["articleId"]!.GetValue<int>() }.ToJson(),
                            _ => "{}"
                        }
                    });
                }
                site.Pages.Add(row);
            }
            foreach (var page in site.Template["pages"]!.AsArray().Where(p => p!["parentSlug"] != null))
            {
                site.Pages.Single(p => p.Slug == page!["slug"]!.GetValue<string>()).ParentPageId = idOfSlug[page!["parentSlug"]!.GetValue<string>()];
            }

            foreach (var article in site.Handover["catalog"]!["articles"]!.AsArray())
            {
                var imageRef = article!["imageRef"]!.GetValue<string>();
                site.Articles.Add(new Article
                {
                    Id = article["id"]!.GetValue<int>(), AgentUserId = 4, Title = article["title"]!.GetValue<string>(), Summary = article["summary"]!.GetValue<string>(),
                    ImageUrl = imageRef.Length == 0 ? string.Empty : Stored(imageRef, "article-media"), IsPublished = true,
                    PublishedAt = DateTime.Parse(article["publishedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
                });
            }
            foreach (var form in site.Handover["catalog"]!["forms"]!.AsArray())
            {
                var row = new WebsiteForm
                {
                    Id = form!["id"]!.GetValue<int>(), AgentUserId = 4, Title = form["title"]!.GetValue<string>(),
                    Description = form["description"]!.GetValue<string>(), SubmitButtonText = form["submitButtonText"]!.GetValue<string>()
                };
                var fields = new List<WebsiteFormField>();
                var options = new Dictionary<int, IReadOnlyList<string>>();
                var order = 0;
                foreach (var field in form["fields"]!.AsArray())
                {
                    var f = new WebsiteFormField
                    {
                        Id = field!["id"]!.GetValue<int>(), WebsiteFormId = row.Id, FieldType = field["type"]!.GetValue<string>(), Label = field["label"]!.GetValue<string>(),
                        Placeholder = field["placeholder"]!.GetValue<string>(), HelpText = field["helpText"]!.GetValue<string>(),
                        // A section header is never required, whatever its row says.
                        IsRequired = field["required"]!.GetValue<bool>() || field["type"]!.GetValue<string>() == WebsiteFormFieldTypes.Section,
                        SortOrder = order++
                    };
                    fields.Add(f);
                    var list = field["options"]!.AsArray().Select(o => o!.GetValue<string>()).ToList();
                    // A stale option list on a field that takes none is not sent.
                    options[f.Id] = list.Count > 0 ? list : new List<string> { "stale" };
                }
                site.Forms.Add(new HandoverForm(row, fields, options));
            }
            return site;
        }
    }
}
