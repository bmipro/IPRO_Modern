namespace IPRO.Entities;

// A card is either a piece of licensed artwork or a clean generated panel built from the agent's
// own accent colour. Both are first-class: the artwork cards cover specific occasions with real
// illustration, the generated ones are the simple everyday cards that work for any occasion.
public static class ECardArtKinds
{
    public const string Image = "image";
    public const string Generated = "generated";
}

public record ECardTemplate(
    string Key,
    string Occasion,
    string Name,
    string Kind,
    string DefaultHeaderText,
    string DefaultMessage)
{
    // Artwork cards only.
    public string FileName { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }

    // Generated cards only -- the gradient runs from the agent's accent to this colour.
    public string Accent { get; init; } = string.Empty;
    public string Emoji { get; init; } = string.Empty;

    // Whether the greeting band sits on a dark or light ground. Artwork with a dark ground gets a
    // black band so the card reads as one object rather than art with a white strip stapled below.
    public bool IsDark { get; init; }

    public bool IsArtwork => Kind == ECardArtKinds.Image;
    public string Url => IsArtwork ? $"/images/ecard-art/{FileName}" : string.Empty;

    public static ECardTemplate Art(string key, string occasion, string name, string fileName,
        int width, int height, bool isDark, string header, string message) =>
        new(key, occasion, name, ECardArtKinds.Image, header, message)
        { FileName = fileName, Width = width, Height = height, IsDark = isDark };

    public static ECardTemplate Generated(string key, string occasion, string name,
        string accent, string emoji, string header, string message) =>
        new(key, occasion, name, ECardArtKinds.Generated, header, message)
        { Accent = accent, Emoji = emoji, IsDark = true };
}

public static class ECardTemplateCatalog
{
    // The simple generated cards come first -- they're the everyday ones, they work for any
    // occasion, and an agent in a hurry shouldn't have to scroll past seven zombies to find them.
    // Artwork greeting copy is the legacy wording, recovered verbatim from the 2014 database dump.
    public static IReadOnlyList<ECardTemplate> All { get; } = new List<ECardTemplate>
    {
        ECardTemplate.Generated("simple-birthday", "Simple", "Birthday", "#ff7a59", "🎂",
            "Happy Birthday", "Wishing you a wonderful year ahead."),
        ECardTemplate.Generated("simple-thank-you", "Simple", "Thank You", "#0f9d78", "🙏",
            "Thank You", "Thank you — it's a pleasure working with you."),
        ECardTemplate.Generated("simple-holiday", "Simple", "Season's Greetings", "#1e3a8a", "❄️",
            "Season's Greetings", "Wishing you and your family a warm and happy season."),
        ECardTemplate.Generated("simple-congratulations", "Simple", "Congratulations", "#7c3aed", "🎉",
            "Congratulations", "Congratulations — very well deserved."),

        ECardTemplate.Art("halloween-1", "Halloween", "Zombie couple", "halloween1.jpg", 700, 525,
            true, "Happy Halloween", "Have a very special Halloween"),
        ECardTemplate.Art("halloween-2", "Halloween", "Zombie clown and doll", "halloween2.jpg", 700, 525,
            true, "Happy Halloween", "Have a very special Halloween"),
        ECardTemplate.Art("halloween-3", "Halloween", "Hell hound", "halloween3.jpg", 700, 525,
            true, "Happy Halloween", "Have a very special Halloween"),
        ECardTemplate.Art("halloween-4", "Halloween", "Zombie pair dancing", "halloween4.jpg", 700, 525,
            true, "Happy Halloween", "Have a very special Halloween"),
        ECardTemplate.Art("halloween-5", "Halloween", "Nurse and pumpkin", "halloween5.jpg", 700, 525,
            true, "Happy Halloween", "Have a very special Halloween"),
        ECardTemplate.Art("halloween-6", "Halloween", "Mechanic zombies", "halloween6.jpg", 700, 525,
            true, "Happy Halloween", "Have a very special Halloween"),
        ECardTemplate.Art("halloween-7", "Halloween", "Zombie horde", "halloween7.jpg", 700, 525,
            true, "Happy Halloween", "Have a very special Halloween"),

        ECardTemplate.Art("anniversary-1", "Anniversary", "Red roses", "anniversary1.jpg", 467, 311,
            false, "Happy anniversary", "May this very special love always be yours to share"),
        ECardTemplate.Art("anniversary-2", "Anniversary", "Red roses (alternate)", "anniversary2.jpg", 467, 311,
            false, "Happy anniversary", "May this very special love always be yours to share"),

        ECardTemplate.Art("birthday-audi", "Birthday", "Luxury car", "birthday-audi.jpg", 540, 396,
            false, "Happy Birthday",
            "With many good wishes for your birthday and every day throughout the coming year."),
    };

    // Cards sent before the catalog existed stored the old enum name in the same column, so map
    // those onto the generated cards they were -- old rows keep showing a real card, not raw text.
    private static readonly Dictionary<string, string> LegacyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Birthday"] = "simple-birthday",
        ["ThankYou"] = "simple-thank-you",
        ["Holiday"] = "simple-holiday",
        ["Congratulations"] = "simple-congratulations",
    };

    public static ECardTemplate? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var match = All.FirstOrDefault(t => t.Key == key);
        if (match != null) return match;
        return LegacyKeys.TryGetValue(key, out var mapped) ? All.FirstOrDefault(t => t.Key == mapped) : null;
    }

    public static ECardTemplate Default => All[0];

    public static IEnumerable<IGrouping<string, ECardTemplate>> ByOccasion() =>
        All.GroupBy(t => t.Occasion);
}
