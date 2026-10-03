using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.DataAccess;

// 546: the 2026 e-card collection -- 49 designs across 21 occasions, approved by the owner on
// 2026-10-03 (the designer's handoff package, "Approved v6", all marked Keep). Keys, names and
// greetings are the package's designs.csv; sizes, grounds and greeting placement its manifest.json.
// The pictures are in IPRO.Web/wwwroot/images/ecard-art, named by key, each with a thumbs/ copy.
//
// Unlike ECardDesignSeeder, which fills an empty table once, this adds to a library SuperAdmin
// already manages, so it inserts only what is missing and never updates a row: an admin's edit to
// one of these designs (its greeting, its order, switching it on) must survive every deploy. A
// design counts as present when its key OR its picture is already in the table, so renaming a key
// in SuperAdmin cannot make the next start-up add the design a second time.
//
// The occasions are the package's, with one label shortened: the brief's "Season's greetings
// (winter, not religious)" is a note to the designer, and the picker shows "Season's greetings".
public static class ECardCollectionSeeder
{
    // Its own lock name: SeedGuard serialises the two apps running this at the same start-up.
    public static async Task SeedAsync(IPRODbContext db, ILogger? logger = null) =>
        await SeedGuard.RunAsync(db, "ECardCollection2026", logger, async () =>
        {
            var existing = await db.ECardDesigns.AsNoTracking()
                .Select(d => new { d.Key, d.ImageUrl, d.SortOrder })
                .ToListAsync();
            var keys = existing.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pictures = existing.Select(d => d.ImageUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = BuildCollection()
                .Where(d => !keys.Contains(d.Key) && !pictures.Contains(d.ImageUrl))
                .ToList();
            if (missing.Count == 0) return;

            // After everything already in the picker, in the package's order (#1-49).
            var after = existing.Count == 0 ? 0 : existing.Max(d => d.SortOrder);
            foreach (var design in missing) design.SortOrder += after;

            db.ECardDesigns.AddRange(missing);
            await db.SaveChangesAsync();
            logger?.LogWarning("E-card collection 2026: added {Count} design(s).", missing.Count);
        });

    // Public so the collection can be checked and rendered without a database.
    public static List<ECardDesign> BuildCollection()
    {
        ECardDesign Art(int number, string key, string occasion, string name, string file, int width, int height,
            bool dark, string greetingStyle, string header, string message) =>
            new()
            {
                Key = key,
                Occasion = occasion,
                Name = name,
                Kind = ECardArtKinds.Image,
                ImageUrl = ECardDesign.ShippedArtPath + file,
                Width = width,
                Height = height,
                IsDark = dark,
                GreetingStyle = greetingStyle,
                DefaultHeaderText = header,
                DefaultMessage = message,
                IsActive = number <= 40,
                SortOrder = number * 10,
            };

        return new List<ECardDesign>
        {
            Art(1, "birthday-balloons", "Birthday", "Bright birthday balloons", "birthday-balloons.jpg", 1240, 930, dark: true, ECardGreetingStyles.Below,
                "Happy Birthday",
                "Wishing you a wonderful birthday and a year filled with good things."),
            Art(2, "birthday-cake", "Birthday", "Cake with candles", "birthday-cake.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy Birthday",
                "May your day bring plenty of joy and a few lovely surprises."),
            Art(3, "birthday-gift", "Birthday", "Gift with ribbon", "birthday-gift.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy Birthday",
                "Wishing you a happy birthday and a bright year ahead."),
            Art(4, "birthday-ribbons", "Birthday", "Ribbons in motion", "birthday-ribbons.jpg", 1240, 930, dark: true, ECardGreetingStyles.Below,
                "Happy Birthday",
                "Here is to a new year of happy moments and possibilities."),
            Art(5, "thank-you-wildflowers", "Thank you", "A clear thank you", "thank-you-wildflowers.jpg", 1240, 930, dark: false, ECardGreetingStyles.TitleInPicture,
                "Thank You",
                "Thank you for your trust. It is a pleasure working with you."),
            Art(6, "thank-you-camellia", "Thank you", "Camellia in vase", "thank-you-camellia.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Thank You",
                "Your support means a great deal. Thank you."),
            Art(7, "thank-you-songbird", "Thank you", "A note of thanks", "thank-you-songbird.jpg", 1240, 930, dark: false, ECardGreetingStyles.InPicture,
                "Thank You",
                "With sincere appreciation for your trust and support."),
            Art(8, "congratulations-rosette", "Congratulations", "Ribbon rosette celebration", "congratulations-rosette.jpg", 1240, 930, dark: true, ECardGreetingStyles.Below,
                "Congratulations",
                "Wishing you every success as you celebrate this achievement."),
            Art(9, "congratulations-summit", "Congratulations", "Star of success", "congratulations-summit.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Congratulations",
                "Congratulations on this milestone. May it open the door to wonderful things ahead."),
            Art(10, "welcome-new-growth", "Welcome (new client)", "A warm welcome", "welcome-new-growth.jpg", 1240, 930, dark: true, ECardGreetingStyles.TitleInPicture,
                "Welcome",
                "It is a pleasure to welcome you. We look forward to working with you."),
            Art(11, "welcome-coffee", "Welcome (new client)", "Coffee for two", "welcome-coffee.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Welcome",
                "A warm welcome. We are glad to begin this journey with you."),
            Art(12, "anniversary-cranes", "Anniversary", "Cranes by water", "anniversary-cranes.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy Anniversary",
                "Wishing you a beautiful anniversary and many more happy moments together."),
            Art(13, "anniversary-ribbons", "Anniversary", "Two intertwined ribbons", "anniversary-ribbons.jpg", 1240, 930, dark: true, ECardGreetingStyles.Below,
                "Happy Anniversary",
                "May the years ahead bring continued happiness and wonderful memories together."),
            Art(14, "new-home-doorway", "New home", "Sunlit orange doorway", "new-home-doorway.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Welcome Home",
                "Wishing you happiness, comfort and wonderful memories in your new home."),
            Art(15, "new-home-keys", "New home", "Keys with olive", "new-home-keys.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Congratulations on Your Home",
                "May your new home be a place of warmth, laughter and belonging."),
            Art(16, "retirement-lakeside", "Retirement", "Blue lakeside chair", "retirement-lakeside.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy Retirement",
                "Wishing you time to explore, relax and enjoy all that comes next."),
            Art(17, "retirement-sailboat", "Retirement", "Sailboat at dawn", "retirement-sailboat.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy Retirement",
                "May this new chapter bring freedom, discovery and many happy days."),
            Art(18, "new-baby-booties", "New baby", "Tiny knitted booties", "new-baby-booties.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Welcome, Little One",
                "Warm wishes to you and your growing family as you welcome your little one."),
            Art(19, "get-well-tulips", "Get well", "Rest and recovery", "get-well-tulips.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Get Well Soon",
                "Wishing you a smooth recovery and better days ahead."),
            Art(20, "sympathy-peace-lily", "Sympathy", "Quiet white lily", "sympathy-peace-lily.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "With Sympathy",
                "Thinking of you with sympathy and wishing you comfort in the days ahead."),
            Art(21, "winter-evergreens", "Season's greetings", "Glowing snowy evergreens", "winter-evergreens.jpg", 1240, 930, dark: true, ECardGreetingStyles.Below,
                "Season's Greetings",
                "Wishing you a peaceful season and a wonderful year ahead."),
            Art(22, "winter-aurora", "Season's greetings", "Aurora over pines", "winter-aurora.jpg", 1240, 930, dark: true, ECardGreetingStyles.Below,
                "Season's Greetings",
                "May the season bring warmth, rest and time with those who matter most."),
            Art(23, "winter-cardinal", "Season's greetings", "Cardinal on pine", "winter-cardinal.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Warm Winter Wishes",
                "Wishing you moments of peace and joy throughout the season."),
            Art(24, "winter-cabin", "Season's greetings", "Cabin in snow", "winter-cabin.jpg", 1240, 930, dark: true, ECardGreetingStyles.Below,
                "Season's Greetings",
                "Wishing you a warm and restful season, wherever you call home."),
            Art(25, "christmas-baubles", "Christmas", "Baubles among evergreens", "christmas-baubles.jpg", 1240, 930, dark: true, ECardGreetingStyles.Below,
                "Merry Christmas",
                "Wishing you a joyful Christmas and a peaceful holiday season."),
            Art(26, "christmas-wreath", "Christmas", "Modern evergreen wreath", "christmas-wreath.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Merry Christmas",
                "May your Christmas be filled with warmth, happiness and cherished moments."),
            Art(27, "new-year-fireworks", "New Year", "Fireworks above water", "new-year-fireworks.jpg", 1240, 930, dark: true, ECardGreetingStyles.Below,
                "Happy New Year",
                "Wishing you a bright year filled with health, happiness and new possibilities."),
            Art(28, "new-year-sunrise", "New Year", "Sunrise on horizon", "new-year-sunrise.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy New Year",
                "May the year ahead bring fresh opportunities and moments to treasure."),
            Art(29, "thanksgiving-harvest", "Thanksgiving (Canada)", "Pumpkin and pears", "thanksgiving-harvest.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy Thanksgiving",
                "Wishing you a warm Thanksgiving filled with gratitude and good company."),
            Art(30, "thanksgiving-maple", "Thanksgiving (Canada)", "Sunlit maple branch", "thanksgiving-maple.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy Thanksgiving",
                "With appreciation and warm wishes for a peaceful Thanksgiving."),
            Art(31, "canada-day-maple", "Canada Day", "Red maple leaf", "canada-day-maple.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy Canada Day",
                "Wishing you a wonderful Canada Day and a relaxing summer celebration."),
            Art(32, "canada-day-lakeshore", "Canada Day", "Maple by lakeshore", "canada-day-lakeshore.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy Canada Day",
                "Warm wishes for a happy Canada Day, wherever you are celebrating."),
            Art(33, "easter-eggs", "Easter", "Pastel Easter eggs", "easter-eggs.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy Easter",
                "Wishing you a joyful Easter and a beautiful start to spring."),
            Art(34, "easter-daffodils", "Easter", "Daffodils and egg", "easter-daffodils.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy Easter",
                "May Easter bring you peace, happiness and fresh possibilities."),
            Art(35, "diwali-diya", "Diwali", "Glowing clay diya", "diwali-diya.jpg", 1240, 930, dark: true, ECardGreetingStyles.Below,
                "Happy Diwali",
                "Wishing you a joyful Diwali filled with light, warmth and happiness."),
            Art(36, "diwali-rangoli", "Diwali", "Diya and rangoli", "diwali-rangoli.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy Diwali",
                "May the festival of lights bring joy and bright possibilities to your home."),
            Art(37, "hanukkah-nine-lights", "Hanukkah", "Nine glowing candles", "hanukkah-nine-lights.jpg", 1240, 930, dark: true, ECardGreetingStyles.Below,
                "Happy Hanukkah",
                "Wishing you a Hanukkah filled with light, peace and happy moments."),
            Art(38, "lunar-new-year-lanterns", "Lunar New Year", "Red hanging lanterns", "lunar-new-year-lanterns.jpg", 1240, 930, dark: true, ECardGreetingStyles.Below,
                "Happy Lunar New Year",
                "Wishing you happiness, good health and a bright year ahead."),
            Art(39, "lunar-new-year-mandarins", "Lunar New Year", "Mandarins and blossoms", "lunar-new-year-mandarins.jpg", 1240, 930, dark: false, ECardGreetingStyles.Below,
                "Happy Lunar New Year",
                "Warm wishes for a joyful celebration and a year of new possibilities."),
            Art(40, "eid-lantern", "Eid", "Lantern under crescent", "eid-lantern.jpg", 1240, 930, dark: true, ECardGreetingStyles.Below,
                "Eid Mubarak",
                "Wishing you and your loved ones a joyful Eid filled with peace and happiness."),

            // The nine pictures the owner supplied (#41-49). Approved for the collection, but the package
            // carries no licence for them (RIGHTS-AND-SOURCES.txt), so they arrive switched off; SuperAdmin
            // turns each on once its licence is confirmed. Sizes are their own: never stretched or cropped.
            Art(41, "birthday-vintage-lettering", "Birthday", "Vintage Birthday Lettering", "birthday-vintage-lettering.jpg", 967, 750, dark: false, ECardGreetingStyles.TitleInPicture,
                "Happy Birthday!",
                "Wishing you a wonderful day and a happy year ahead."),
            Art(42, "birthday-groovy-lettering", "Birthday", "Groovy Birthday Lettering", "birthday-groovy-lettering.jpg", 1024, 768, dark: false, ECardGreetingStyles.TitleInPicture,
                "Happy Birthday!",
                "Wishing you a bright and joyful birthday."),
            Art(43, "birthday-colourful-letters", "Birthday", "Colourful Birthday Letters", "birthday-colourful-letters.jpg", 450, 267, dark: false, ECardGreetingStyles.TitleInPicture,
                "Happy Birthday!",
                "May your day be filled with happiness and good company."),
            Art(44, "anniversary-red-roses", "Anniversary", "Red Rose Keepsake", "anniversary-red-roses.jpg", 1240, 827, dark: false, ECardGreetingStyles.InPicture,
                "Happy Anniversary!",
                "Wishing you another wonderful year of love and happiness."),
            Art(45, "congratulations-city-marquee", "Congratulations", "City Lights Marquee", "congratulations-city-marquee.jpg", 1240, 1240, dark: true, ECardGreetingStyles.InPicture,
                "Congratulations!",
                "Celebrating your achievement and wishing you continued success."),
            Art(46, "christmas-sleigh-bells", "Christmas", "Sleigh and Bells", "christmas-sleigh-bells.jpg", 1240, 1217, dark: false, ECardGreetingStyles.InPicture,
                "Merry Christmas!",
                "Wishing you a joyful Christmas and a peaceful holiday season."),
            Art(47, "thank-you-rose-note", "Thank you", "Rose Thank You", "thank-you-rose-note.jpg", 1240, 827, dark: false, ECardGreetingStyles.TitleInPicture,
                "Thank You!",
                "With appreciation for your trust and support."),
            Art(48, "norooz-haft-seen", "Norooz", "Norooz Celebration Table", "norooz-haft-seen.jpg", 600, 423, dark: false, ECardGreetingStyles.Below,
                "Happy Norooz!",
                "Wishing you and your loved ones health, happiness and prosperity in the new year."),
            Art(49, "norooz-goldfish", "Norooz", "Norooz Golden Fish", "norooz-goldfish.gif", 361, 238, dark: false, ECardGreetingStyles.Below,
                "Happy Norooz!",
                "Wishing you and your loved ones health, happiness and prosperity in the new year."),
        };
    }
}
