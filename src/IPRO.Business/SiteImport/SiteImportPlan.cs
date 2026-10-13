using System.Security.Cryptography;
using System.Text;
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
    Removed     // on the site, not in the package, and it goes
}

public sealed class PlannedPage
{
    // The page as it will be, sections included (for a removed page: as it is now). A page that does
    // not exist yet, and every new section, carries a negative Id until it is written.
    public WebsitePage Page { get; init; } = null!;
    public PageChange Change { get; init; }
    // The section types the page has on the site today (empty for an added page).
    public IReadOnlyList<string> SectionsBefore { get; init; } = Array.Empty<string>();
    // 572: the page was handed to the builder and did not come back: it was removed there. (A page
    // made in iPro after the hand-over was never in the builder, and is simply kept.)
    public bool RemovedInBuilder { get; init; }
    // When the page was last changed on the site (null for an added page). A returning package
    // replaces whatever was changed in iPro after its hand-over; the review says which pages.
    public DateTime? ChangedOnSite { get; init; }
    // 574: what the page says today is not what its hand-over recorded: it was changed in iPro after the
    // site went to the builder, and this return replaces (or removes) that change. Decided here, from
    // the content, and not in the view from a timestamp: the page editor stamps a saved section and not
    // its page, and adding, deleting or moving a section stamps nothing.
    public bool ChangedSinceHandover { get; init; }

    // Sections a replaced or removed page has today that this package could not have carried (a map,
    // a gallery, a price list; for a site built from scratch also a form or an article section, which
    // only a hand-over can hold). They do not come back with the package.
    public IReadOnlyList<string> SectionsLost { get; init; } = Array.Empty<string>();
}

public enum LogoChange { Unchanged, Set, Replaced, Removed }

public sealed class SiteImportPlan
{
    public IReadOnlyList<PlannedPage> Pages { get; init; } = Array.Empty<PlannedPage>();
    // What stops the package from being brought in as chosen. Empty = it can go in.
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    public string LogoUrlAfter { get; init; } = string.Empty;
    public LogoChange Logo { get; init; }
    // 574: the site's logo is not the one that was handed over, and this return changes it.
    public bool LogoChangedSinceHandover { get; init; }
    // 574: what this plan would do, as one value. The review screen posts it back with "bring it in";
    // if the site has changed in between so that the plan is no longer the one that was read, the
    // write refuses and the review is shown again.
    public string Fingerprint { get; init; } = string.Empty;

    // The site's pages afterwards, in menu order, as the public site would load them.
    public IReadOnlyList<WebsitePage> PagesAfter =>
        Pages.Where(p => p.Change != PageChange.Removed).Select(p => p.Page).ToList();

    // removePageIds: pages the operator chose to remove. handedOverPageIds: the pages a hand-over
    // carried (empty for a site built from scratch); one of those missing from the package was
    // removed in the builder and goes, unless the operator keeps it (keepPageIds).
    public static SiteImportPlan Build(
        BuilderTemplate template,
        IReadOnlyDictionary<string, string> imageUrls,
        AgentWebsite site,
        IReadOnlyList<WebsitePage> currentPages,
        IReadOnlySet<int> removePageIds,
        IReadOnlySet<int>? handedOverPageIds = null,
        IReadOnlySet<int>? keepPageIds = null,
        IEnumerable<string>? knownProblems = null,
        // 574, for a return. handedOverRowBySlug: the address a page had when it was handed over, to the
        // row it is today (a page renamed in iPro since is still that page). changedSinceHandover: the
        // rows whose content is not what the hand-over recorded. carriedTypes: the section types this
        // package could carry (default: the contract's eight).
        IReadOnlyDictionary<string, int>? handedOverRowBySlug = null,
        IReadOnlySet<int>? changedSinceHandover = null,
        bool logoChangedSinceHandover = false,
        IReadOnlySet<string>? carriedTypes = null)
    {
        IReadOnlyList<string> Lost(IReadOnlyList<string> before) =>
            before.Where(t => carriedTypes == null ? !BuilderContract.BlockTypes.Contains(t, StringComparer.Ordinal) : !carriedTypes.Contains(t)).ToList();
        var problems = new List<string>(knownProblems ?? Array.Empty<string>());
        var nextId = -1;
        int TempId() => nextId--;
        string Image(string imageRef) => imageRef.Length > 0 && imageUrls.TryGetValue(imageRef, out var url) ? url : string.Empty;
        foreach (var missing in template.UsedImageRefs.Where(r => !imageUrls.ContainsKey(r)).Take(1))
        {
            // Without this a page would be written with its pictures quietly gone.
            problems.Add($"The record of where this package's pictures were stored is incomplete ({missing}). Drop the package and upload it again.");
        }

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
        // 574: a page given another address in iPro after the hand-over is still the page that went to
        // the builder. Matched by the address it had then, it is replaced on its own row (and keeps
        // its search settings); before, it was removed and the package's page added beside the gap.
        if (handedOverRowBySlug != null)
        {
            foreach (var page in template.Pages.Where(p => !rowFor.ContainsKey(p)))
            {
                if (!handedOverRowBySlug.TryGetValue(page.Slug, out var rowId) || taken.Contains(rowId)) continue;
                var row = currentPages.FirstOrDefault(r => r.Id == rowId);
                if (row == null) continue;
                rowFor[page] = row;
                taken.Add(row.Id);
            }
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
                OldAddresses = OldAddressesAfter(row, source.Slug),
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
                SectionsBefore = row == null ? Array.Empty<string>() : SectionTypes(row),
                SectionsLost = row == null ? Array.Empty<string>() : Lost(SectionTypes(row)),
                ChangedOnSite = row?.UpdatedAt,
                ChangedSinceHandover = row != null && (changedSinceHandover?.Contains(row.Id) ?? false)
            });
        }
        foreach (var source in template.Pages.Where(p => p.ParentSlug != null))
        {
            bySlug[source.Slug].ParentPageId = bySlug.TryGetValue(source.ParentSlug!, out var parent) ? parent.Id : null;
        }

        // 3. The site's other pages: kept as they are, or removed -- because the operator said so,
        //    or because the page went to the builder and was removed there.
        var others = currentPages.Where(r => !taken.Contains(r.Id)).ToList();
        var removedInBuilder = others
            .Where(r => handedOverPageIds != null && handedOverPageIds.Contains(r.Id) && !(keepPageIds?.Contains(r.Id) ?? false))
            .Select(r => r.Id).ToHashSet();
        var removed = others.Where(r => removePageIds.Contains(r.Id) || removedInBuilder.Contains(r.Id)).Select(r => r.Id).ToHashSet();
        var parentOf = currentPages.ToDictionary(r => r.Id, r => r.ParentPageId);
        foreach (var row in others)
        {
            if (removed.Contains(row.Id))
            {
                planned.Add(new PlannedPage
                {
                    Page = row, Change = PageChange.Removed, SectionsBefore = SectionTypes(row), SectionsLost = Lost(SectionTypes(row)),
                    RemovedInBuilder = removedInBuilder.Contains(row.Id), ChangedOnSite = row.UpdatedAt,
                    ChangedSinceHandover = changedSinceHandover?.Contains(row.Id) ?? false
                });
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
            planned.Add(new PlannedPage { Page = kept, Change = PageChange.Kept, SectionsBefore = SectionTypes(row), ChangedOnSite = row.UpdatedAt });

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
        return new SiteImportPlan
        {
            Pages = ordered, Problems = problems, LogoUrlAfter = logoAfter, Logo = logo,
            LogoChangedSinceHandover = logoChangedSinceHandover && logo != LogoChange.Unchanged,
            Fingerprint = FingerprintOf(ordered, problems, logo, logoAfter)
        };
    }

    // Everything the review screen says about a plan: each page, what happens to it, what it loses and
    // whether it was changed in iPro; the logo; what stands in the way.
    private static string FingerprintOf(IReadOnlyList<PlannedPage> pages, IReadOnlyList<string> problems, LogoChange logo, string logoAfter)
    {
        const char field = (char)0x1F;
        var text = new StringBuilder();
        foreach (var item in pages)
        {
            text.Append(item.Change).Append(field)
                .Append(item.Page.Id > 0 ? item.Page.Id : 0).Append(field)
                .Append(item.Page.Slug).Append(field)
                .Append(item.Page.Title).Append(field)
                .Append(item.Page.Blocks?.Count ?? 0).Append(field)
                .Append(string.Join(",", item.SectionsBefore)).Append(field)
                .Append(string.Join(",", item.SectionsLost)).Append(field)
                .Append(item.RemovedInBuilder).Append(item.ChangedSinceHandover).Append((char)0x1E);
        }
        text.Append(logo).Append(field).Append(logoAfter).Append(field).Append(string.Join(((char)0x1E).ToString(), problems));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant()[..32];
    }

    private static IReadOnlyList<string> SectionTypes(WebsitePage page) =>
        page.Blocks.OrderBy(b => b.SortOrder).Select(b => b.BlockType).ToList();

    // A page that keeps its row but changes its address (only the home page can: it is matched as
    // the home page, not by address) remembers the address it had, so links and search results that
    // point at the old one still arrive (553: OldAddresses, one per line).
    private static string OldAddressesAfter(WebsitePage? row, string newSlug)
    {
        var current = row?.OldAddresses ?? string.Empty;
        if (row == null || string.Equals(row.Slug, newSlug, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(row.Slug)) return current;
        var lines = current.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (lines.Contains(row.Slug, StringComparer.OrdinalIgnoreCase)) return current;
        lines.Add(row.Slug);
        var joined = string.Join("\n", lines);
        return joined.Length <= 2000 ? joined : current;   // the column's size; an address too many is not worth a failed import
    }
}
