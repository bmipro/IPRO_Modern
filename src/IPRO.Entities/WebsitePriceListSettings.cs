using System.Text.Json;

namespace IPRO.Entities;

// 549: a price list or a menu on an agent's website -- sections of items, each with a name, a price
// written as the business writes it, and optionally a line of description, a badge, a photo and a
// "not available" switch. Built generic on purpose (the owner: "a generic with flexibility that would
// looks as good for a bakery and accountant"): a bakery's Breads and Drinks, an accountant's fee
// schedule ("Personal tax return -- from $150").
//
// The price is free text, not a number: real menus say "$3.50 / $3.75 / $4.10" for three sizes,
// "$7.50 Medium, $10.00 Large", "$12 / dozen", "from $150". Nothing here does arithmetic with it.
public static class PriceListKinds
{
    // Products and services: marked up as a schema.org OfferCatalog.
    public const string Prices = "prices";
    // Food and drink: marked up as a schema.org Menu, which is what search engines read for a menu.
    public const string Menu = "menu";

    public static readonly string[] All = { Prices, Menu };

    public static string Normalize(string? kind) => kind == Menu ? Menu : Prices;
}

public static class PriceListText
{
    private static readonly System.Text.RegularExpressions.Regex Single =
        new(@"^\$?\s*(\d{1,6}(?:\.\d{1,2})?)\s*\$?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // The price as a number for search engines, when it is one plain amount ("$4.25", "4", "$12.50");
    // null for anything else ("from $25", "$3.50 / $3.75", "$12 a dozen"), which stays words on the page.
    public static string? SingleAmount(string? price)
    {
        var match = Single.Match((price ?? string.Empty).Trim());
        return match.Success ? match.Groups[1].Value : null;
    }
}

public class WebsitePriceListItem
{
    public const int NameMax = 120;
    public const int PriceMax = 60;
    public const int DescriptionMax = 300;
    public const int BadgeMax = 30;

    public string Name { get; set; } = string.Empty;
    public string Price { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    // A short label shown beside the name: "New", "Vegan", "Most popular".
    public string Badge { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    // Off shows the item as unavailable instead of hiding it -- a seasonal item, today's sold-out bread.
    public bool Available { get; set; } = true;
}

public class WebsitePriceListSection
{
    public const int TitleMax = 100;
    public const int NoteMax = 300;

    public string Title { get; set; } = string.Empty;
    // A line under the section: "*Add a drink to any pizza for $1", "Prices include HST".
    public string Note { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public List<WebsitePriceListItem> Items { get; set; } = new();
}

public class WebsitePriceListSettings
{
    // Far above any real menu (the bakery's longer page is 7 sections, about 50 items), low enough
    // that a crafted post cannot store a page nobody could load.
    public const int MaxSections = 30;
    public const int MaxItemsPerSection = 100;
    public const int MaxItems = 400;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public string Kind { get; set; } = PriceListKinds.Prices;
    public List<WebsitePriceListSection> Sections { get; set; } = new();

    public bool HasItems => Sections.Any(s => s.Items.Count > 0);

    public static WebsitePriceListSettings FromJson(string? json) =>
        TryParse(json, out var settings) ? settings : new();

    // False for anything that is not a price list -- the editor's save keeps the stored list then,
    // rather than replacing it with an empty one because a post arrived without (or with broken) data.
    public static bool TryParse(string? json, out WebsitePriceListSettings settings)
    {
        settings = new();
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<WebsitePriceListSettings>(json, ReadOptions);
            if (parsed?.Sections == null) return false;
            settings = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    // What may be stored from what the editor posted: every field trimmed and cut to its length, an
    // item without a name and a section with neither title nor items dropped, the counts capped, and
    // only picture addresses the caller's rule accepts (site-relative or http/https) kept.
    public static WebsitePriceListSettings Clean(WebsitePriceListSettings posted, Func<string?, string> cleanUrl)
    {
        var clean = new WebsitePriceListSettings { Kind = PriceListKinds.Normalize(posted.Kind) };
        var total = 0;
        foreach (var section in posted.Sections ?? new())
        {
            if (section == null) continue;
            if (clean.Sections.Count == MaxSections) break;
            var kept = new WebsitePriceListSection
            {
                Title = Cut(section.Title, WebsitePriceListSection.TitleMax),
                Note = Cut(section.Note, WebsitePriceListSection.NoteMax),
                ImageUrl = cleanUrl(section.ImageUrl)
            };
            foreach (var item in section.Items ?? new())
            {
                if (item == null) continue;
                var name = Cut(item.Name, WebsitePriceListItem.NameMax);
                if (name.Length == 0) continue;
                if (kept.Items.Count == MaxItemsPerSection || total == MaxItems) break;
                kept.Items.Add(new WebsitePriceListItem
                {
                    Name = name,
                    Price = Cut(item.Price, WebsitePriceListItem.PriceMax),
                    Description = Cut(item.Description, WebsitePriceListItem.DescriptionMax),
                    Badge = Cut(item.Badge, WebsitePriceListItem.BadgeMax),
                    ImageUrl = cleanUrl(item.ImageUrl),
                    Available = item.Available
                });
                total++;
            }
            if (kept.Title.Length > 0 || kept.Items.Count > 0) clean.Sections.Add(kept);
        }
        return clean;
    }

    // The starting point of a new block: one section showing every kind of field, for the agent to
    // overwrite. Generic wording -- it reads as sensibly on a bakery's site as on an accountant's.
    public static WebsitePriceListSettings Starter() => new()
    {
        Kind = PriceListKinds.Prices,
        Sections =
        {
            new WebsitePriceListSection
            {
                Title = "Section title",
                Note = "An optional note for this section.",
                Items =
                {
                    new WebsitePriceListItem { Name = "First item", Price = "$10", Description = "A short description of what is included." },
                    new WebsitePriceListItem { Name = "Second item", Price = "from $25", Badge = "Popular" }
                }
            }
        }
    };

    private static string Cut(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max].TrimEnd();
    }
}
