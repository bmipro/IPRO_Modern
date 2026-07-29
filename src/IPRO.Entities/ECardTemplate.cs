namespace IPRO.Entities;

// How a card's artwork and its greeting text combine. Taken from the legacy designs rather than
// invented: the artwork itself dictates where text can legibly go, so this travels with the image.
public static class ECardLayouts
{
    // Art fills the top panel; greeting sits over it in white. Contact block on black beneath.
    public const string DarkOverlay = "dark-overlay";
    // Art has a light area designed into it (e.g. the blank card nested in the roses);
    // greeting sits over that area in dark text. Contact block on white beneath.
    public const string LightOverlay = "light-overlay";
    // Art already carries its own lettering, so the greeting goes below it rather than on top.
    public const string LightBanner = "light-banner";
}

public record ECardTemplate(
    string Key,
    string Occasion,
    string Name,
    string FileName,
    int Width,
    int Height,
    string Layout,
    string DefaultHeaderText,
    string DefaultMessage)
{
    public string Url => $"/images/ecard-art/{FileName}";
    public bool IsDark => Layout == ECardLayouts.DarkOverlay;
}

public static class ECardTemplateCatalog
{
    // Greeting copy is the legacy wording, recovered verbatim from the 2014 database dump
    // (ecardtemplate.DefaultHeaderText / DefaultMessage) so returning agents see what they knew.
    public static IReadOnlyList<ECardTemplate> All { get; } = new List<ECardTemplate>
    {
        new("halloween-1", "Halloween", "Zombie couple", "halloween1.jpg", 700, 525,
            ECardLayouts.DarkOverlay, "Happy Halloween", "Have a very special Halloween"),
        new("halloween-2", "Halloween", "Zombie clown and doll", "halloween2.jpg", 700, 525,
            ECardLayouts.DarkOverlay, "Happy Halloween", "Have a very special Halloween"),
        new("halloween-3", "Halloween", "Hell hound", "halloween3.jpg", 700, 525,
            ECardLayouts.DarkOverlay, "Happy Halloween", "Have a very special Halloween"),
        new("halloween-4", "Halloween", "Zombie pair dancing", "halloween4.jpg", 700, 525,
            ECardLayouts.DarkOverlay, "Happy Halloween", "Have a very special Halloween"),
        new("halloween-5", "Halloween", "Nurse and pumpkin", "halloween5.jpg", 700, 525,
            ECardLayouts.DarkOverlay, "Happy Halloween", "Have a very special Halloween"),
        new("halloween-6", "Halloween", "Mechanic zombies", "halloween6.jpg", 700, 525,
            ECardLayouts.DarkOverlay, "Happy Halloween", "Have a very special Halloween"),
        new("halloween-7", "Halloween", "Zombie horde", "halloween7.jpg", 700, 525,
            ECardLayouts.DarkOverlay, "Happy Halloween", "Have a very special Halloween"),

        new("anniversary-1", "Anniversary", "Red roses", "anniversary1.jpg", 467, 311,
            ECardLayouts.LightOverlay, "Happy anniversary",
            "May this very special love always be yours to share"),
        new("anniversary-2", "Anniversary", "Red roses (alternate)", "anniversary2.jpg", 467, 311,
            ECardLayouts.LightOverlay, "Happy anniversary",
            "May this very special love always be yours to share"),

        new("birthday-audi", "Birthday", "Luxury car", "birthday-audi.jpg", 540, 396,
            ECardLayouts.LightBanner, "Happy Birthday",
            "With many good wishes for your birthday and every day throughout the coming year."),
    };

    public static ECardTemplate? Find(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(t => t.Key == key);

    public static ECardTemplate Default => All[0];

    public static IEnumerable<IGrouping<string, ECardTemplate>> ByOccasion() =>
        All.GroupBy(t => t.Occasion);
}
