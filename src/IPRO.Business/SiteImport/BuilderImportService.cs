using System.Text.Json;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.SiteImport;

// 571: the importer's work on the database and storage. Four steps, each its own call:
//
//   StoreAsync    a package that passed every rule: its new images go into the site's media library,
//                 its template.json into a WebsiteBuilderImport row. The site itself is untouched.
//   PlanAsync     what bringing it in would do (SiteImportPlan), for the review screen and the look.
//   ApplyAsync    writes exactly that plan, after keeping the site as it was.
//   RestoreAsync  puts the site back as it was (DiscardAsync drops a package that never went in).
//
// 572: a package may also be the return of a site iPro handed to the builder (SiteHandoverService).
// It is then held to that hand-over: its section types, its articles and forms as they are NOW, and
// only the site's newest hand-over can come back.
//
// Static with the context passed in, like ClientRecycleBin: the admin app runs it today and the
// portal will when a customer returns from the builder.
public static class BuilderImportService
{
    public const string MediaContainer = "website-media";

    // Where an image the package uses is stored. Created = this import put it in the media library
    // (false: the site already held the same bytes, so the existing image is linked). MediaAssetId is
    // 0 for a picture that is not a row of the media library (a logo the hand-over delivered).
    public sealed record StoredImage(string Ref, string Url, int MediaAssetId, bool Created, string Sha256, long Bytes);

    public sealed record ApplyResult(bool Ok, IReadOnlyList<string> Problems)
    {
        public static ApplyResult Failed(params string[] problems) => new(false, problems);
        public static ApplyResult Done() => new(true, Array.Empty<string>());
    }

    // What a package for this site is checked against. packageHandoverId is what the package says
    // about itself (BuilderPackageReader.PeekHandoverId): empty for a site built from scratch in the
    // builder, else the hand-over it answers. Only the site's newest hand-over can come back; for any
    // other id the reader says which hand-over the site is waiting for.
    public static async Task<BuilderImportContext> ContextAsync(IPRODbContext db, AgentWebsite site, long storageLimitBytes, string? packageHandoverId = null)
    {
        var known = (await db.WebsiteMediaAssets.AsNoTracking()
            .Where(a => a.AgentWebsiteId == site.Id && a.ContentSha256 != "")
            .Select(a => a.ContentSha256)
            .Distinct()
            .ToListAsync()).ToHashSet(StringComparer.Ordinal);
        // A limit of zero or less means no limit, as in the page editor's own upload.
        var remaining = storageLimitBytes <= 0
            ? long.MaxValue
            : Math.Max(0, storageLimitBytes - await AgentStorage.TotalBytesAsync(db, site.AgentUserId));
        if (string.IsNullOrEmpty(packageHandoverId)) return BuilderImportContext.DesignerTemplate(known, remaining);

        var handover = await SiteHandoverService.LatestAsync(db, site.Id);
        if (handover == null) return BuilderImportContext.DesignerTemplate(known, remaining);
        var allowed = SiteHandoverService.List<string>(handover.AllowedBlockTypesJson).ToHashSet(StringComparer.Ordinal);
        if (handover.HandoverId != packageHandoverId)
        {
            return new BuilderImportContext(handover.HandoverId, allowed, new HashSet<int>(), new HashSet<int>(), known, remaining);
        }

        // The articles and forms the hand-over listed that are STILL the customer's published
        // articles and active forms: one unpublished since cannot be shown, so it is refused (section 7).
        var listedArticles = SiteHandoverService.List<int>(handover.ArticleIdsJson);
        var listedForms = SiteHandoverService.List<int>(handover.FormIdsJson);
        var articles = await db.Articles.AsNoTracking()
            .Where(a => a.AgentUserId == site.AgentUserId && a.IsPublished && listedArticles.Contains(a.Id)).Select(a => a.Id).ToListAsync();
        var forms = await db.WebsiteForms.AsNoTracking()
            .Where(f => f.AgentUserId == site.AgentUserId && f.IsActive && listedForms.Contains(f.Id)).Select(f => f.Id).ToListAsync();
        foreach (var delivered in SiteHandoverService.List<HandoverImage>(handover.DeliveredJson)) known.Add(delivered.Sha256);
        return new BuilderImportContext(handover.HandoverId, allowed, articles.ToHashSet(), forms.ToHashSet(), known, remaining);
    }

    public static async Task<WebsiteBuilderImport> StoreAsync(
        IPRODbContext db, IBlobStorageService blob, AgentWebsite site, BuilderPackage package, string fileName, string uploadedBy)
    {
        if (!package.IsValid) throw new InvalidOperationException("Only a package that passed every rule is stored.");
        var template = package.Template!;

        // Every picture the site already stores, by its content: the media library's, and for a
        // returning package the ones its hand-over delivered (a logo is not a row of the library).
        var byHash = new Dictionary<string, (string Url, int MediaAssetId)>(StringComparer.Ordinal);
        var existing = await db.WebsiteMediaAssets
            .Where(a => a.AgentWebsiteId == site.Id && a.ContentSha256 != "")
            .OrderBy(a => a.Id)
            .ToListAsync();
        foreach (var asset in existing) byHash.TryAdd(asset.ContentSha256, (asset.BlobUrl, asset.Id));
        if (template.HandoverId.Length > 0)
        {
            var handover = await SiteHandoverService.FindAsync(db, site.Id, template.HandoverId);
            foreach (var delivered in SiteHandoverService.List<HandoverImage>(handover?.DeliveredJson ?? "[]"))
            {
                byHash.TryAdd(delivered.Sha256, (delivered.Url, 0));
            }
        }

        // The images and the package's row are recorded together or not at all. Storage is outside
        // the transaction: what was uploaded for a package that was not recorded is taken back out.
        var created = new List<WebsiteMediaAsset>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            foreach (var image in package.Images.Values.OrderBy(i => i.Ref, StringComparer.Ordinal))
            {
                if (byHash.ContainsKey(image.Sha256)) continue;
                using var stream = new MemoryStream(image.Bytes, writable: false);
                var url = await blob.UploadAsync(stream, image.FileName, MediaContainer, image.ContentType, isPrivate: false);
                var asset = new WebsiteMediaAsset
                {
                    AgentWebsiteId = site.Id,
                    OriginalFileName = image.FileName,
                    BlobUrl = url,
                    ContentType = image.ContentType,
                    FileSize = image.Length,
                    ContentSha256 = image.Sha256
                };
                db.WebsiteMediaAssets.Add(asset);
                created.Add(asset);
                byHash[image.Sha256] = (url, 0);
            }
            await db.SaveChangesAsync();
            foreach (var asset in created) byHash[asset.ContentSha256] = (asset.BlobUrl, asset.Id);

            var createdHashes = created.Select(a => a.ContentSha256).ToHashSet(StringComparer.Ordinal);
            var stored = package.Images.Values
                .OrderBy(i => i.Ref, StringComparer.Ordinal)
                .Select(i => new StoredImage(i.Ref, byHash[i.Sha256].Url, byHash[i.Sha256].MediaAssetId, createdHashes.Contains(i.Sha256), i.Sha256, i.Length))
                .ToList();
            var import = new WebsiteBuilderImport
            {
                AgentUserId = site.AgentUserId,
                AgentWebsiteId = site.Id,
                Status = WebsiteBuilderImportStatus.Pending,
                FileName = Clip(Path.GetFileName(fileName), 200),
                PackageSha256 = package.Sha256,
                TemplateName = template.TemplateName,
                Theme = template.Theme,
                HandoverId = template.HandoverId,
                PageCount = template.Pages.Count,
                BlockCount = template.Pages.Sum(p => p.Blocks.Count),
                NewImageCount = created.Count,
                NewImageBytes = created.Sum(a => a.FileSize),
                TemplateJson = package.TemplateJson,
                AssetMapJson = JsonSerializer.Serialize(stored),
                UploadedBy = Clip(uploadedBy, 100),
                CreatedAt = DateTime.UtcNow
            };
            db.WebsiteBuilderImports.Add(import);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return import;
        }
        catch
        {
            await transaction.RollbackAsync();
            foreach (var asset in created)
            {
                db.Entry(asset).State = EntityState.Detached;
                await blob.DeleteAsync(asset.BlobUrl);
            }
            throw;
        }
    }

    public static IReadOnlyList<StoredImage> StoredImages(WebsiteBuilderImport import)
    {
        try { return JsonSerializer.Deserialize<List<StoredImage>>(import.AssetMapJson) ?? new List<StoredImage>(); }
        catch (JsonException) { return new List<StoredImage>(); }
    }

    // Handover: the hand-over a returning package answers (null for a site built from scratch).
    public sealed record PlanResult(BuilderPackage Package, SiteImportPlan? Plan, AgentWebsite Site, WebsiteBuilderHandover? Handover = null);

    // The stored package read again and laid over the site as it is now. Plan is null when the
    // package no longer passes (Package.Problems says why).
    public static async Task<PlanResult?> PlanAsync(
        IPRODbContext db, WebsiteBuilderImport import, IReadOnlySet<int>? removePageIds = null, IReadOnlySet<int>? keepPageIds = null)
    {
        var site = await db.AgentWebsites.AsNoTracking().Include(w => w.AgentUser).Include(w => w.Template)
            .FirstOrDefaultAsync(w => w.Id == import.AgentWebsiteId);
        if (site == null) return null;

        // Storage was settled when the package was stored (its images are already in the library).
        // What the site decides is asked again: for a return, that its hand-over is still the newest
        // and that its articles and forms are still published and active.
        var returned = import.HandoverId.Length > 0;
        var context = returned ? await ContextAsync(db, site, 0, import.HandoverId) : BuilderImportContext.DesignerTemplate();
        var handover = returned ? await SiteHandoverService.FindAsync(db, site.Id, import.HandoverId) : null;
        var package = BuilderPackageReader.ReadStored(import.TemplateJson, context);
        if (package.Template == null) return new PlanResult(package, null, site, handover);

        var pages = await db.WebsitePages.AsNoTracking()
            .Where(p => p.AgentWebsiteId == site.Id)
            .Include(p => p.Blocks)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Title)
            .ToListAsync();

        // A picture of this package that has since been deleted from the media library would be
        // written into the site as a dead picture: said here, so the review shows it and the write refuses.
        var stored = StoredImages(import);
        var problems = new List<string>();
        var assetIds = stored.Where(i => i.MediaAssetId > 0).Select(i => i.MediaAssetId).Distinct().ToList();
        if (assetIds.Count > 0)
        {
            var present = await db.WebsiteMediaAssets.AsNoTracking()
                .Where(a => a.AgentWebsiteId == site.Id && assetIds.Contains(a.Id)).Select(a => a.Id).ToListAsync();
            var gone = stored.Where(i => i.MediaAssetId > 0 && !present.Contains(i.MediaAssetId)).Select(i => i.Ref).Distinct().ToList();
            if (gone.Count > 0)
            {
                problems.Add($"{gone.Count} of this package's pictures {(gone.Count == 1 ? "has" : "have")} been deleted from the media library since it was uploaded " +
                    $"({string.Join(", ", gone.Take(3))}{(gone.Count > 3 ? ", ..." : string.Empty)}). Drop the package and upload it again.");
            }
        }

        var urls = stored.GroupBy(i => i.Ref).ToDictionary(g => g.Key, g => g.First().Url, StringComparer.Ordinal);

        // 574: for a return, the site as it is now is read against what the hand-over recorded.
        HashSet<int>? handedOver = null;
        Dictionary<string, int>? rowBySlug = null;
        HashSet<int>? changed = null;
        var logoChanged = false;
        // What this package could carry: a hand-over's own list, or (built from scratch) the types
        // that do not point at one adviser's content.
        IReadOnlySet<string> carried = BuilderContract.BlockTypes.Except(BuilderContract.HandoverOnlyTypes).ToHashSet(StringComparer.Ordinal);
        if (handover != null)
        {
            carried = SiteHandoverService.List<string>(handover.AllowedBlockTypesJson).ToHashSet(StringComparer.Ordinal);
            var recorded = SiteHandoverService.List<HandoverPage>(handover.PagesJson);
            var recordedIds = recorded.Select(r => r.Id).ToHashSet();
            var byId = pages.ToDictionary(r => r.Id);
            var today = SiteFingerprint.OfPages(pages);
            handedOver = new HashSet<int>();
            rowBySlug = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            changed = new HashSet<int>();
            foreach (var was in recorded)
            {
                // The row the page is today: its own, or -- after a put-back, which gives every page a
                // new row -- the row now at the address it had.
                var row = byId.GetValueOrDefault(was.Id)
                    ?? pages.FirstOrDefault(r => !recordedIds.Contains(r.Id) && string.Equals(r.Slug, was.Slug, StringComparison.OrdinalIgnoreCase));
                if (row == null || !handedOver.Add(row.Id)) continue;
                rowBySlug[was.Slug] = row.Id;
                var said = was.Fingerprint ?? string.Empty;
                // A hand-over made before fingerprints were kept falls back on the times, sections included.
                var differs = said.Length > 0
                    ? !string.Equals(today[row.Id], said, StringComparison.Ordinal)
                    : row.Blocks.Select(x => x.UpdatedAt).Append(row.UpdatedAt).Max() > handover.IssuedAt;
                if (differs) changed.Add(row.Id);
            }
            logoChanged = recorded.Any(r => !string.IsNullOrEmpty(r.Fingerprint))
                && !string.Equals(handover.LogoUrl ?? string.Empty, site.LogoUrl ?? string.Empty, StringComparison.Ordinal);
        }

        var plan = SiteImportPlan.Build(package.Template, urls, site, pages, removePageIds ?? new HashSet<int>(), handedOver, keepPageIds, problems,
            rowBySlug, changed, logoChanged, carried);
        return new PlanResult(package, plan, site, handover);
    }

    // "12,15" as a set of ids: the pages to remove or to keep, as the review's address and the look's
    // address both carry them. One reading, so the two cannot come to mean different pages.
    public static HashSet<int> ParseIds(string? list) =>
        (list ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0).ToHashSet();

    // The caller's row must be one this context saves: the kept copy of the site and the closing
    // fields ride on it. A row read without tracking would bring the package in and keep no copy.
    private static void Track(IPRODbContext db, WebsiteBuilderImport import)
    {
        if (db.Entry(import).State == EntityState.Detached) db.WebsiteBuilderImports.Attach(import);
    }

    // One piece of work on a site at a time: bringing a package in, putting one back and dropping one
    // each begin by holding the site's own row until they commit. The claim on the package's row
    // stops the same step running twice; this stops two different steps crossing on one site.
    private static Task HoldSiteAsync(IPRODbContext db, int websiteId) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT `Id` FROM `AgentWebsites` WHERE `Id` = {websiteId} FOR UPDATE");

    // Brings the package in: the site's pages become the plan's pages. templateId, when given, also
    // switches the site to that design.
    public static async Task<ApplyResult> ApplyAsync(
        IPRODbContext db, WebsiteBuilderImport import, IReadOnlySet<int> removePageIds, int? templateId, string appliedBy, IReadOnlySet<int>? keepPageIds = null,
        string? reviewedPlan = null)
    {
        Track(db, import);
        if (templateId.HasValue && !await db.WebsiteTemplates.AnyAsync(t => t.Id == templateId.Value && t.IsActive))
        {
            return ApplyResult.Failed("That design is not available.");
        }

        // One request brings a package in, whatever arrives at the same moment: the row is claimed
        // in the database (Pending becomes Applied only for the first), inside the transaction that
        // does the work, so a second click waits and then finds nothing left to claim. Leaving this
        // method any other way than by Commit gives the claim back.
        await using var transaction = await db.Database.BeginTransactionAsync();
        await HoldSiteAsync(db, import.AgentWebsiteId);
        var claimed = await db.WebsiteBuilderImports
            .Where(i => i.Id == import.Id && i.Status == WebsiteBuilderImportStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, WebsiteBuilderImportStatus.Applied));
        if (claimed != 1) return ApplyResult.Failed("This package has already been brought in or dropped.");

        var planned = await PlanAsync(db, import, removePageIds, keepPageIds);
        if (planned == null) return ApplyResult.Failed("The customer's website no longer exists.");
        if (planned.Plan == null) return new ApplyResult(false, planned.Package.Problems.Select(p => $"{p.Where}: {p.Message}").ToList());
        if (planned.Plan.Problems.Count > 0) return new ApplyResult(false, planned.Plan.Problems);
        var plan = planned.Plan;
        // 574: what is written is what was read. The plan is worked out again here, on the site as it
        // is at this moment; if the customer changed the site after the review was opened (a page
        // added at an address the package takes, a map added to a page about to be replaced), it is no
        // longer the plan the screen showed.
        if (reviewedPlan != null && !string.Equals(reviewedPlan, plan.Fingerprint, StringComparison.Ordinal))
        {
            return ApplyResult.Failed("The site has changed since this review was opened, so bringing the package in would not do what the screen said. Nothing was changed. Read the review again.");
        }

        var site = await db.AgentWebsites.FirstAsync(w => w.Id == import.AgentWebsiteId);
        var rows = await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id).Include(p => p.Blocks).ToListAsync();
        import.SnapshotJson = SiteSnapshot.Capture(site, rows).ToJson();

        var now = DateTime.UtcNow;
        var rowById = rows.ToDictionary(r => r.Id);

        // Removals first and on their own, so an address a removed page held is free before another
        // page takes it (the site's pages are unique by address).
        foreach (var gone in plan.Pages.Where(p => p.Change == PageChange.Removed))
        {
            var row = rowById[gone.Page.Id];
            db.WebsiteContentBlocks.RemoveRange(row.Blocks);
            db.WebsitePages.Remove(row);
        }
        // Parents are set once every page has its row; clearing them here keeps the delete above from
        // racing the database's own "set null".
        foreach (var live in plan.Pages.Where(p => p.Change is PageChange.Kept or PageChange.Replaced)) rowById[live.Page.Id].ParentPageId = null;
        await db.SaveChangesAsync();

        var idFor = new Dictionary<int, WebsitePage>();
        foreach (var item in plan.Pages.Where(p => p.Change != PageChange.Removed))
        {
            var after = item.Page;
            WebsitePage row;
            if (item.Change == PageChange.Added)
            {
                row = new WebsitePage { AgentWebsiteId = site.Id, CreatedAt = now };
                db.WebsitePages.Add(row);
            }
            else
            {
                row = rowById[after.Id];
            }
            idFor[after.Id] = row;

            if (item.Change == PageChange.Kept)
            {
                if (row.SortOrder != after.SortOrder || row.IsHomePage != after.IsHomePage) row.UpdatedAt = now;
                row.SortOrder = after.SortOrder;
                row.IsHomePage = after.IsHomePage;
                continue;
            }

            row.Title = after.Title;
            row.Slug = after.Slug;
            row.NavigationLabel = after.NavigationLabel;
            row.IsHomePage = after.IsHomePage;
            row.SortOrder = after.SortOrder;
            row.MetaTitle = after.MetaTitle;
            row.MetaDescription = after.MetaDescription;
            row.OldAddresses = after.OldAddresses;
            row.ShowInNavigation = after.ShowInNavigation;
            row.IsPublished = after.IsPublished;
            row.UpdatedAt = now;
            if (item.Change == PageChange.Replaced) db.WebsiteContentBlocks.RemoveRange(row.Blocks);
            foreach (var block in after.Blocks)
            {
                db.WebsiteContentBlocks.Add(new WebsiteContentBlock
                {
                    WebsitePage = row,
                    BlockType = block.BlockType,
                    Heading = block.Heading,
                    Subheading = block.Subheading,
                    Body = block.Body,
                    ImageUrl = block.ImageUrl,
                    ButtonText = block.ButtonText,
                    ButtonUrl = block.ButtonUrl,
                    SettingsJson = block.SettingsJson,
                    LayoutVariant = block.LayoutVariant,
                    SortOrder = block.SortOrder,
                    IsVisible = block.IsVisible,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }
        await db.SaveChangesAsync();

        foreach (var item in plan.Pages.Where(p => p.Change != PageChange.Removed))
        {
            idFor[item.Page.Id].ParentPageId = item.Page.ParentPageId.HasValue && idFor.TryGetValue(item.Page.ParentPageId.Value, out var parent)
                ? parent.Id
                : null;
        }

        site.LogoUrl = plan.LogoUrlAfter;
        if (templateId.HasValue) site.TemplateId = templateId.Value;
        site.UpdatedAt = now;
        import.Status = WebsiteBuilderImportStatus.Applied;
        import.AppliedAt = now;
        import.AppliedBy = Clip(appliedBy, 100);
        import.PreviewKey = string.Empty;
        import.PreviewKeyExpiresAt = null;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ApplyResult.Done();
    }

    // The newest package that went in and has not been taken back out: the only one whose "before" is
    // still the site's previous state.
    public static Task<int?> LatestAppliedIdAsync(IPRODbContext db, int websiteId) =>
        db.WebsiteBuilderImports.AsNoTracking()
            .Where(i => i.AgentWebsiteId == websiteId && i.Status == WebsiteBuilderImportStatus.Applied)
            .OrderByDescending(i => i.AppliedAt).ThenByDescending(i => i.Id)
            .Select(i => (int?)i.Id)
            .FirstOrDefaultAsync();

    // Puts the site back as it was the moment before this package was brought in. Anything changed
    // on the site since then goes with it, so the screen asks first.
    // blob (574): when given, the pictures the package brought that nothing uses any more leave the
    // media library with it, as they do when a package is dropped.
    public static async Task<ApplyResult> RestoreAsync(IPRODbContext db, WebsiteBuilderImport import, IBlobStorageService? blob = null)
    {
        Track(db, import);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await HoldSiteAsync(db, import.AgentWebsiteId);
        var latest = await LatestAppliedIdAsync(db, import.AgentWebsiteId);
        var current = await db.WebsiteBuilderImports.AsNoTracking().Where(i => i.Id == import.Id)
            .Select(i => new { i.Status, i.SnapshotJson }).FirstOrDefaultAsync();
        if (current == null || current.Status != WebsiteBuilderImportStatus.Applied || string.IsNullOrWhiteSpace(current.SnapshotJson))
        {
            return ApplyResult.Failed("This package is not on the site, so there is nothing to put back.");
        }
        if (latest != import.Id)
        {
            return ApplyResult.Failed("A newer package has been brought in since. Put that one back first.");
        }
        var snapshot = SiteSnapshot.FromJson(current.SnapshotJson);
        if (snapshot == null) return ApplyResult.Failed("The record of the previous site cannot be read.");

        // Claimed in the database, as in ApplyAsync: of two requests to put it back, one does.
        var claimed = await db.WebsiteBuilderImports
            .Where(i => i.Id == import.Id && i.Status == WebsiteBuilderImportStatus.Applied)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, WebsiteBuilderImportStatus.Restored));
        if (claimed != 1) return ApplyResult.Failed("This package is not on the site, so there is nothing to put back.");

        var site = await db.AgentWebsites.FirstOrDefaultAsync(w => w.Id == import.AgentWebsiteId);
        if (site == null) return ApplyResult.Failed("The customer's website no longer exists.");
        var rows = await db.WebsitePages.Where(p => p.AgentWebsiteId == site.Id).Include(p => p.Blocks).ToListAsync();
        var now = DateTime.UtcNow;
        var wanted = snapshot.Pages.Select(p => p.Id).ToHashSet();

        foreach (var row in rows.Where(r => !wanted.Contains(r.Id)))
        {
            db.WebsiteContentBlocks.RemoveRange(row.Blocks);
            db.WebsitePages.Remove(row);
        }
        foreach (var row in rows.Where(r => wanted.Contains(r.Id)))
        {
            row.ParentPageId = null;
            // Out of the way of every address the old pages are about to take back.
            row.Slug = $"restoring-{row.Id}";
        }
        await db.SaveChangesAsync();

        var rowById = rows.Where(r => wanted.Contains(r.Id)).ToDictionary(r => r.Id);
        var rowFor = new Dictionary<int, WebsitePage>();
        foreach (var page in snapshot.Pages)
        {
            if (!rowById.TryGetValue(page.Id, out var row))
            {
                row = new WebsitePage { AgentWebsiteId = site.Id };
                db.WebsitePages.Add(row);
            }
            else
            {
                db.WebsiteContentBlocks.RemoveRange(row.Blocks);
            }
            rowFor[page.Id] = row;
            row.Title = page.Title;
            row.Slug = page.Slug;
            row.NavigationLabel = page.NavigationLabel;
            row.MetaTitle = page.MetaTitle;
            row.MetaDescription = page.MetaDescription;
            row.OldAddresses = page.OldAddresses;
            row.IsHomePage = page.IsHomePage;
            row.ShowInNavigation = page.ShowInNavigation;
            row.IsPublished = page.IsPublished;
            row.SortOrder = page.SortOrder;
            row.CreatedAt = page.CreatedAt;
            row.UpdatedAt = now;
            foreach (var block in page.Blocks)
            {
                db.WebsiteContentBlocks.Add(new WebsiteContentBlock
                {
                    WebsitePage = row,
                    BlockType = block.BlockType,
                    Heading = block.Heading,
                    Subheading = block.Subheading,
                    Body = block.Body,
                    ImageUrl = block.ImageUrl,
                    ButtonText = block.ButtonText,
                    ButtonUrl = block.ButtonUrl,
                    SettingsJson = block.SettingsJson,
                    LayoutVariant = block.LayoutVariant,
                    SortOrder = block.SortOrder,
                    IsVisible = block.IsVisible,
                    CreatedAt = block.CreatedAt,
                    UpdatedAt = now
                });
            }
        }
        await db.SaveChangesAsync();

        foreach (var page in snapshot.Pages)
        {
            rowFor[page.Id].ParentPageId = page.ParentPageId.HasValue && rowFor.TryGetValue(page.ParentPageId.Value, out var parent) ? parent.Id : null;
        }
        site.LogoUrl = snapshot.LogoUrl;
        if (await db.WebsiteTemplates.AnyAsync(t => t.Id == snapshot.TemplateId)) site.TemplateId = snapshot.TemplateId;
        site.UpdatedAt = now;
        import.Status = WebsiteBuilderImportStatus.Restored;
        import.ClosedAt = now;
        // The site is its own record again: the kept copy has done its work, and while it stayed it
        // would keep every picture it names from ever being cleaned up (BlobReferences reads it).
        import.SnapshotJson = null;
        await db.SaveChangesAsync();
        var freed = blob == null ? new List<string>() : await ReleaseImagesAsync(db, import);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        foreach (var url in freed)
        {
            if (!await BlobReferences.IsReferencedAsync(db, url)) await blob!.DeleteAsync(url);
        }
        return ApplyResult.Done();
    }

    // Drops a package that was never brought in, and takes back out of the media library the images
    // it added that nothing uses. Rows first, files second, and a file only when nothing references it
    // (the rule the page editor's own delete follows).
    public static async Task<bool> DiscardAsync(IPRODbContext db, IBlobStorageService blob, WebsiteBuilderImport import)
    {
        Track(db, import);
        var freed = new List<string>();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await HoldSiteAsync(db, import.AgentWebsiteId);
            // Claimed in the database: a "bring it in" arriving at the same moment gets the row or
            // this does, never both (dropping would delete pictures the other is about to use).
            var claimed = await db.WebsiteBuilderImports
                .Where(i => i.Id == import.Id && i.Status == WebsiteBuilderImportStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, WebsiteBuilderImportStatus.Discarded));
            if (claimed != 1) return false;

            freed = await ReleaseImagesAsync(db, import);

            import.Status = WebsiteBuilderImportStatus.Discarded;
            import.ClosedAt = DateTime.UtcNow;
            import.PreviewKey = string.Empty;
            import.PreviewKeyExpiresAt = null;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        // A file goes only when nothing references it any more: not a page, not an article, and not
        // the kept copy of a site that another package replaced (BlobReferences reads those too).
        foreach (var url in freed)
        {
            if (!await BlobReferences.IsReferencedAsync(db, url)) await blob.DeleteAsync(url);
        }
        return true;
    }

    // The pictures this package added to the media library that nothing uses: their rows go here, and
    // the caller deletes each file afterwards if nothing references it. Used when a package is
    // dropped and when one is put back.
    private static async Task<List<string>> ReleaseImagesAsync(IPRODbContext db, WebsiteBuilderImport import)
    {
        var freed = new List<string>();
        var site = await db.AgentWebsites.AsNoTracking().FirstOrDefaultAsync(w => w.Id == import.AgentWebsiteId);
        var mine = StoredImages(import).Where(i => i.Created).ToList();
        // Another package of this site that links the same image (it arrived with identical bytes).
        var others = await db.WebsiteBuilderImports.AsNoTracking()
            .Where(i => i.AgentWebsiteId == import.AgentWebsiteId && i.Id != import.Id
                        && (i.Status == WebsiteBuilderImportStatus.Pending || i.Status == WebsiteBuilderImportStatus.Applied))
            .Select(i => i.AssetMapJson)
            .ToListAsync();
        var linkedElsewhere = others
            .SelectMany(json => StoredImages(new WebsiteBuilderImport { AssetMapJson = json }))
            .Select(i => i.Url)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var image in mine)
        {
            if (linkedElsewhere.Contains(image.Url)) continue;
            if (site != null && string.Equals(site.LogoUrl, image.Url, StringComparison.Ordinal)) continue;
            if (await db.WebsiteContentBlocks.AnyAsync(b => b.WebsitePage.AgentWebsiteId == import.AgentWebsiteId && b.ImageUrl == image.Url)) continue;
            var asset = await db.WebsiteMediaAssets.FirstOrDefaultAsync(a => a.Id == image.MediaAssetId && a.AgentWebsiteId == import.AgentWebsiteId);
            if (asset == null) continue;
            db.WebsiteMediaAssets.Remove(asset);
            freed.Add(asset.BlobUrl);
        }
        return freed;
    }

    // ---- the look ----
    //
    // A SuperAdmin looks at a package on the portal's host, where they are not signed in. The link
    // carries a random key kept on the import's row for an hour; the portal shows the look only to a
    // request that names the row and carries its key. No shared secret between the two apps: the
    // database they already share is the channel.
    public static readonly TimeSpan LookLifetime = TimeSpan.FromHours(1);

    // The key for this import's look, made (or renewed) when it has none or it is about to lapse.
    public static async Task<string> LookKeyAsync(IPRODbContext db, WebsiteBuilderImport import)
    {
        var now = DateTime.UtcNow;
        if (import.PreviewKey.Length == 0 || import.PreviewKeyExpiresAt == null || import.PreviewKeyExpiresAt < now.AddMinutes(20))
        {
            import.PreviewKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            import.PreviewKeyExpiresAt = now.Add(LookLifetime);
            await db.SaveChangesAsync();
        }
        return import.PreviewKey;
    }

    public static bool LookKeyLooksRight(string? key) =>
        key is { Length: 48 } && key.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    // Only a package still waiting has a look; once it is brought in the site itself is the look.
    public static bool LookIsOpen(WebsiteBuilderImport import, string? key, DateTime utcNow) =>
        import.Status == WebsiteBuilderImportStatus.Pending
        && LookKeyLooksRight(key)
        && import.PreviewKey.Length == key!.Length
        && import.PreviewKeyExpiresAt.HasValue && import.PreviewKeyExpiresAt.Value >= utcNow
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(import.PreviewKey), System.Text.Encoding.ASCII.GetBytes(key));

    private static string Clip(string? value, int length)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length <= length ? value : value[..length];
    }
}

// The whole site as it was before a package went in: enough to put every page, section and the logo
// back. Kept as JSON on the import's row.
public sealed class SiteSnapshot
{
    public string LogoUrl { get; set; } = string.Empty;
    public int TemplateId { get; set; }
    public List<PageSnapshot> Pages { get; set; } = new();

    public sealed class PageSnapshot
    {
        public int Id { get; set; }
        public int? ParentPageId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string NavigationLabel { get; set; } = string.Empty;
        public string MetaTitle { get; set; } = string.Empty;
        public string MetaDescription { get; set; } = string.Empty;
        public string OldAddresses { get; set; } = string.Empty;
        public bool IsHomePage { get; set; }
        public bool ShowInNavigation { get; set; }
        public bool IsPublished { get; set; }
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<BlockSnapshot> Blocks { get; set; } = new();
    }

    public sealed class BlockSnapshot
    {
        public string BlockType { get; set; } = string.Empty;
        public string Heading { get; set; } = string.Empty;
        public string Subheading { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public string ButtonText { get; set; } = string.Empty;
        public string ButtonUrl { get; set; } = string.Empty;
        public string SettingsJson { get; set; } = "{}";
        public string LayoutVariant { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public bool IsVisible { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public static SiteSnapshot Capture(AgentWebsite site, IEnumerable<WebsitePage> pages) => new()
    {
        LogoUrl = site.LogoUrl ?? string.Empty,
        TemplateId = site.TemplateId,
        Pages = pages.OrderBy(p => p.SortOrder).ThenBy(p => p.Id).Select(p => new PageSnapshot
        {
            Id = p.Id,
            ParentPageId = p.ParentPageId,
            Title = p.Title,
            Slug = p.Slug,
            NavigationLabel = p.NavigationLabel,
            MetaTitle = p.MetaTitle,
            MetaDescription = p.MetaDescription,
            OldAddresses = p.OldAddresses,
            IsHomePage = p.IsHomePage,
            ShowInNavigation = p.ShowInNavigation,
            IsPublished = p.IsPublished,
            SortOrder = p.SortOrder,
            CreatedAt = p.CreatedAt,
            Blocks = p.Blocks.OrderBy(b => b.SortOrder).ThenBy(b => b.Id).Select(b => new BlockSnapshot
            {
                BlockType = b.BlockType,
                Heading = b.Heading,
                Subheading = b.Subheading,
                Body = b.Body,
                ImageUrl = b.ImageUrl,
                ButtonText = b.ButtonText,
                ButtonUrl = b.ButtonUrl,
                SettingsJson = b.SettingsJson,
                LayoutVariant = b.LayoutVariant,
                SortOrder = b.SortOrder,
                IsVisible = b.IsVisible,
                CreatedAt = b.CreatedAt
            }).ToList()
        }).ToList()
    };

    // Addresses are written as they are (574): the default writer turns an accent, an apostrophe or
    // an ampersand into an escape, and the "is this file still used?" check looks for the address as
    // text. A picture called "Bob's office.png" was not protected by the copy that named it.
    public string ToJson() => JsonSerializer.Serialize(this, SiteHandoverService.AddressesAsWritten);

    public static SiteSnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<SiteSnapshot>(json); }
        catch (JsonException) { return null; }
    }
}
