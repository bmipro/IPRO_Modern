using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

/// <summary>
/// 579 (2026-10-15): every customer website's accessibility statement, at /accessibility, linked from
/// its footer. Nothing to set up: the page is written from what the site already has (its name, its
/// language, its contact details) and sent from PublicWebsiteController when the site has no page of
/// its own at that address. A site owner who wants other words makes a page called "accessibility"
/// and it wins, as a real page always does. Its words are in Views/PublicWebsite/_PublicAccessibilityStatement.cshtml.
///
/// What the statement may claim is what 578's checks showed, and no more: the designs pass automated
/// WCAG 2.1 AA checks and keyboard checks; no person has checked them with a screen reader yet, so it
/// claims no conformance. When a release changes what the statement says, change the words and
/// <see cref="UpdatedOn"/> together.
/// </summary>
public static class AccessibilityStatement
{
    public const string Slug = "accessibility";
    public const string Path = "/" + Slug;

    /// <summary>The date the statement's words were last changed, shown on every customer's statement.</summary>
    public static readonly DateOnly UpdatedOn = new(2026, 10, 15);

    /// <summary>The same for iPro's own statement (Views/Home/Accessibility.cshtml), which changes on its own.</summary>
    public static readonly DateOnly PlatformUpdatedOn = new(2026, 10, 15);

    /// <summary>
    /// True for exactly /accessibility (any letter case). A longer address (/accessibility/x) is not
    /// the statement: it answers 404 like any other address that is not a page. Compared as it came,
    /// not through OldAddresses.Normalize, which reads "/x.y/accessibility" or "/%2561ccessibility" as
    /// an old site's address and would answer them all with the statement.
    /// </summary>
    public static bool IsAddress(string? requestedAddress) =>
        string.Equals(requestedAddress?.Trim('/'), Slug, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The owner's footer already links to a statement (theirs anywhere, or this one by its address):
    /// the footer does not add a second one. "Accessib" covers Accessibility and Accessibilité.
    /// </summary>
    public static bool IsLinked(IEnumerable<WebsiteFooterLink> legalLinks) =>
        legalLinks.Any(link =>
            string.Equals(OldAddresses.Normalize(link.Url), Slug, StringComparison.Ordinal) ||
            (link.Label ?? string.Empty).Contains("accessib", StringComparison.OrdinalIgnoreCase));

    /// <summary>The footer link and the page's heading, in the website's language.</summary>
    public static string LinkText(string language) => language == WebsiteLanguages.French ? "Accessibilité" : "Accessibility";

    public static string Title(string language, string siteName) => $"{LinkText(language)} | {siteName}";

    public static string Description(string language, string siteName) => language == WebsiteLanguages.French
        ? $"Ce que {siteName} fait pour que ce site soit accessible, et comment demander de l'aide ou un autre format."
        : $"What {siteName} does to make this website accessible, and how to ask for help or another format.";

    /// <summary>
    /// "15 October 2026" or "15 octobre 2026", the way iPro's legal pages write a date (French writes the
    /// first of a month "1er"). The month names are spelled out here rather than asked of the server's
    /// culture data, which a host can lack.
    /// </summary>
    public static string Date(string language) => Date(language, UpdatedOn);

    public static string Date(string language, DateOnly on) => language == WebsiteLanguages.French
        ? $"{(on.Day == 1 ? "1er" : on.Day.ToString(System.Globalization.CultureInfo.InvariantCulture))} {FrenchMonths[on.Month - 1]} {on.Year}"
        : $"{on.Day} {EnglishMonths[on.Month - 1]} {on.Year}";

    private static readonly string[] EnglishMonths =
        { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
    private static readonly string[] FrenchMonths =
        { "janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre" };
}
