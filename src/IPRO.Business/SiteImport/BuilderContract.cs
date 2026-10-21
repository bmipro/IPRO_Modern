using System.Text.RegularExpressions;
using IPRO.Entities;

namespace IPRO.Business.SiteImport;

// 571: the Template Builder export contract, v0.4, as iPro's importer reads it. The contract itself
// is CONTRACT.md (iPro owns it; the builder's developer works from it and never sees this code), so
// every number and list here has a section of that document behind it, named beside it.
public static class BuilderContract
{
    public const string Version = "0.4";                                  // section 0
    // 576, v0.5: v0.4 plus host sections, and stated only where it is used (section 0). The
    // hand-over of a site that has such a section says "0.5", and so does its return, even one that
    // comes back with every box removed. Every other package says "0.4" and is byte for byte what it was.
    public const string VersionWithHostSections = "0.5";
    public static readonly string[] Versions = { Version, VersionWithHostSections };

    // Section 4.10: a section the builder does not carry, travelling as a locked box. Not one of
    // iPro's own section types and never stored as one: on the way back it becomes the section it
    // stands for (SiteImportPlan).
    public const string HostSection = "HostSection";
    // Section 9.3, catalog.hostSections[].kind: iPro's own name for the kind of section.
    public static readonly Regex HostSectionKind = new(@"\A[A-Za-z][A-Za-z0-9]{0,39}\z", RegexOptions.CultureInvariant);
    public const int MaxHostSectionLabel = 60, MaxHostSectionTitle = 200;

    // Section 1: the package.
    public const long MaxZipBytes = 40L * 1024 * 1024;                    // rule 5
    public const long MaxAssetBytes = 8L * 1024 * 1024;                   // rule 5
    public const int MaxFileNameLength = 100;                             // clarification of 2026-09-29
    public const string TemplateFile = "template.json";
    public const string AssetFolder = "assets/";

    // Not contract rules: how far the importer will read before it stops. No package within section 1
    // comes near them (images do not compress, so a 40 MB package holds about 40 MB).
    public const long MaxTemplateJsonBytes = 16L * 1024 * 1024;
    public const long MaxExpandedBytes = 120L * 1024 * 1024;
    public const int MaxEntries = 2000;
    public const int MaxProblemsReported = 100;

    public static readonly Regex AssetFileName =
        new(@"\A[a-z0-9][a-z0-9-]*\.(jpg|jpeg|png|gif|webp)\z", RegexOptions.CultureInvariant);       // rule 4
    public static readonly Regex Slug = new(@"\A[a-z0-9]+(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant); // sections 2, 3
    public static readonly Regex HandoverId = new(@"\Ah_[0-9a-f]{16}\z", RegexOptions.CultureInvariant);   // section 9.2

    public static readonly string[] Themes = { "modern", "classic", "editorial" };                    // section 2

    // Section 4: the eight export types, verbatim.
    public static readonly string[] BlockTypes =
    {
        WebsiteBlockTypes.Hero, WebsiteBlockTypes.Text, WebsiteBlockTypes.Services, WebsiteBlockTypes.CallToAction,
        WebsiteBlockTypes.ContactForm, WebsiteBlockTypes.ArticleContent, WebsiteBlockTypes.Blog, WebsiteBlockTypes.Form
    };

    // Sections 4.7 and 4.9: these two point at content one adviser owns, so they exist only in a
    // package that came from a hand-over.
    public static readonly string[] HandoverOnlyTypes = { WebsiteBlockTypes.ArticleContent, WebsiteBlockTypes.Form };

    // Sections 4.3 to 4.9 and section 10, rule 7: the fields a type does not use, which a package
    // writes as an empty string. A value there would be stored and never shown, so it is refused
    // rather than dropped without a word.
    public static string[] UnusedFields(string type) => type switch
    {
        WebsiteBlockTypes.Services => new[] { "imageRef", "buttonText", "buttonUrl" },
        WebsiteBlockTypes.CallToAction => new[] { "imageRef" },
        WebsiteBlockTypes.ContactForm => new[] { "imageRef", "buttonText", "buttonUrl" },
        WebsiteBlockTypes.ArticleContent => new[] { "heading", "subheading", "body", "imageRef", "buttonText", "buttonUrl" },
        WebsiteBlockTypes.Blog => new[] { "imageRef", "buttonText", "buttonUrl" },
        WebsiteBlockTypes.Form => new[] { "imageRef", "buttonText", "buttonUrl" },
        HostSection => new[] { "heading", "subheading", "body", "imageRef", "buttonText", "buttonUrl" },
        _ => Array.Empty<string>()
    };

    // Section 4.1.
    public static readonly string[] HeroLayouts = { "split", "image-left", "centered", "full-image", "compact" };
    public static readonly string[] HeroImagePositions = { "center", "left", "right", "top", "bottom" };
    public static readonly string[] HeroTextAlignments = { "left", "center" };
    public static readonly string[] HeroHeights = { "compact", "standard", "tall" };
    public const int HeroOverlayMax = 85;

    public const int MaxPages = 30, MaxBlocksPerPage = 40, MaxDepth = 3;                              // section 3
    public const int MaxSlug = 80, MaxTitle = 120, MaxNavigationLabel = 40, MaxLogoAltText = 80;
    public const int MaxHeading = 200, MaxSubheading = 300, MaxBody = 10_000, MaxButtonText = 60, MaxButtonUrl = 500; // section 4

    // Section 3, "Reserved slugs". The first group is what Program.cs never surrenders to a
    // customer's site on that customer's own domain (IsNeverShadowedPrefix; a test keeps the two in
    // step), then /portal, and the two addresses the public site answers itself. A page given one of
    // these names could never be reached.
    public static readonly string[] ReservedSlugs =
    {
        "account", "billing", "publicwebsite", "media", "hangfire", "health", "error",
        "clientportal", "clientportalaccount", "clientportalappointments", "clientportaldocuments",
        "clientportalmessages", "clientportalpreferences", "clientportalprofile",
        "invoice", "poll", "testimonial", "email-preferences", "t",
        "portal", "sitemap", "robots"
    };

    // iPro's page editor keeps the address "home" for the home page alone.
    public const string HomeSlug = "home";

    public static string ContentTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => string.Empty
    };

    // Section 1, rule 6: the file is what its name says. (585: iPro's own picture uploads judge a
    // picture by its first bytes alone, IPRO.Utility.UploadedPicture; a package names its files.)
    public static bool HasImageSignature(ReadOnlySpan<byte> bytes, string fileName)
    {
        if (bytes.Length < 6) return false;
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            ".png" => bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".gif" => bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8),
            ".webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
    }

    // Section 4, buttonUrl: nothing, a path on this site, an https address, or a mail or phone link.
    // "//host" is another site under a path's clothing, and browsers read "/\host" the same way; they
    // also drop a tab or a line break inside an address, so no control character and no backslash is
    // allowed anywhere. A space is allowed only where people write one: inside a phone number.
    public static bool IsAllowedButtonUrl(string value)
    {
        if (value.Length == 0) return true;
        if (value.Any(char.IsControl) || value.Contains('\\')) return false;
        var phone = value.StartsWith("tel:", StringComparison.Ordinal);
        if (!phone && value.Any(char.IsWhiteSpace)) return false;
        if (value[0] == '/') return value.Length == 1 || value[1] != '/';
        return (value.StartsWith("https://", StringComparison.Ordinal) && value.Length > "https://".Length)
            || (value.StartsWith("mailto:", StringComparison.Ordinal) && value.Length > "mailto:".Length)
            || (phone && value["tel:".Length..].Trim().Length > 0);
    }
}
