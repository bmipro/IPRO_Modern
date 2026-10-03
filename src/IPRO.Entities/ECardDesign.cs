namespace IPRO.Entities;

// A card design managed by SuperAdmin. Replaces the hardcoded catalog that shipped with the
// feature: adding an occasion is now an upload and a form, not a code change and a deploy.
//
// Two kinds. An artwork design points at an uploaded image; a generated design is a colour panel
// built from the agent's own accent, which is what makes it usable for an occasion nobody has
// commissioned art for yet.
public static class ECardArtKinds
{
    public const string Image = "image";
    public const string Generated = "generated";

    public static readonly string[] All = { Image, Generated };
}

// 546: where an artwork card's greeting goes. Most designs leave the picture text-free and the
// greeting is set below it on the card's ground. The 2026 collection added two other kinds, and
// printing a greeting band under them would say everything twice:
//   TitleInPicture -- lettering art ("THANK YOU", "happy birthday"): the title is drawn into the
//                     picture, so only the message goes below it.
//   InPicture      -- the whole greeting is printed on a note inside the picture (the songbird
//                     thank-you, the rose anniversary, the marquee, the sleigh). Nothing goes below,
//                     and the wording is the approved one; agents cannot change text inside a
//                     picture (the owner's choice, 2026-10-03).
// Simple (generated) cards have no picture, so they are always Below.
public static class ECardGreetingStyles
{
    public const string Below = "below";
    public const string TitleInPicture = "title-in-picture";
    public const string InPicture = "in-picture";

    public static readonly string[] All = { Below, TitleInPicture, InPicture };
}

public class ECardDesign
{
    public int Id { get; set; }

    // Stable identifier stored on every ECard that used this design. Never reassigned, because
    // historical sends resolve their thumbnail and layout through it.
    public string Key { get; set; } = string.Empty;

    public string Occasion { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = ECardArtKinds.Image;

    public string DefaultHeaderText { get; set; } = string.Empty;
    public string DefaultMessage { get; set; } = string.Empty;

    // Artwork designs. ImageUrl is site-relative ("/images/ecard-art/x.jpg") for the designs that
    // shipped in wwwroot, or absolute for anything uploaded to blob storage since.
    public string ImageUrl { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }

    // Generated designs: the gradient runs from the agent's accent colour to this one.
    public string Accent { get; set; } = string.Empty;
    public string Emoji { get; set; } = string.Empty;

    // Whether the greeting band sits on a dark or light ground, so the card reads as one object
    // rather than artwork with a mismatched strip below it.
    public bool IsDark { get; set; }

    // Retired designs disappear from the agent's picker but keep rendering on past sends --
    // deleting one would blank the thumbnail on every e-card that ever used it.
    public bool IsActive { get; set; } = true;

    // Does this card still go to someone who has unsubscribed, if they explicitly asked to keep
    // receiving greetings? Intended for birthday and anniversary designs.
    //
    // Defaults to FALSE on purpose: an unsubscribe stops everything unless a human has ticked this
    // box for a specific design, so a design added next year cannot silently inherit an exemption
    // nobody chose.
    //
    // This is a real column rather than a rule matched on Occasion, because Occasion is a design
    // FAMILY and not a category -- `simple-birthday` is filed under "Simple" while `birthday-audi`
    // is filed under "Birthday". Any string rule would let one birthday card through and block the
    // other, which reads as a bug months later.
    public bool SendAfterUnsubscribe { get; set; } = false;

    // 546: see ECardGreetingStyles.
    public string GreetingStyle { get; set; } = ECardGreetingStyles.Below;

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsArtwork => Kind == ECardArtKinds.Image;

    // Only an artwork card can carry text in its picture; anything else reads as Below.
    public bool TitleIsInPicture => IsArtwork && GreetingStyle is ECardGreetingStyles.TitleInPicture or ECardGreetingStyles.InPicture;
    public bool MessageIsInPicture => IsArtwork && GreetingStyle == ECardGreetingStyles.InPicture;

    // Artwork shipped in IPRO.Web's wwwroot. Each file there has a small copy under thumbs/ for the
    // picker and the lists (546: the 2026 collection is 49 pictures of up to 300 KB at 1240 px, which
    // a picker tile shows at about 165 px); a test holds every shipped file to that.
    public const string ShippedArtPath = "/images/ecard-art/";

    // The picker tile's image: the thumbs/ copy of shipped art, or the picture itself for anything
    // uploaded since (blob storage).
    public string PickerImageUrl =>
        ImageUrl.StartsWith(ShippedArtPath, StringComparison.Ordinal) && ImageUrl.IndexOf('/', ShippedArtPath.Length) < 0
            ? ShippedArtPath + "thumbs/" + ImageUrl[ShippedArtPath.Length..]
            : ImageUrl;

    /// <summary>
    /// Artwork URL that works from outside the web app. The seeded designs live in IPRO.Web's
    /// wwwroot and carry a site-relative path, which resolves correctly on the agent portal but
    /// 404s anywhere else -- that is why every thumbnail was blank on the admin screens, and why
    /// an e-card email needs this too. Anything uploaded to blob storage is already absolute and
    /// is returned untouched.
    /// </summary>
    public string AbsoluteImageUrl(string baseUrl) => Absolute(ImageUrl, baseUrl);

    // The same, for the picker-size copy (the admin list shows sixty-odd of these on one page).
    public string AbsolutePickerImageUrl(string baseUrl) => Absolute(PickerImageUrl, baseUrl);

    private static string Absolute(string url, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
        return $"{(baseUrl ?? string.Empty).TrimEnd('/')}{url}";
    }
}
