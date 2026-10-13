using System.Security.Cryptography;
using System.Text;
using IPRO.Entities;

namespace IPRO.Business.SiteImport;

// 574: what a page says, as one value. Taken when a site is handed to the builder and again when the
// package comes back: a page whose value differs was changed in iPro in between, and the return is
// about to replace that change.
//
// A timestamp cannot answer this. The page editor stamps the section it saves, not the page, and
// adding, deleting or moving a section stamps nothing at all (the audit of 572 found section edits
// replaced without a word). The value covers exactly what a return overwrites: the page's name,
// address and place in the menu, and its sections in order with everything they show. It leaves out
// what the contract does not carry and a return therefore keeps (search title and description, the
// published and in-menu switches, old addresses), and it leaves out ids and times, so a site put
// back by "put the previous site back" reads as unchanged.
public static class SiteFingerprint
{
    private const char Field = (char)0x1F, Record = (char)0x1E;

    // Every page of a site, by its row id. The parent goes in by its address: ids do not survive a
    // put-back, addresses do.
    public static IReadOnlyDictionary<int, string> OfPages(IReadOnlyList<WebsitePage> pages)
    {
        var slugById = pages.ToDictionary(p => p.Id, p => p.Slug ?? string.Empty);
        return pages.ToDictionary(p => p.Id, p => OfPage(p, p.ParentPageId.HasValue && slugById.TryGetValue(p.ParentPageId.Value, out var parent) ? parent : string.Empty));
    }

    public static string OfPage(WebsitePage page, string parentSlug)
    {
        var text = new StringBuilder();
        void Add(string? value) => text.Append(value ?? string.Empty).Append(Field);
        Add(page.Title);
        Add(page.Slug);
        Add(page.NavigationLabel);
        Add(parentSlug);
        Add(page.IsHomePage ? "home" : string.Empty);
        Add(page.SortOrder.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var block in (page.Blocks ?? new List<WebsiteContentBlock>()).OrderBy(b => b.SortOrder).ThenBy(b => b.Id))
        {
            text.Append(Record);
            Add(block.BlockType);
            Add(block.Heading);
            Add(block.Subheading);
            Add(block.Body);
            Add(block.ImageUrl);
            Add(block.ButtonText);
            Add(block.ButtonUrl);
            Add(block.LayoutVariant);
            Add(block.SettingsJson);
            Add(block.IsVisible ? "shown" : "hidden");
        }
        return Hash(text.ToString());
    }

    public static string OfLogo(string? logoUrl) => Hash(logoUrl ?? string.Empty);

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
