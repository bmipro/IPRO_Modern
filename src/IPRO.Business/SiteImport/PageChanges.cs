using IPRO.Entities;

namespace IPRO.Business.SiteImport;

// 585 (the owner's bench walk-through, 2026-10-19): what bringing a return in changes on one page, in
// plain sentences, from the page as the site has it today to the page as it will be. Every page of a
// return is "replaced" (it keeps its row and takes the package's sections), so the review said the same
// of all 18 of the owner's pages when he had changed two; which pages the customer changed is the
// reviewer's first question about a send from the new builder.
//
// A section is compared by what a visitor sees of it: its kind, its words, its picture, its button, its
// layout and style, and whether it shows. What the contract leaves out for its kind is not compared (a
// contact form's button words are never shown and never travel), settings are read as the site reads
// them (WebsiteHeroSettings and the others fill in their defaults), and line ends and the spaces around
// words do not count: a page the round trip only re-spells reads as unchanged. A locked box comes back
// as the site's own section (SiteImportPlan), so it compares as the same.
public static class PageChanges
{
    private const char Field = (char)0x1F;

    // before: the page's sections on the site today, after: as they will be, each in page order.
    public static IReadOnlyList<string> Between(WebsitePage row, IEnumerable<WebsiteContentBlock> before, WebsitePage page, IEnumerable<WebsiteContentBlock> after)
    {
        var said = new List<string>();
        if (Clean(row.Title) != Clean(page.Title))
            said.Add($"Its title: \"{Clean(row.Title)}\" becomes \"{Clean(page.Title)}\".");
        if (Clean(row.NavigationLabel) != Clean(page.NavigationLabel))
            said.Add($"Its menu label: \"{Label(row)}\" becomes \"{Label(page)}\".");

        var was = before.OrderBy(b => b.SortOrder).ThenBy(b => b.Id).Select(Section.Of).ToList();
        var now = after.OrderBy(b => b.SortOrder).Select(Section.Of).ToList();
        var wasMatched = new bool[was.Count];
        var nowMatched = new bool[now.Count];
        var lines = new List<(int Order, string Text)>();

        // 1. In place: the longest run of sections that read the same, in the same order.
        foreach (var (i, j) in InPlace(was, now))
        {
            wasMatched[i] = true;
            nowMatched[j] = true;
        }
        // 2. Moved: a section that reads the same, elsewhere on the page.
        for (var j = 0; j < now.Count; j++)
        {
            if (nowMatched[j]) continue;
            var i = Enumerable.Range(0, was.Count).FirstOrDefault(x => !wasMatched[x] && was[x].Says == now[j].Says, -1);
            if (i < 0) continue;
            wasMatched[i] = nowMatched[j] = true;
            lines.Add((j, $"Moved: {now[j].Name} (now section {j + 1} of {now.Count})."));
        }
        // 3. Changed: the next section of the same kind, in page order, that is not accounted for.
        for (var j = 0; j < now.Count; j++)
        {
            if (nowMatched[j]) continue;
            var i = Enumerable.Range(0, was.Count).FirstOrDefault(x => !wasMatched[x] && was[x].Type == now[j].Type, -1);
            if (i < 0) continue;
            wasMatched[i] = nowMatched[j] = true;
            lines.Add((j, $"Changed: {was[i].Name}: {What(was[i], now[j])}."));
        }
        // 4. What is new, and what goes.
        for (var j = 0; j < now.Count; j++)
        {
            if (!nowMatched[j]) lines.Add((j, $"New section: {now[j].Name}."));
        }
        for (var i = 0; i < was.Count; i++)
        {
            if (!wasMatched[i]) lines.Add((now.Count + i, $"Removed: {was[i].Name}."));
        }
        said.AddRange(lines.OrderBy(l => l.Order).Select(l => l.Text));
        return said;
    }

    private static string What(Section was, Section now)
    {
        var parts = new List<string>();
        if (was.Words != now.Words) parts.Add("its words");
        if (was.Picture != now.Picture)
            parts.Add(was.Picture.Length == 0 ? "a picture added" : now.Picture.Length == 0 ? "its picture taken out" : "a new picture");
        if (was.Button != now.Button) parts.Add("its button");
        if (was.Look != now.Look) parts.Add("its layout or style");
        if (was.Shown != now.Shown) parts.Add(now.Shown ? "now shown to visitors" : "now hidden from visitors");
        return parts.Count switch
        {
            0 => "it reads differently",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + ", and " + parts[^1]
        };
    }

    // The pairs (before, after) of the longest common run of sections that read the same.
    private static IEnumerable<(int, int)> InPlace(IReadOnlyList<Section> was, IReadOnlyList<Section> now)
    {
        var length = new int[was.Count + 1, now.Count + 1];
        for (var i = was.Count - 1; i >= 0; i--)
            for (var j = now.Count - 1; j >= 0; j--)
                length[i, j] = was[i].Says == now[j].Says ? length[i + 1, j + 1] + 1 : Math.Max(length[i + 1, j], length[i, j + 1]);
        for (int i = 0, j = 0; i < was.Count && j < now.Count;)
        {
            if (was[i].Says == now[j].Says) { yield return (i, j); i++; j++; }
            else if (length[i + 1, j] >= length[i, j + 1]) i++;
            else j++;
        }
    }

    private static string Label(WebsitePage page) => Clean(page.NavigationLabel).Length > 0 ? Clean(page.NavigationLabel) : Clean(page.Title);

    private static string Clean(string? text) => (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    // A section as a visitor sees it.
    private sealed record Section(string Type, string Name, string Words, string Picture, string Button, string Look, bool Shown)
    {
        public string Says => string.Join(Field, Type, Words, Picture, Button, Look, Shown ? "shown" : "hidden");

        public static Section Of(WebsiteContentBlock block)
        {
            var type = block.BlockType ?? string.Empty;
            var name = SiteImportPlan.Named(type, block.Heading);
            var words = string.Join(Field, Clean(block.Heading), Clean(block.Subheading), Clean(block.Body));
            if (!BuilderContract.BlockTypes.Contains(type, StringComparer.Ordinal))
            {
                // A section the builder cannot carry is the site's own, compared whole.
                return new Section(type, name, words, Clean(block.ImageUrl), Clean(block.ButtonText) + Field + Clean(block.ButtonUrl),
                    Clean(block.LayoutVariant) + Field + (string.IsNullOrWhiteSpace(block.SettingsJson) ? "{}" : block.SettingsJson.Trim()), block.IsVisible);
            }

            // A kind the builder carries, read the way the hand-over writes it (SiteHandoverBuilder.BlockAsync):
            // what the contract leaves out for its kind is not part of it, a link's scheme is in lower case,
            // a Text section's button without a link (or words) is no button, and its layout is one of its kind's.
            var unused = BuilderContract.UnusedFields(type);
            string Used(string field, string? value) => unused.Contains(field) ? string.Empty : Clean(value);
            var buttonText = Used("buttonText", block.ButtonText);
            var buttonUrl = SiteHandoverBuilder.LowerScheme(Used("buttonUrl", block.ButtonUrl));
            if (type == WebsiteBlockTypes.Text && (buttonText.Length == 0) != (buttonUrl.Length == 0)) buttonText = buttonUrl = string.Empty;
            return new Section(
                type,
                name,
                string.Join(Field, Used("heading", block.Heading), Used("subheading", block.Subheading), Used("body", block.Body)),
                Used("imageRef", block.ImageUrl),
                buttonText + Field + buttonUrl,
                WebsiteBlockLayoutVariants.Normalize(type, block.LayoutVariant) + Field + Settings(type, block.SettingsJson),
                block.IsVisible);
        }

        // Settings as the site reads them, so that a default written out equals one left out.
        private static string Settings(string type, string? json)
        {
            switch (type)
            {
                case WebsiteBlockTypes.Hero: return WebsiteHeroSettings.FromJson(json).ToJson();
                case WebsiteBlockTypes.ContactForm: return WebsiteContactFormSettings.FromJson(json).ToJson();
                case WebsiteBlockTypes.ArticleContent: return WebsiteArticleContentSettings.FromJson(json).ToJson();
                case WebsiteBlockTypes.Form: return WebsiteFormSettings.FromJson(json).ToJson();
                case WebsiteBlockTypes.Blog:
                    var blog = WebsiteBlogSettings.FromJson(json);
                    return new WebsiteBlogSettings { PostCount = blog.EffectivePostCount, ShowImages = blog.ShowImages }.ToJson();
                default:
                    return string.IsNullOrWhiteSpace(json) ? "{}" : json.Trim();
            }
        }
    }
}
