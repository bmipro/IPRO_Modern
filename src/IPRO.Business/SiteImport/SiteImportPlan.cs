using IPRO.Entities;

namespace IPRO.Business.SiteImport;

// 571: what bringing a package in would do to a site, worked out without touching anything. The
// review screen shows it, the look renders it and BuilderImportService writes exactly it, so the
// three cannot disagree.
public enum PageChange
{
    Added,      // in the package, not on the site
    Replaced,   // in the package and on the site: the page keeps its row and its search settings
    Kept,       // on the site, not in the package: left as it is
    Removed     // on the site, not in the package, and the operator chose to remove it
}

public sealed class PlannedPage
{
    // The page as it will be, sections included (for a removed page: as it is now). A page that does
    // not exist yet, and every new section, carries a negative Id until it is written.
    public WebsitePage Page { get; init; } = null!;
    public PageChange Change { get; init; }
    // The section types the page has on the site today (empty for an added page).
    public IReadOnlyList<string> SectionsBefore { get; init; } = Array.Empty<string>();

    // Sections a replaced or removed page has today that the builder cannot carry (a map, a gallery,
    // a price list...). They do not come back with the package.
    public IReadOnlyList<string> SectionsLost => Change is PageChange.Replaced or PageChange.Removed
        ? SectionsBefore.Where(t => !BuilderContract.BlockTypes.Contains(t, StringComparer.Ordinal)).ToList()
        : Array.Empty<string>();
}

public enum LogoChange { Unchanged, Set, Replaced, Removed }

public sealed class SiteImportPlan
{
    public IReadOnlyList<PlannedPage> Pages { get; init; } = Array.Empty<PlannedPage>();
    // What stops the package from being brought in as chosen. Empty = it can go in.
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    public string LogoUrlAfter { get; init; } = string.Empty;
    public LogoChange Logo { get; init; }

    // The site's pages afterwards, in menu order, as the public site would load them.
    public IReadOnlyList<WebsitePage> PagesAfter =>
        Pages.Where(p => p.Change != PageChange.Removed).Select(p => p.Page).ToList();

    public static SiteImportPlan Build(
        BuilderTemplate template,
        IReadOnlyDictionary<string, string> imageUrls,
        AgentWebsite site,
        IReadOnlyList<WebsitePage> currentPages,
        IReadOnlySet<int> removePageIds)
    {
        var problems = new List<string>();
        var nextId = -1;
        int TempId() => nextId--;
        string Image(string imageRef) => imageRef.Length > 0 && imageUrls.TryGetValue(imageRef, out var url) ? url : string.Empty;

        // 1. Which row of the site each package page lands on. The home page is the home page,
        //    whatever either side calls it; every other page is matched by its address.
        var rowFor = new Dictionary<BuilderPage, WebsitePage>();
        var taken = new HashSet<int>();
        var packageHome = template.Pages.FirstOrDefault(p => p.IsHomePage);
        var siteHome = currentPages.FirstOrDefault(p => p.IsHomePage);
        if (packageHome != null && siteHome != null)
        {
            rowFor[packageHome] = siteHome;
            taken.Add(siteHome.Id);
        }
        foreach (var page in template.Pages.Where(p => !rowFor.ContainsKey(p)))
        {
            var row = currentPages.FirstOrDefault(r => !taken.Contains(r.Id) && string.Equals(r.Slug, page.Slug, StringComparison.OrdinalIgnoreCase));
            if (row == null) continue;
            rowFor[page] = row;
            taken.Add(row.Id);
        }

        // 2. The package's pages as they will be.
        var planned = new List<PlannedPage>();
        var bySlug = new Dictionary<string, WebsitePage>(StringComparer.Ordinal);
        foreach (var source in template.Pages)
        {
            rowFor.TryGetValue(source, out var row);
            var page = new WebsitePage
            {
                Id = row?.Id ?? TempId(),
                AgentWebsiteId = site.Id,
                Title = source.Title,
                Slug = source.Slug,
                NavigationLabel = source.NavigationLabel,
                IsHomePage = source.IsHomePage,
                SortOrder = source.SortOrder,
                // What the contract does not carry stays the page's own (section 12); a new page
                // gets iPro's defaults.
                MetaTitle = row?.MetaTitle ?? string.Empty,
                MetaDescription = row?.MetaDescription ?? string.Empty,
                OldAddresses = row?.OldAddresses ?? string.Empty,
                ShowInNavigation = row?.ShowInNavigation ?? true,
                IsPublished = row?.IsPublished ?? true,
                CreatedAt = row?.CreatedAt ?? DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            page.Blocks = source.Blocks.OrderBy(b => b.SortOrder).Select(b => new WebsiteContentBlock
            {
                Id = TempId(),
                WebsitePageId = page.Id,
                BlockType = b.Type,
                Heading = b.Heading,
                Subheading = b.Subheading,
                Body = b.Body,
                ImageUrl = Image(b.ImageRef),
                ButtonText = b.ButtonText,
                ButtonUrl = b.ButtonUrl,
                SettingsJson = b.SettingsJson,
                LayoutVariant = b.LayoutVariant,
                SortOrder = b.SortOrder,
                IsVisible = b.IsVisible
            }).ToList();
            bySlug[source.Slug] = page;
            planned.Add(new PlannedPage
            {
                Page = page,
                Change = row == null ? PageChange.Added : PageChange.Replaced,
                SectionsBefore = row == null ? Array.Empty<string>() : SectionTypes(row)
            });
        }
        foreach (var source in template.Pages.Where(p => p.ParentSlug != null))
        {
            bySlug[source.Slug].ParentPageId = bySlug.TryGetValue(source.ParentSlug!, out var parent) ? parent.Id : null;
        }

        // 3. The site's other pages: kept as they are, or removed when the operator said so.
        var others = currentPages.Where(r => !taken.Contains(r.Id)).ToList();
        var removed = others.Where(r => removePageIds.Contains(r.Id)).Select(r => r.Id).ToHashSet();
        var parentOf = currentPages.ToDictionary(r => r.Id, r => r.ParentPageId);
        foreach (var row in others)
        {
            if (removed.Contains(row.Id))
            {
                planned.Add(new PlannedPage { Page = row, Change = PageChange.Removed, SectionsBefore = SectionTypes(row) });
                continue;
            }

            // A page whose parent goes moves up to that parent's parent, exactly as deleting a page in
            // the page editor does (WebsitePagesController.Delete).
            var parentId = row.ParentPageId;
            while (parentId.HasValue && removed.Contains(parentId.Value)) parentId = parentOf.GetValueOrDefault(parentId.Value);
            // A replaced parent may itself have moved: it keeps its row, so the link holds.
            var kept = new WebsitePage
            {
                Id = row.Id,
                AgentWebsiteId = row.AgentWebsiteId,
                ParentPageId = parentId,
                Title = row.Title,
                Slug = row.Slug,
                NavigationLabel = row.NavigationLabel,
                MetaTitle = row.MetaTitle,
                MetaDescription = row.MetaDescription,
                OldAddresses = row.OldAddresses,
                IsHomePage = false,   // the package's home page is the home page now
                ShowInNavigation = row.ShowInNavigation,
                IsPublished = row.IsPublished,
                SortOrder = row.SortOrder,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
                Blocks = row.Blocks.OrderBy(b => b.SortOrder).ToList()
            };
            planned.Add(new PlannedPage { Page = kept, Change = PageChange.Kept, SectionsBefore = SectionTypes(row) });

            if (bySlug.ContainsKey(row.Slug))
            {
                problems.Add($"The site already has a page at \"/{row.Slug}\" (\"{row.Title}\"), and the package's home page takes that address. " +
                    "Remove that page below, or give the home page another address in the builder.");
            }
        }

        // 4. Menu order: beside the package's pages, a kept page follows them in the order it had.
        var live = planned.Where(p => p.Change != PageChange.Removed).ToList();
        foreach (var group in live.GroupBy(p => p.Page.ParentPageId ?? 0))
        {
            var fromPackage = group.Where(p => p.Change != PageChange.Kept).ToList();
            if (fromPackage.Count == 0) continue;
            var next = fromPackage.Max(p => p.Page.SortOrder) + 1;
            foreach (var kept in group.Where(p => p.Change == PageChange.Kept).OrderBy(p => p.Page.SortOrder).ThenBy(p => p.Page.Title, StringComparer.OrdinalIgnoreCase))
            {
                kept.Page.SortOrder = next++;
            }
        }

        // 5. No page ends up deeper than the menu goes.
        var after = live.ToDictionary(p => p.Page.Id, p => p.Page);
        foreach (var item in live)
        {
            var depth = 1;
            var parentId = item.Page.ParentPageId;
            var seen = new HashSet<int> { item.Page.Id };
            while (parentId.HasValue && after.TryGetValue(parentId.Value, out var above) && seen.Add(above.Id))
            {
                depth++;
                parentId = above.ParentPageId;
            }
            if (depth > BuilderContract.MaxDepth)
            {
                problems.Add($"\"{item.Page.Title}\" would end up {depth} levels deep in the menu, and a menu goes {BuilderContract.MaxDepth}. " +
                    (item.Change == PageChange.Kept ? "Remove that page below, or move it in the page editor first." : "Move it in the builder."));
            }
        }

        // 6. The logo. A package built from scratch that has none says nothing about the site's logo;
        //    a site that came back from a hand-over without one had it removed in the builder.
        var logoAfter = template.LogoRef.Length > 0 ? Image(template.LogoRef)
            : template.HandoverId.Length > 0 ? string.Empty
            : site.LogoUrl ?? string.Empty;
        var logoBefore = site.LogoUrl ?? string.Empty;
        var logo = logoAfter == logoBefore ? LogoChange.Unchanged
            : logoAfter.Length == 0 ? LogoChange.Removed
            : logoBefore.Length == 0 ? LogoChange.Set
            : LogoChange.Replaced;

        var ordered = planned
            .OrderBy(p => p.Change == PageChange.Removed ? 1 : 0)
            .ThenBy(p => p.Page.SortOrder)
            .ThenBy(p => p.Page.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SiteImportPlan { Pages = ordered, Problems = problems, LogoUrlAfter = logoAfter, Logo = logo };
    }

    private static IReadOnlyList<string> SectionTypes(WebsitePage page) =>
        page.Blocks.OrderBy(b => b.SortOrder).Select(b => b.BlockType).ToList();
}
