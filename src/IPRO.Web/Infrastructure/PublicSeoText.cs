using System.Net;
using System.Text.RegularExpressions;
using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

// 554 (2026-10-07): what a public page tells search engines about itself -- its title and its
// description -- when the site's owner has not written them.
//
// Every site was born with both fields FILLED: a starter page's Browser/SEO Title is its own page
// title ("Home") and its Search Description is "<Title> - professional service and support."; a new
// site's tagline is "Professional service and client support.". Filled fields are used as they
// are, so the day the first customer who is not an adviser moved in -- a bakery -- its home page's
// search result was titled "Home" and five of its seven pages described themselves as
// "Professional service and client support.". The owner's own site said the same.
//
// The rule now, in one place for the page's <head> and for the page editor's hints:
//   - What the owner typed is used, always.
//   - A starter value nobody touched counts as not written. Recognised here rather than cleaned out
//     of the table: every existing site is right from the next request, and nothing an owner typed
//     can be lost to a clean-up that guessed wrong.
//   - Not written, the title is the business's name (the home page) or "<Page> | <Business>", and
//     the description is the page's own opening words, then the site's tagline, then plain facts:
//     the page, the business and its town. Never a sentence about "professional services" -- the
//     product is sold to bakeries too.
public static class PublicSeoText
{
    // About what a search result shows before it cuts the text off.
    public const int DescriptionLength = 155;

    // The starter sentences. WebsiteStarterContentSeeder writes the first for every starter page
    // ("<Title> - professional service and support."), SuperAdmin's provisioning the same shape for
    // the Request Meeting page, and WebsiteProvisioning the second as every new site's tagline.
    private const string StarterDescriptionEnding = " - professional service and support.";
    public const string StarterTagLine = "Professional service and client support.";

    // The name the site goes by: its own title, else the person's name.
    public static string SiteName(AgentWebsite website)
    {
        if (!string.IsNullOrWhiteSpace(website.SiteTitle)) return website.SiteTitle.Trim();
        var agent = website.AgentUser;
        return agent == null ? string.Empty : $"{agent.FirstName} {agent.LastName}".Trim();
    }

    // A Browser/SEO Title the owner wrote: anything but the page's own title, which is what the
    // starter put there.
    public static bool HasOwnTitle(WebsitePage? page) =>
        page != null && !string.IsNullOrWhiteSpace(page.MetaTitle) && !Same(page.MetaTitle, page.Title);

    // A Search Description the owner wrote: anything but a starter sentence.
    public static bool HasOwnDescription(WebsitePage? page) =>
        page != null && !string.IsNullOrWhiteSpace(page.MetaDescription) && !IsStarterSentence(page.MetaDescription);

    public static bool IsStarterSentence(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return value.EndsWith(StarterDescriptionEnding, StringComparison.OrdinalIgnoreCase) || Same(value, StarterTagLine);
    }

    public static string Title(WebsitePage? page, string siteName, bool pageNotFound = false)
    {
        if (pageNotFound) return $"Page not found | {siteName}";
        if (HasOwnTitle(page)) return page!.MetaTitle.Trim();
        return page != null && !page.IsHomePage && !string.IsNullOrWhiteSpace(page.Title)
            ? $"{page.Title.Trim()} | {siteName}"
            : siteName;
    }

    public static string Description(WebsitePage? page, AgentWebsite website, bool pageNotFound = false)
    {
        var siteName = SiteName(website);
        if (pageNotFound) return $"That page could not be found on {siteName}.";
        if (HasOwnDescription(page)) return page!.MetaDescription.Trim();

        var words = OpeningWords(page);
        if (words.Length > 0) return words;

        if (!string.IsNullOrWhiteSpace(website.TagLine) && !IsStarterSentence(website.TagLine)) return website.TagLine.Trim();

        var agent = website.AgentUser;
        var place = string.Join(", ", new[] { agent?.City, agent?.Province }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        var business = place.Length > 0 ? $"{siteName}, {place}" : siteName;
        return page != null && !page.IsHomePage && !string.IsNullOrWhiteSpace(page.Title)
            ? $"{page.Title.Trim()} - {business}"
            : business;
    }

    // The page's own opening words: the lines under its first headings, in the order a visitor
    // reads them. Only the blocks that hold sentences -- a price list's Body is its introduction,
    // but a map's is an address and a video's a link.
    private static readonly string[] SentenceBlocks =
    {
        WebsiteBlockTypes.Hero, WebsiteBlockTypes.Text, WebsiteBlockTypes.CallToAction, WebsiteBlockTypes.PriceList
    };

    internal static string OpeningWords(WebsitePage? page)
    {
        if (page?.Blocks == null) return string.Empty;

        var text = string.Empty;
        foreach (var block in page.Blocks.Where(b => b.IsVisible && SentenceBlocks.Contains(b.BlockType)).OrderBy(b => b.SortOrder))
        {
            foreach (var part in new[] { Plain(block.Subheading), Plain(block.Body) })
            {
                // A line a second block repeats ("Our menu" over each of three price lists) is said once.
                if (part.Length == 0 || text.Contains(part, StringComparison.OrdinalIgnoreCase)) continue;
                if (text.Length > 0 && !".!?:;,".Contains(text[^1])) text += ".";
                text += (text.Length > 0 ? " " : string.Empty) + part;
            }

            // One block's worth is the page's opening; a second is read only when the first was a few words.
            if (text.Length >= 60) break;
        }

        // A page whose first heading is its only sentence ("A selection of drinks using our house
        // espresso") says more than a bare fact line does.
        if (text.Length == 0)
        {
            text = page.Blocks.Where(b => b.IsVisible && SentenceBlocks.Contains(b.BlockType)).OrderBy(b => b.SortOrder)
                .Select(b => Plain(b.Heading)).FirstOrDefault(heading => heading.Length >= 25) ?? string.Empty;
        }

        return Shorten(text);
    }

    // Cut at a word, never inside one, and never left hanging on a comma.
    internal static string Shorten(string text)
    {
        if (text.Length <= DescriptionLength) return text;
        var cut = text.LastIndexOf(' ', DescriptionLength);
        return (cut > 60 ? text[..cut] : text[..DescriptionLength]).TrimEnd(' ', ',', ';', ':', '-');
    }

    private static string Plain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = Regex.Replace(html, "<(br|/p|/div|/li|/h[1-6])[^>]*>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        return Regex.Replace(WebUtility.HtmlDecode(text), "\\s+", " ").Trim();
    }

    private static bool Same(string? a, string? b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
}
