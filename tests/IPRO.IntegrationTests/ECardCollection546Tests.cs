using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPRO.IntegrationTests;

// 546 (2026-10-03). The owner approved 49 e-card designs across 21 occasions (the designer's handoff
// package, "Approved v6", all Keep) and handed them over: "This is ready to be taken care of by you."
// They join the library without touching it: inserted once, by key or picture, never updated. The
// collection brought two things the old library never had -- pictures that carry their own words
// (lettering art with the title drawn in; four cards with the whole greeting printed inside) and
// pictures that are not 4:3 (square, tall, a 361 px animated GIF) -- so the card no longer prints the
// greeting twice, the picker shows the whole picture instead of cropping it, and a small picture is
// matted instead of enlarged. The nine pictures the owner supplied himself carry no licence in the
// package and arrive switched off until he confirms them.
public class ECardCollection546Tests
{
    private static readonly string[] Lettering =
    {
        "thank-you-wildflowers", "welcome-new-growth", "birthday-vintage-lettering",
        "birthday-groovy-lettering", "birthday-colourful-letters", "thank-you-rose-note"
    };

    private static readonly string[] GreetingInPicture =
    {
        "thank-you-songbird", "anniversary-red-roses", "congratulations-city-marquee", "christmas-sleigh-bells"
    };

    private static readonly string[] Supplied =
    {
        "birthday-vintage-lettering", "birthday-groovy-lettering", "birthday-colourful-letters", "anniversary-red-roses",
        "congratulations-city-marquee", "christmas-sleigh-bells", "thank-you-rose-note", "norooz-haft-seen", "norooz-goldfish"
    };

    [Fact]
    public void The_collection_is_the_approved_49_with_their_occasions_sizes_and_greeting_places()
    {
        var all = ECardCollectionSeeder.BuildCollection();

        Assert.Equal(49, all.Count);
        Assert.Equal(49, all.Select(d => d.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(21, all.Select(d => d.Occasion).Distinct().Count());
        Assert.Equal(2, all.Count(d => d.Occasion == "Norooz"));                       // a new occasion
        Assert.Equal(4, all.Count(d => d.Occasion == "Season's greetings"));            // the brief's label, shortened
        Assert.DoesNotContain(all, d => d.Occasion.Contains("religious"));

        // Every sent card stores its design's key in ECards.Occasion, a varchar(40).
        Assert.All(all, d => Assert.InRange(d.Key.Length, 1, 40));
        Assert.All(all, d => Assert.Equal(ECardArtKinds.Image, d.Kind));
        Assert.All(all, d => Assert.False(d.SendAfterUnsubscribe));                     // a human ticks that, per design

        Assert.Equal(Lettering, all.Where(d => d.GreetingStyle == ECardGreetingStyles.TitleInPicture).Select(d => d.Key));
        Assert.Equal(GreetingInPicture, all.Where(d => d.GreetingStyle == ECardGreetingStyles.InPicture).Select(d => d.Key));
        Assert.Equal(39, all.Count(d => d.GreetingStyle == ECardGreetingStyles.Below));

        // The owner's own pictures wait for their licences; the 40 made for iPro are on.
        Assert.Equal(Supplied, all.Where(d => !d.IsActive).Select(d => d.Key));
        Assert.Equal(40, all.Count(d => d.IsActive));
        // The December ten are in, the sleigh with the supplied nine.
        foreach (var key in new[] { "winter-evergreens", "winter-aurora", "winter-cardinal", "winter-cabin", "christmas-baubles",
                     "christmas-wreath", "new-year-fireworks", "new-year-sunrise", "hanukkah-nine-lights" })
            Assert.True(all.Single(d => d.Key == key).IsActive, key);

        // 40 faces at 1240 x 930; the supplied keep their own proportions.
        Assert.Equal(40, all.Count(d => d.Width == 1240 && d.Height == 930));
        var marquee = all.Single(d => d.Key == "congratulations-city-marquee");
        Assert.Equal((1240, 1240), (marquee.Width, marquee.Height));
        var goldfish = all.Single(d => d.Key == "norooz-goldfish");
        Assert.Equal((361, 238), (goldfish.Width, goldfish.Height));
        Assert.Equal("/images/ecard-art/norooz-goldfish.gif", goldfish.ImageUrl);     // the animation, its first frame for Outlook

        // Ground and greeting from the package, e.g. #1 and #49.
        var balloons = all.Single(d => d.Key == "birthday-balloons");
        Assert.True(balloons.IsDark);
        Assert.Equal("Happy Birthday", balloons.DefaultHeaderText);
        Assert.Equal("Wishing you a wonderful birthday and a year filled with good things.", balloons.DefaultMessage);
        Assert.False(goldfish.IsDark);
        Assert.Equal("Happy Norooz!", goldfish.DefaultHeaderText);
        Assert.Equal(Enumerable.Range(1, 49).Select(n => n * 10), all.Select(d => d.SortOrder));
    }

    [Fact]
    public void Every_picture_is_in_the_site_at_its_recorded_size_and_every_shipped_picture_has_a_picker_copy()
    {
        var art = Path.Combine(RepoRoot(), "src", "IPRO.Web", "wwwroot", "images", "ecard-art");

        foreach (var design in ECardCollectionSeeder.BuildCollection().Concat(ECardDesignSeeder.BuildDefaults().Where(d => d.IsArtwork)))
        {
            Assert.StartsWith(ECardDesign.ShippedArtPath, design.ImageUrl);
            var file = Path.Combine(art, design.ImageUrl[ECardDesign.ShippedArtPath.Length..]);
            Assert.True(File.Exists(file), design.Key);
            Assert.Equal((design.Width, design.Height), ImageSize(file));
        }
        foreach (var design in ECardCollectionSeeder.BuildCollection().Where(d => d.ImageUrl.EndsWith(".jpg")))
            Assert.True(new FileInfo(Path.Combine(art, design.Key + ".jpg")).Length <= 300_000, design.Key);

        // The picker's copy of every shipped picture: same name under thumbs/, at most 400 px wide, never
        // wider than the picture, same proportions.
        var files = Directory.GetFiles(art);
        Assert.True(files.Length >= 59, $"{files.Length} pictures");
        foreach (var file in files)
        {
            var thumb = Path.Combine(art, "thumbs", Path.GetFileName(file));
            Assert.True(File.Exists(thumb), $"no picker copy of {Path.GetFileName(file)}");
            var (w, h) = ImageSize(file);
            var (tw, th) = ImageSize(thumb);
            Assert.True(tw <= 400 || tw == w, $"{Path.GetFileName(file)}: {tw} wide");
            Assert.True(tw <= w, $"{Path.GetFileName(file)} was enlarged");
            Assert.InRange(Math.Abs(th - h * (double)tw / w), 0, 1.0);
        }

        Assert.Equal("/images/ecard-art/thumbs/birthday-cake.jpg", new ECardDesign { ImageUrl = "/images/ecard-art/birthday-cake.jpg" }.PickerImageUrl);
        const string uploaded = "https://iprostorage.blob.core.windows.net/ecard-art/design-1.jpg";
        Assert.Equal(uploaded, new ECardDesign { ImageUrl = uploaded }.PickerImageUrl);         // shown as it is
        Assert.Equal("https://app.test/images/ecard-art/thumbs/halloween1.jpg",
            new ECardDesign { ImageUrl = "/images/ecard-art/halloween1.jpg" }.AbsolutePickerImageUrl("https://app.test/"));
    }

    [Fact]
    public async Task The_seeder_adds_the_collection_once_after_the_library_and_never_touches_a_design_an_admin_changed()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        await ECardDesignSeeder.SeedAsync(db);                                            // the 14 that shipped first
        var shipped = await db.ECardDesigns.AsNoTracking().ToDictionaryAsync(d => d.Key, d => (d.SortOrder, d.DefaultMessage, d.GreetingStyle));
        Assert.Equal(14, shipped.Count);

        await ECardCollectionSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        var designs = await db.ECardDesigns.AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Id).ToListAsync();
        Assert.Equal(63, designs.Count);
        foreach (var (key, before) in shipped)                                            // the library is as it was
            Assert.Equal(before, designs.Where(d => d.Key == key).Select(d => (d.SortOrder, d.DefaultMessage, d.GreetingStyle)).Single());
        Assert.All(shipped.Values, v => Assert.Equal(ECardGreetingStyles.Below, v.GreetingStyle));
        var added = designs.Skip(14).ToList();                                            // after it, in the package's order
        Assert.Equal(ECardCollectionSeeder.BuildCollection().Select(d => d.Key), added.Select(d => d.Key));
        Assert.Equal(shipped.Values.Max(v => v.SortOrder) + 10, added[0].SortOrder);
        Assert.Equal(ECardGreetingStyles.InPicture, added.Single(d => d.Key == "thank-you-songbird").GreetingStyle);
        Assert.Equal(ECardGreetingStyles.TitleInPicture, added.Single(d => d.Key == "welcome-new-growth").GreetingStyle);
        Assert.Equal(Supplied, added.Where(d => !d.IsActive).Select(d => d.Key));

        // SuperAdmin's changes: a key renamed, a greeting reworded, a supplied card switched on. And one
        // design gone from the table, the case the seeder exists for.
        var cake = await db.ECardDesigns.SingleAsync(d => d.Key == "birthday-cake");
        cake.Key = "birthday-cake-candles";
        var aurora = await db.ECardDesigns.SingleAsync(d => d.Key == "winter-aurora");
        aurora.DefaultMessage = "Warm wishes from all of us.";
        var goldfish = await db.ECardDesigns.SingleAsync(d => d.Key == "norooz-goldfish");
        goldfish.IsActive = true;
        db.ECardDesigns.Remove(await db.ECardDesigns.SingleAsync(d => d.Key == "eid-lantern"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await ECardCollectionSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        var after = await db.ECardDesigns.AsNoTracking().ToListAsync();
        Assert.Equal(63, after.Count);                                                    // the missing one back, nothing doubled
        Assert.DoesNotContain(after, d => d.Key == "birthday-cake");                      // its picture is still in the library
        Assert.Single(after, d => d.ImageUrl == "/images/ecard-art/birthday-cake.jpg");
        Assert.Equal("Warm wishes from all of us.", after.Single(d => d.Key == "winter-aurora").DefaultMessage);
        Assert.True(after.Single(d => d.Key == "norooz-goldfish").IsActive);
        Assert.Equal(after.Where(d => d.Key != "eid-lantern").Max(d => d.SortOrder) + 400, after.Single(d => d.Key == "eid-lantern").SortOrder);
    }

    [Fact]
    public void A_picture_without_words_gets_the_agents_title_and_message_below_it()
    {
        var design = Design("birthday-balloons");
        var html = ECardHtmlComposer.Wrap(new ECard { Subject = "Happy birthday, Bob", Message = "Many happy returns." }, Agent(), design, "https://app.test");

        Assert.Contains("Happy birthday, Bob", html);
        Assert.Contains("Many happy returns.", html);
        Assert.Contains("alt=\"Bright birthday balloons\"", html);
        Assert.Contains("src=\"https://app.test/images/ecard-art/birthday-balloons.jpg\" width=\"620\"", html);
        Assert.Contains("width=\"100%\" style=\"width:100%;max-width:620px;background:#111111;", html);   // 548: fluid up to 620
    }

    [Fact]
    public void Lettering_art_keeps_its_title_in_the_picture_and_takes_only_the_message_below()
    {
        var design = Design("thank-you-wildflowers");
        var card = new ECard { Subject = "Thanks for everything", Message = "It was a pleasure, Bob." };

        var html = ECardHtmlComposer.Wrap(card, Agent(), design, "https://app.test");
        Assert.DoesNotContain("Thanks for everything", html);                            // the subject line only
        Assert.DoesNotContain("font-family:Georgia", html);                               // no title band at all
        Assert.Contains("It was a pleasure, Bob.", html);
        Assert.Contains("alt=\"Thank You\"", html);                                       // what the picture says

        var text = ECardHtmlComposer.WrapText(card, Agent(), design, "https://app.test/p");
        Assert.StartsWith("Thank You\n\nIt was a pleasure, Bob.\n", text);
    }

    [Fact]
    public void A_picture_carrying_the_whole_greeting_gets_nothing_below_it_and_its_words_are_the_approved_ones()
    {
        var design = Design("thank-you-songbird");
        var card = new ECard { Subject = "Thank you!", Message = "My own words." };

        var html = ECardHtmlComposer.Wrap(card, Agent(), design, "https://app.test");
        Assert.DoesNotContain("My own words.", html);
        Assert.DoesNotContain("font-family:Georgia", html);
        Assert.DoesNotContain("line-height:1.65", html);                                   // no greeting band
        Assert.Contains("alt=\"Thank You. With sincere appreciation for your trust and support.\"", html);
        Assert.Contains("Pat Adviser", html);                                              // the contact block still follows

        var text = ECardHtmlComposer.WrapText(card, Agent(), design, "https://app.test/p");
        Assert.StartsWith("Thank You\n\nWith sincere appreciation for your trust and support.\n", text);
        Assert.DoesNotContain("My own words.", text);

        // A title with its own stop keeps it.
        Assert.Contains("alt=\"Merry Christmas! Wishing you a joyful Christmas and a peaceful holiday season.\"",
            ECardHtmlComposer.Wrap(card, Agent(), Design("christmas-sleigh-bells"), "https://app.test"));
    }

    [Fact]
    public void A_small_picture_is_matted_at_its_own_size_and_the_old_library_draws_as_before()
    {
        var goldfish = ECardHtmlComposer.Wrap(new ECard(), Agent(), Design("norooz-goldfish"), "https://app.test");
        Assert.Contains("style=\"width:100%;max-width:480px;", goldfish);                 // room for the contact block
        Assert.Contains("src=\"https://app.test/images/ecard-art/norooz-goldfish.gif\" width=\"361\"", goldfish);
        Assert.Contains("max-width:361px", goldfish);                                      // never enlarged
        Assert.Contains("padding:24px 24px 0;", goldfish);

        var lettering = ECardHtmlComposer.Wrap(new ECard(), Agent(), Design("birthday-colourful-letters"), "https://app.test");
        Assert.Contains("style=\"width:100%;max-width:498px;", lettering);                 // 450 + the mat

        // The narrowest picture before 2026, the anniversary roses at 467 px, is unchanged: full bleed.
        var roses = ECardDesignSeeder.BuildDefaults().Single(d => d.Key == "anniversary-1");
        var html = ECardHtmlComposer.Wrap(new ECard(), Agent(), roses, "https://app.test");
        Assert.Contains("style=\"width:100%;max-width:467px;", html);
        Assert.DoesNotContain("padding:24px 24px 0;", html);
        Assert.Contains("Happy anniversary", html);

        // A design uploaded before 546 has no recorded size; its picture used to be drawn 0 px wide.
        var uploaded = new ECardDesign { Kind = ECardArtKinds.Image, Name = "Uploaded", ImageUrl = "https://iprostorage.blob.core.windows.net/ecard-art/d.jpg" };
        var drawn = ECardHtmlComposer.Wrap(new ECard(), Agent(), uploaded, "https://app.test");
        Assert.Contains("width=\"620\"", drawn);
        Assert.DoesNotContain("width=\"0\"", drawn);

        // A Simple card has no picture to hold words, whatever its record says.
        var simple = ECardDesignSeeder.BuildDefaults().Single(d => d.Key == "simple-thank-you");
        simple.GreetingStyle = ECardGreetingStyles.InPicture;
        var panel = ECardHtmlComposer.Wrap(new ECard { Subject = "Thanks a lot", Message = "Truly." }, Agent(), simple, "https://app.test");
        Assert.Contains("Thanks a lot", panel);
        Assert.Contains("Truly.", panel);
    }

    [Fact]
    public async Task SuperAdmin_saves_where_the_words_are_and_a_simple_card_is_always_below()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var art = new ECardDesign
        {
            Key = "t546-lettering", Occasion = "Thank you", Name = "Lettering", Kind = ECardArtKinds.Image,
            ImageUrl = "/images/ecard-art/thank-you-wildflowers.jpg", Width = 1240, Height = 930
        };
        var simple = new ECardDesign { Key = "t546-simple", Occasion = "Simple", Name = "Panel", Kind = ECardArtKinds.Generated, Accent = "#1e3a8a", Emoji = "*" };
        db.AddRange(art, simple);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = NewController(db);
        Assert.IsType<RedirectToActionResult>(await controller.Edit(Posted(art, ECardGreetingStyles.TitleInPicture), null));
        Assert.IsType<RedirectToActionResult>(await controller.Edit(Posted(simple, ECardGreetingStyles.InPicture), null));
        db.ChangeTracker.Clear();
        Assert.Equal(ECardGreetingStyles.TitleInPicture, (await db.ECardDesigns.SingleAsync(d => d.Id == art.Id)).GreetingStyle);
        Assert.Equal(ECardGreetingStyles.Below, (await db.ECardDesigns.SingleAsync(d => d.Id == simple.Id)).GreetingStyle);

        Assert.IsType<RedirectToActionResult>(await NewController(db).Edit(Posted(art, "on-the-envelope"), null));
        db.ChangeTracker.Clear();
        Assert.Equal(ECardGreetingStyles.Below, (await db.ECardDesigns.SingleAsync(d => d.Id == art.Id)).GreetingStyle);
    }

    [Fact]
    public void The_screens_the_schema_and_both_start_ups_carry_it()
    {
        var picker = Read(@"src\IPRO.Web\Views\ECards\Create.cshtml");
        Assert.Contains(".occasion-choice img { display: block; width: 100%; aspect-ratio: 4/3; object-fit: contain;", picker);
        Assert.Contains("<img src=\"@template.PickerImageUrl\"", picker);
        Assert.Contains("data-greeting=\"@greeting\"", picker);
        Assert.Contains("messageInput.disabled = mode === 'in-picture';", picker);              // not sent...
        Assert.Contains("messageField.hidden = mode === 'in-picture';", picker);                // ...and not shown
        Assert.Contains("<select id=\"occasionFilter\"", picker);
        // Untouched stock wording follows the design; the agent's own is never replaced.
        Assert.Contains("(!subjectInput.value.trim() || subjectInput.value === stockHeader)", picker);
        Assert.Contains("(!messageInput.value.trim() || messageInput.value === stockMessage)", picker);
        Assert.Contains("<img src=\"@cardTemplate.PickerImageUrl\"", Read(@"src\IPRO.Web\Views\ECards\Index.cshtml"));

        var edit = Read(@"src\IPRO.Admin\Views\ECardDesigns\Edit.cshtml");
        Assert.Contains("<select asp-for=\"GreetingStyle\" class=\"form-select\">", edit);
        Assert.Contains("widthInput.value = picture.naturalWidth;", edit);                 // an upload records its size
        Assert.Contains("@design.AbsolutePickerImageUrl(webBaseUrl)", Read(@"src\IPRO.Admin\Views\ECardDesigns\Index.cshtml"));
        Assert.Contains("existing.GreetingStyle = model.GreetingStyle;", Read(@"src\IPRO.Admin\Controllers\ECardDesignsController.cs"));

        // Both apps add the column before the seeders run (INVARIANTS rule 4), and both seed.
        Assert.Contains("(\"ECardDesigns\", \"GreetingStyle\", \"varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'below'\")",
            Read(@"src\IPRO.DataAccess\EmailDeliverySchema.cs"));
        Assert.Contains("ConsentColumns.Concat(DesignColumns)", Read(@"src\IPRO.DataAccess\EmailDeliverySchema.cs"));
        foreach (var program in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
        {
            var src = Read(program);
            var schema = src.IndexOf("await EmailDeliverySchema.EnsureAsync(db);", StringComparison.Ordinal);
            var seed = src.IndexOf("        await ECardCollectionSeeder.SeedAsync(db, seedLogger);", StringComparison.Ordinal);
            Assert.True(schema >= 0 && seed > schema, program);
        }

        var guide = Read(@"DOCS\18_ECARDS.md");
        Assert.Contains("Norooz", guide);
        Assert.Contains("printed in the picture", guide);
        Assert.Contains("Words in the picture", guide);
    }

    // ---- harness -----------------------------------------------------------------------------------

    private static ECardDesign Design(string key) => ECardCollectionSeeder.BuildCollection().Single(d => d.Key == key);

    private static AgentUser Agent() => new()
    {
        FirstName = "Pat", LastName = "Adviser", CompanyName = "Global Business Solution",
        Email = "pat546@example.test", Phone = "416-555-0146", DomainName = "pat546.example.test"
    };

    private static ECardDesign Posted(ECardDesign d, string greetingStyle) => new()
    {
        Id = d.Id, Key = d.Key, Occasion = d.Occasion, Name = d.Name, Kind = d.Kind, ImageUrl = d.ImageUrl,
        Width = d.Width, Height = d.Height, Accent = d.Accent, Emoji = d.Emoji, IsActive = true, GreetingStyle = greetingStyle
    };

    private static IPRO.Admin.Controllers.ECardDesignsController NewController(IPRODbContext db)
    {
        var controller = new IPRO.Admin.Controllers.ECardDesignsController(db, new ServiceCollection().BuildServiceProvider(),
            new NoAudit(), new ConfigurationBuilder().AddInMemoryCollection().Build());
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, "admin") }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(ctx, new NoTempData());
        return controller;
    }

    private sealed class NoAudit : IAdminAuditLogService
    {
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details) => Task.CompletedTask;
    }

    private sealed class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    // Width and height from the file itself: a GIF's logical screen, or a JPEG's start-of-frame segment.
    private static (int Width, int Height) ImageSize(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length > 10 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
            return (b[6] | b[7] << 8, b[8] | b[9] << 8);
        Assert.True(b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8, $"{path} is not a JPEG or GIF");
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                return (b[i + 7] << 8 | b[i + 8], b[i + 5] << 8 | b[i + 6]);
            i += 2 + (b[i + 2] << 8 | b[i + 3]);
        }
        throw new InvalidDataException($"no frame header in {path}");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));
}
