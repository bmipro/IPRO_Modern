using System.Security.Cryptography;
using System.Text;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.SiteImport;

// 584 (2026-10-16): SuperAdmin's "Start the website again". Every page of an adviser's website (and
// its sections) goes, and the starter pack of the adviser's CURRENT business type comes in its place
// at once, written by the code sign-up uses (WebsiteStarterPagesHelper, WebsiteStarterResourcesHelper):
// the starter pages, Request Meeting with the business type's meeting form, Resources with its
// articles and calculators, Did You Know wired to real articles. Changing the business type alone
// changes no page (starter pages are made only for a site with none), so a demo account built from
// one pack could not show another.
//
// Everything that is not a page stays: the website itself (design, colours, logo, header, footer,
// domains, title), the media library, forms, contacts, leads and the adviser's articles. Articles of
// another business type's pack can be taken off the website (unpublished, never deleted), so a
// mortgage site does not show insurance articles in its blog.
//
// The site as it was is kept the way a Template Builder package keeps it (SiteSnapshot on a
// WebsiteBuilderImport row, marked StartAgainMarker), so BuilderImportService.RestoreAsync puts the
// previous pages back, one change at a time in the order they went in, and the kept copy protects the
// pictures it names (BlobReferences). Putting a start-again back leaves the logo and design as they
// are and gives the articles back the state they had.
public static class SiteStartAgainService
{
    // In WebsiteBuilderImport.PackageSha256, which for a real package is 64 hex characters.
    public const string StartAgainMarker = "starter-pack";

    public static bool IsStartAgain(WebsiteBuilderImport import) =>
        string.Equals(import.PackageSha256, StartAgainMarker, StringComparison.Ordinal);

    public sealed record PageLine(string Title, string Slug, bool IsHomePage, bool IsPublished, int PagesUnder);
    public sealed record ArticleLine(int Id, string Title, string FromPack);

    public sealed class Plan
    {
        public AgentUser Agent { get; init; } = null!;
        public AgentWebsite Site { get; init; } = null!;
        public string BusinessType { get; init; } = string.Empty;
        // What goes: the top of the menu, each with how many pages sit under it.
        public IReadOnlyList<PageLine> PagesGoing { get; init; } = Array.Empty<PageLine>();
        public int PageCountGoing { get; init; }
        public int SectionCountGoing { get; init; }
        // What comes: the starter pages, then Resources.
        public IReadOnlyList<string> StarterPages { get; init; } = Array.Empty<string>();
        public bool HasResources { get; init; }
        public int ResourcesArticleCount { get; init; }
        public IReadOnlyList<string> ResourcesGroups { get; init; } = Array.Empty<string>();
        public int CalculatorCount { get; init; }
        public bool MeetingFormComing { get; init; }
        public int NewArticleCount { get; init; }
        public int ReusedArticleCount { get; init; }
        public int FormsKept { get; init; }
        // Published articles of another business type's pack that the new pack does not have.
        public IReadOnlyList<ArticleLine> OtherPackArticles { get; init; } = Array.Empty<ArticleLine>();
        // The adviser's articles the new pages show that are not published (an earlier start-again took
        // them off, or the adviser did): published with the pages, as a new sign-up's are, so no
        // Resources page or Did You Know section comes up empty.
        public IReadOnlyList<ArticleLine> PackArticlesToPublish { get; init; } = Array.Empty<ArticleLine>();
        // Links added by hand to the header menu or the footer that point at a page that goes and does
        // not come back: they will find nothing.
        public IReadOnlyList<string> LinksToNowhere { get; init; } = Array.Empty<string>();
        // Template Builder work for this site that is still out: packages waiting for review, and the
        // newest hand-over when nothing has come back from it.
        public int PackagesWaiting { get; init; }
        public DateTime? OpenHandoverIssuedAt { get; init; }
        public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
        // What the screen showed; "Start the website again" writes only when it is still so.
        public string Fingerprint { get; init; } = string.Empty;
        public bool CanStart => Problems.Count == 0;
    }

    public sealed record Result(bool Ok, IReadOnlyList<string> Problems)
    {
        public int ImportId { get; init; }
        public string BusinessType { get; init; } = string.Empty;
        public int PagesRemoved { get; init; }
        public int SectionsRemoved { get; init; }
        public int PagesAdded { get; init; }
        public int SectionsAdded { get; init; }
        public int ArticlesUnpublished { get; init; }
        public int ArticlesPublished { get; init; }
        public int ArticlesAdded { get; init; }
        public int FormsAdded { get; init; }
        public bool SitePublished { get; init; }
        // The same screen was confirmed twice (a double click): the first press did it, the second
        // changed nothing.
        public bool AlreadyDone { get; init; }
        public static Result Failed(params string[] problems) => new(false, problems);
    }

    // What starting this adviser's website again would do, read as it is now. Null: no adviser, or
    // no website yet.
    public static async Task<Plan?> PlanAsync(IPRODbContext db, int agentId)
    {
        var agent = await db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent == null) return null;
        var site = await db.AgentWebsites.AsNoTracking().FirstOrDefaultAsync(w => w.AgentUserId == agentId);
        if (site == null) return null;
        var businessType = agent.BusinessType ?? string.Empty;

        var pages = await db.WebsitePages.AsNoTracking()
            .Where(p => p.AgentWebsiteId == site.Id)
            .Select(p => new { p.Id, p.ParentPageId, p.Title, p.Slug, p.IsHomePage, p.IsPublished, p.SortOrder, Sections = p.Blocks.Count })
            .ToListAsync();
        var byParent = pages.ToLookup(p => p.ParentPageId);
        // Counted once each, whatever the rows say (a page cannot be its own ancestor in the editor,
        // but a count must not depend on that).
        int Under(int id, HashSet<int> seen) => byParent[id].Where(child => seen.Add(child.Id)).Sum(child => 1 + Under(child.Id, seen));
        var ids = pages.Select(p => p.Id).ToHashSet();
        var top = pages
            .Where(p => !p.ParentPageId.HasValue || !ids.Contains(p.ParentPageId.Value))
            .OrderByDescending(p => p.IsHomePage).ThenBy(p => p.SortOrder).ThenBy(p => p.Id)
            .Select(p => new PageLine(p.Title, p.Slug, p.IsHomePage, p.IsPublished, Under(p.Id, new HashSet<int> { p.Id })))
            .ToList();

        var starterPages = await WebsiteStarterPagesHelper.SelectAsync(db, agent);
        var resourcesArticles = await WebsiteStarterResourcesHelper.SelectArticlesAsync(db, businessType);
        var calculators = VerticalCalculatorCatalog.ForBusinessType(businessType);
        var hasResources = resourcesArticles.Count > 0 || calculators.Count > 0;
        var meetingFormComing = starterPages.Any(p => p.Slug.Equals("request-meeting", StringComparison.OrdinalIgnoreCase))
            && await db.WebsiteStarterForms.AsNoTracking().AnyAsync(f => f.IsActive && f.Title == WebsiteStarterFormSeeder.MeetingFormTitle
                && (f.BusinessType == businessType || f.BusinessType == StarterBusinessTypes.All));

        // The articles the new pages name: Resources' and those of the Did You Know sections.
        var didYouKnowIds = starterPages.SelectMany(p => p.Blocks)
            .Where(b => b.BlockType == WebsiteBlockTypes.DidYouKnow)
            .SelectMany(b => WebsiteStarterDidYouKnowSettings.FromJson(b.SettingsJson).StarterArticleIds)
            .Distinct()
            .ToList();
        var didYouKnowTitles = didYouKnowIds.Count == 0
            ? new List<string>()
            : await db.WebsiteStarterArticles.AsNoTracking().Where(a => a.IsActive && didYouKnowIds.Contains(a.Id)).Select(a => a.Title).ToListAsync();
        var packTitles = resourcesArticles.Select(a => a.Title).Concat(didYouKnowTitles)
            .Select(Clean).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var articles = await db.Articles.AsNoTracking()
            .Where(a => a.AgentUserId == agentId)
            .OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.Title, a.IsPublished })
            .ToListAsync();
        var ownTitles = articles.Select(a => Clean(a.Title)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The article provisioning reuses for a title is the adviser's first with it.
        var toPublish = packTitles
            .Select(t => articles.FirstOrDefault(a => string.Equals(Clean(a.Title), t, StringComparison.OrdinalIgnoreCase)))
            .Where(a => a != null && !a.IsPublished)
            .Select(a => new ArticleLine(a!.Id, a.Title, businessType))
            .OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Id)
            .ToList();

        // Another pack's article: its title is a starter article of another business type, and no
        // starter article of this business type or of every pack ("All") has it, retired ones included.
        var starterTitles = await db.WebsiteStarterArticles.AsNoTracking().Select(a => new { a.BusinessType, a.Title }).ToListAsync();
        var thisPack = starterTitles
            .Where(a => a.BusinessType == businessType || a.BusinessType == StarterBusinessTypes.All)
            .Select(a => Clean(a.Title))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var otherPack = starterTitles
            .Where(a => a.BusinessType != businessType && a.BusinessType != StarterBusinessTypes.All && !thisPack.Contains(Clean(a.Title)))
            .GroupBy(a => Clean(a.Title), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(a => a.BusinessType).Distinct().OrderBy(t => t)), StringComparer.OrdinalIgnoreCase);
        var others = articles
            .Where(a => a.IsPublished && otherPack.ContainsKey(Clean(a.Title)))
            .OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Id)
            .Select(a => new ArticleLine(a.Id, a.Title, otherPack[Clean(a.Title)]))
            .ToList();

        // Header and footer links added by hand that name a page that goes and no new page takes its
        // address. A page is reached at /<its address>; the new pack's addresses are its starter pages',
        // Resources' and the addresses Resources makes from its titles.
        var comingSlugs = starterPages.Select(p => p.Slug)
            .Append("resources").Append("calculators")
            .Concat(resourcesArticles.Select(a => Slugify(a.Title)))
            .Concat(resourcesArticles.Select(a => a.Category ?? string.Empty).Where(c => c.Trim().Length > 0).Select(Slugify))
            .Concat(calculators.Select(c => Slugify(CalculatorKinds.DisplayName(c.Kind))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var goingSlugs = pages.Select(p => p.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var linksToNowhere = new List<string>();
        void CheckLink(string where, string label, string? url)
        {
            var address = (url ?? string.Empty).Trim();
            if (!address.StartsWith('/') || address.StartsWith("//", StringComparison.Ordinal)) return;
            var slug = address.Split('?', '#')[0].Trim('/');
            if (slug.Length == 0 || slug.Contains('/') || !goingSlugs.Contains(slug) || comingSlugs.Contains(slug)) return;
            linksToNowhere.Add($"{where} link \"{label}\" ({address})");
        }
        var header = WebsiteHeaderSettings.FromJson(site.HeaderSettingsJson);
        CheckLink("Header button", header.ButtonText, header.ButtonUrl);
        foreach (var link in header.CustomLinks.OrderBy(l => l.SortOrder)) CheckLink("Header menu", link.Label, link.Url);
        foreach (var link in WebsiteFooterSettings.FromJson(site.FooterSettingsJson).LegalLinks.OrderBy(l => l.SortOrder)) CheckLink("Footer", link.Label, link.Url);

        var packagesWaiting = await db.WebsiteBuilderImports.AsNoTracking()
            .CountAsync(i => i.AgentWebsiteId == site.Id && i.Status == WebsiteBuilderImportStatus.Pending);
        var handover = await SiteHandoverService.LatestAsync(db, site.Id);
        var handoverOpen = handover != null && !await db.WebsiteBuilderImports.AsNoTracking().AnyAsync(i =>
            i.AgentWebsiteId == site.Id && i.HandoverId == handover.HandoverId
            && (i.Status == WebsiteBuilderImportStatus.Applied || i.Status == WebsiteBuilderImportStatus.Restored));

        var problems = new List<string>();
        if (starterPages.Count == 0)
        {
            problems.Add(string.IsNullOrWhiteSpace(businessType)
                ? "This adviser has no business type, so there is no starter pack to start the website from. Choose one under Edit first. Nothing was changed."
                : $"There are no starter pages for the business type \"{businessType}\", so the website would be left with no pages. Check Starter Pages, or choose another business type under Edit. Nothing was changed.");
        }

        var plan = new Plan
        {
            Agent = agent,
            Site = site,
            BusinessType = businessType,
            PagesGoing = top,
            PageCountGoing = pages.Count,
            SectionCountGoing = pages.Sum(p => p.Sections),
            StarterPages = starterPages.Select(p => string.IsNullOrWhiteSpace(p.NavigationLabel) ? p.Title : p.NavigationLabel).ToList(),
            HasResources = hasResources,
            ResourcesArticleCount = resourcesArticles.Count,
            ResourcesGroups = resourcesArticles.Select(a => a.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            CalculatorCount = calculators.Count,
            MeetingFormComing = meetingFormComing,
            NewArticleCount = packTitles.Count(t => !ownTitles.Contains(t)),
            ReusedArticleCount = packTitles.Count(t => ownTitles.Contains(t)),
            FormsKept = await db.WebsiteForms.AsNoTracking().CountAsync(f => f.AgentUserId == agentId),
            OtherPackArticles = others,
            PackArticlesToPublish = toPublish,
            LinksToNowhere = linksToNowhere,
            PackagesWaiting = packagesWaiting,
            OpenHandoverIssuedAt = handoverOpen ? handover!.IssuedAt : null,
            Problems = problems,
            Fingerprint = Fingerprint(businessType, agent.PackageId, site.Id,
                pages.OrderBy(p => p.Id).Select(p => $"{p.Id}:{p.ParentPageId}:{p.Slug}:{p.Title}:{p.Sections}"),
                starterPages.Select(p => p.Id.ToString()),
                resourcesArticles.Select(a => a.Id.ToString()),
                others.Select(a => a.Id.ToString()),
                toPublish.Select(a => a.Id.ToString()))
        };
        return plan;
    }

    // Starts the website again, exactly as the plan the screen showed (reviewedPlan) says, once the
    // adviser's username has been typed. One transaction: on any refusal or failure nothing is written.
    public static async Task<Result> ApplyAsync(
        IPRODbContext db, int agentId, string? typedUserName, bool unpublishOtherPackArticles, string? reviewedPlan, string appliedBy)
    {
        var siteId = await db.AgentWebsites.AsNoTracking().Where(w => w.AgentUserId == agentId).Select(w => (int?)w.Id).FirstOrDefaultAsync();
        if (siteId == null) return Result.Failed("This adviser has no website yet, so there is nothing to start again.");

        await using var transaction = await db.Database.BeginTransactionAsync();
        // The same hold the Template Builder takes (BuilderImportService): one change of a site's pages
        // at a time, so a package brought in or put back at the same moment waits for this one.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT `Id` FROM `AgentWebsites` WHERE `Id` = {siteId.Value} FOR UPDATE");

        var plan = await PlanAsync(db, agentId);
        if (plan == null || plan.Site.Id != siteId.Value) return Result.Failed("This adviser has no website yet, so there is nothing to start again.");
        if (!string.Equals((typedUserName ?? string.Empty).Trim(), plan.Agent.UserName, StringComparison.Ordinal))
        {
            return Result.Failed($"The username typed is not \"{plan.Agent.UserName}\". Type it exactly as shown to confirm. Nothing was changed.");
        }
        if (!plan.CanStart) return new Result(false, plan.Problems);
        if (!string.Equals(reviewedPlan, plan.Fingerprint, StringComparison.Ordinal))
        {
            // The button pressed twice: the first press started the site again from this very screen.
            if (!string.IsNullOrEmpty(reviewedPlan) && await NewestStartedFromAsync(db, siteId.Value) == reviewedPlan)
            {
                return new Result(false, new[] { "The website was already started again from this screen (the button was pressed twice). Nothing more was changed." }) { AlreadyDone = true };
            }
            return Result.Failed("The website or the starter pack changed after this screen was opened, so it no longer says what would happen. Nothing was changed. Read the screen again, then confirm.");
        }

        var now = DateTime.UtcNow;
        var site = await db.AgentWebsites.FirstAsync(w => w.Id == siteId.Value);
        var rows = await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id).Include(p => p.Blocks).ToListAsync();
        var snapshot = SiteSnapshot.Capture(site, rows);
        var pagesRemoved = rows.Count;
        var sectionsRemoved = rows.Sum(r => r.Blocks.Count);

        // Parents are cleared first, so the delete never meets the database's own "set null".
        foreach (var row in rows) row.ParentPageId = null;
        await db.SaveChangesAsync();
        foreach (var row in rows)
        {
            db.WebsiteContentBlocks.RemoveRange(row.Blocks);
            db.WebsitePages.Remove(row);
        }
        await db.SaveChangesAsync();

        var unpublished = new List<int>();
        if (unpublishOtherPackArticles && plan.OtherPackArticles.Count > 0)
        {
            var otherIds = plan.OtherPackArticles.Select(a => a.Id).ToList();
            foreach (var article in await db.Articles.Where(a => a.AgentUserId == agentId && a.IsPublished && otherIds.Contains(a.Id)).ToListAsync())
            {
                article.IsPublished = false;
                article.UpdatedAt = now;
                unpublished.Add(article.Id);
            }
            await db.SaveChangesAsync();
        }

        var articlesBefore = (await db.Articles.Where(a => a.AgentUserId == agentId).Select(a => a.Id).ToListAsync()).ToHashSet();
        var formsBefore = (await db.WebsiteForms.Where(f => f.AgentUserId == agentId).Select(f => f.Id).ToListAsync()).ToHashSet();

        // Sign-up's own provisioning: the site has no page now, so the starter pages go in, then Resources.
        await WebsiteStarterPagesHelper.EnsureStarterPagesAsync(db, site, agentId);
        await WebsiteStarterResourcesHelper.EnsureResourcesAsync(db, site, agentId);

        var added = await db.WebsitePages.AsNoTracking().Where(p => p.AgentWebsiteId == site.Id)
            .Select(p => new { p.Id, Sections = p.Blocks.Count }).ToListAsync();
        if (added.Count == 0)
        {
            // Never a site with no pages: the transaction goes unwritten.
            db.ChangeTracker.Clear();
            return Result.Failed($"The starter pack for \"{plan.BusinessType}\" made no pages, so nothing was changed.");
        }

        // A sign-up's starter articles are published. Provisioning reuses an article the adviser already
        // has under the same title as it is, so one an earlier start-again took off the website (or the
        // adviser unpublished) would leave its Resources page and the Did You Know sections empty: the
        // articles the new pages show are published with them.
        var shown = (await db.WebsiteContentBlocks.AsNoTracking()
                .Where(b => b.WebsitePage.AgentWebsiteId == site.Id
                            && (b.BlockType == WebsiteBlockTypes.ArticleContent || b.BlockType == WebsiteBlockTypes.DidYouKnow))
                .Select(b => new { b.BlockType, b.SettingsJson })
                .ToListAsync())
            .SelectMany(b => b.BlockType == WebsiteBlockTypes.ArticleContent
                ? new List<int> { WebsiteArticleContentSettings.FromJson(b.SettingsJson).ArticleId }
                : WebsiteDidYouKnowSettings.FromJson(b.SettingsJson).ArticleIds)
            .Distinct()
            .ToList();
        var republished = new List<int>();
        foreach (var article in await db.Articles.Where(a => a.AgentUserId == agentId && !a.IsPublished && shown.Contains(a.Id)).ToListAsync())
        {
            article.IsPublished = true;
            article.PublishedAt ??= now;
            article.UpdatedAt = now;
            republished.Add(article.Id);
        }
        await db.SaveChangesAsync();

        var articlesAdded = (await db.Articles.Where(a => a.AgentUserId == agentId).Select(a => a.Id).ToListAsync()).Where(id => !articlesBefore.Contains(id)).OrderBy(id => id).ToList();
        var formsAdded = (await db.WebsiteForms.Where(f => f.AgentUserId == agentId).Select(f => f.Id).ToListAsync()).Where(id => !formsBefore.Contains(id)).OrderBy(id => id).ToList();

        snapshot.StartedAgain = new SiteSnapshot.StartAgainRecord
        {
            BusinessType = plan.BusinessType,
            UnpublishedArticleIds = unpublished.OrderBy(id => id).ToList(),
            AddedArticleIds = articlesAdded,
            RepublishedArticleIds = republished.OrderBy(id => id).ToList(),
            AddedFormIds = formsAdded,
            ReviewedPlan = plan.Fingerprint
        };
        var record = new WebsiteBuilderImport
        {
            AgentUserId = agentId,
            AgentWebsiteId = site.Id,
            Status = WebsiteBuilderImportStatus.Applied,
            FileName = Clip($"Started again: {plan.BusinessType} starter pack", 200),
            PackageSha256 = StartAgainMarker,
            TemplateName = Clip($"{plan.BusinessType} starter pack", 60),
            PageCount = added.Count,
            BlockCount = added.Sum(p => p.Sections),
            TemplateJson = string.Empty,
            AssetMapJson = "[]",
            SnapshotJson = snapshot.ToJson(),
            UploadedBy = Clip(appliedBy, 100),
            AppliedBy = Clip(appliedBy, 100),
            CreatedAt = now,
            AppliedAt = now
        };
        // A site that had no page has nothing to put back: no record is kept, so it never stands in front
        // of an older Template Builder package that could still be put back (the review's 584 re-check).
        if (pagesRemoved > 0) db.WebsiteBuilderImports.Add(record);
        site.UpdatedAt = now;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return new Result(true, Array.Empty<string>())
        {
            ImportId = record.Id,
            BusinessType = plan.BusinessType,
            PagesRemoved = pagesRemoved,
            SectionsRemoved = sectionsRemoved,
            PagesAdded = added.Count,
            SectionsAdded = added.Sum(p => p.Sections),
            ArticlesUnpublished = unpublished.Count,
            ArticlesPublished = republished.Count,
            ArticlesAdded = articlesAdded.Count,
            FormsAdded = formsAdded.Count,
            SitePublished = site.IsPublished
        };
    }

    // The screen the site's newest start-again was confirmed from, while it is the newest change of
    // the site's pages.
    private static async Task<string?> NewestStartedFromAsync(IPRODbContext db, int websiteId)
    {
        var latest = await BuilderImportService.LatestAppliedIdAsync(db, websiteId);
        if (latest == null) return null;
        var json = await db.WebsiteBuilderImports.AsNoTracking()
            .Where(i => i.Id == latest.Value && i.PackageSha256 == StartAgainMarker)
            .Select(i => i.SnapshotJson)
            .FirstOrDefaultAsync();
        return SiteSnapshot.FromJson(json)?.StartedAgain?.ReviewedPlan;
    }

    // The site's newest change of pages, when it is a start-again that can still be put back. A
    // Template Builder package brought in after it is put back first, on the Template Builder screen.
    public static async Task<WebsiteBuilderImport?> PutBackableAsync(IPRODbContext db, int websiteId)
    {
        var latest = await BuilderImportService.LatestAppliedIdAsync(db, websiteId);
        if (latest == null) return null;
        return await db.WebsiteBuilderImports.AsNoTracking()
            .Where(i => i.Id == latest.Value && i.PackageSha256 == StartAgainMarker)
            .Select(i => new WebsiteBuilderImport
            {
                Id = i.Id, AgentUserId = i.AgentUserId, AgentWebsiteId = i.AgentWebsiteId, Status = i.Status, FileName = i.FileName,
                PackageSha256 = i.PackageSha256, TemplateName = i.TemplateName, PageCount = i.PageCount, BlockCount = i.BlockCount,
                AppliedBy = i.AppliedBy, CreatedAt = i.CreatedAt, AppliedAt = i.AppliedAt
            })
            .FirstOrDefaultAsync();
    }

    // How many pages a put-back would bring back, read from the kept copy.
    public static async Task<int> PagesKeptAsync(IPRODbContext db, int importId)
    {
        var json = await db.WebsiteBuilderImports.AsNoTracking().Where(i => i.Id == importId).Select(i => i.SnapshotJson).FirstOrDefaultAsync();
        return SiteSnapshot.FromJson(json)?.Pages.Count ?? 0;
    }

    private static string Fingerprint(string businessType, int packageId, int siteId, params IEnumerable<string>[] parts)
    {
        var text = new StringBuilder($"{businessType}\n{packageId}\n{siteId}");
        foreach (var part in parts) text.Append('\n').Append(string.Join("|", part));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static string Clean(string? title) => (title ?? string.Empty).Trim();

    // A page address from a title, as WebsiteStarterResourcesHelper makes it.
    private static string Slugify(string title) =>
        System.Text.RegularExpressions.Regex.Replace(title.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    private static string Clip(string? value, int length)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length <= length ? value : value[..length];
    }
}
