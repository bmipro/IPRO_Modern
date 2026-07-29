using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// Seeds the card designs that used to be a hardcoded array in IPRO.Entities. Runs once; after
// that the table is SuperAdmin's to manage and this seeder must never touch it again, or an
// admin's edits would be silently reverted on the next deploy.
//
// Greeting copy is the legacy wording recovered verbatim from the 2014 database dump.
public static class ECardDesignSeeder
{
    public static async Task SeedAsync(IPRODbContext db)
    {
        if (await db.ECardDesigns.AnyAsync()) return;
        db.ECardDesigns.AddRange(BuildDefaults());
        await db.SaveChangesAsync();
    }

    // Public so the shipped library can be rendered and eyeballed without a database.
    public static List<ECardDesign> BuildDefaults()
    {
        var order = 0;
        int Next() => order += 10;

        ECardDesign Generated(string key, string name, string accent, string emoji, string header, string message) =>
            new()
            {
                Key = key,
                Occasion = "Simple",
                Name = name,
                Kind = ECardArtKinds.Generated,
                Accent = accent,
                Emoji = emoji,
                DefaultHeaderText = header,
                DefaultMessage = message,
                IsDark = true,
                SortOrder = Next(),
            };

        ECardDesign Art(string key, string occasion, string name, string file, int w, int h, bool dark,
            string header, string message) =>
            new()
            {
                Key = key,
                Occasion = occasion,
                Name = name,
                Kind = ECardArtKinds.Image,
                ImageUrl = $"/images/ecard-art/{file}",
                Width = w,
                Height = h,
                IsDark = dark,
                DefaultHeaderText = header,
                DefaultMessage = message,
                SortOrder = Next(),
            };

        const string halloweenHeader = "Happy Halloween";
        const string halloweenMessage = "Have a very special Halloween";
        const string anniversaryHeader = "Happy anniversary";
        const string anniversaryMessage = "May this very special love always be yours to share";

        // Simple cards first: they work for any occasion, and an agent in a hurry shouldn't have
        // to scroll past seven zombies to find Thank You.
        return new List<ECardDesign>
        {
            Generated("simple-birthday", "Birthday", "#ff7a59", "🎂",
                "Happy Birthday", "Wishing you a wonderful year ahead."),
            Generated("simple-thank-you", "Thank You", "#0f9d78", "🙏",
                "Thank You", "Thank you — it's a pleasure working with you."),
            Generated("simple-holiday", "Season's Greetings", "#1e3a8a", "❄️",
                "Season's Greetings", "Wishing you and your family a warm and happy season."),
            Generated("simple-congratulations", "Congratulations", "#7c3aed", "🎉",
                "Congratulations", "Congratulations — very well deserved."),

            Art("halloween-1", "Halloween", "Zombie couple", "halloween1.jpg", 700, 525, true, halloweenHeader, halloweenMessage),
            Art("halloween-2", "Halloween", "Zombie clown and doll", "halloween2.jpg", 700, 525, true, halloweenHeader, halloweenMessage),
            Art("halloween-3", "Halloween", "Hell hound", "halloween3.jpg", 700, 525, true, halloweenHeader, halloweenMessage),
            Art("halloween-4", "Halloween", "Zombie pair dancing", "halloween4.jpg", 700, 525, true, halloweenHeader, halloweenMessage),
            Art("halloween-5", "Halloween", "Nurse and pumpkin", "halloween5.jpg", 700, 525, true, halloweenHeader, halloweenMessage),
            Art("halloween-6", "Halloween", "Mechanic zombies", "halloween6.jpg", 700, 525, true, halloweenHeader, halloweenMessage),
            Art("halloween-7", "Halloween", "Zombie horde", "halloween7.jpg", 700, 525, true, halloweenHeader, halloweenMessage),

            Art("anniversary-1", "Anniversary", "Red roses", "anniversary1.jpg", 467, 311, false, anniversaryHeader, anniversaryMessage),
            Art("anniversary-2", "Anniversary", "Red roses (alternate)", "anniversary2.jpg", 467, 311, false, anniversaryHeader, anniversaryMessage),

            Art("birthday-audi", "Birthday", "Luxury car", "birthday-audi.jpg", 540, 396, false,
                "Happy Birthday",
                "With many good wishes for your birthday and every day throughout the coming year."),
        };
    }
}
