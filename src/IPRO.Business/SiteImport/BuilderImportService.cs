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
// Static with the context passed in, like ClientRecycleBin: the admin app runs it today and the
// portal will when a customer returns from the builder.
public static class BuilderImportService
{
    public const string MediaContainer = "website-media";

    // Where an image the package uses is stored. Created = this import put it in the media library
    // (false: the site already held the same bytes, so the existing image is linked).
    public sealed record StoredImage(string Ref, string Url, int MediaAssetId, bool Created, string Sha256, long Bytes);

    public sealed record ApplyResult(bool Ok, IReadOnlyList<string> Problems)
    {
        public static ApplyResult Failed(params string[] problems) => new(false, problems);
        public static ApplyResult Done() => new(true, Array.Empty<string>());
    }

    // What a package for this site is checked against. No hand-over exists yet (the exporter is the
    // next piece of work), so only a site built from scratch in the builder is taken.
    public static async Task<BuilderImportContext> ContextAsync(IPRODbContext db, AgentWebsite site, long storageLimitBytes)
    {
        var known = await db.WebsiteMediaAssets.AsNoTracking()
            .Where(a => a.AgentWebsiteId == site.Id && a.ContentSha256 != "")
            .Select(a => a.ContentSha256)
            .Distinct()
            .ToListAsync();
        // A limit of zero or less means no limit, as in the page editor's own upload.
        var remaining = storageLimitBytes <= 0
            ? long.MaxValue
            : Math.Max(0, storageLimitBytes - await AgentStorage.TotalBytesAsync(db, site.AgentUserId));
        return BuilderImportContext.DesignerTemplate(known.ToHashSet(StringComparer.Ordinal), remaining);
    }

    public static async Task<WebsiteBuilderImport> StoreAsync(
        IPRODbContext db, IBlobStorageService blob, AgentWebsite site, BuilderPackage package, string fileName, string uploadedBy)
    {
        if (!package.IsValid) throw new InvalidOperationException("Only a package that passed every rule is stored.");

        var existing = await db.WebsiteMediaAssets
            .Where(a => a.AgentWebsiteId == site.Id && a.ContentSha256 != "")
            .OrderBy(a => a.Id)
            .ToListAsync();
        var byHash = new Dictionary<string, WebsiteMediaAsset>(StringComparer.Ordinal);
        foreach (var asset in existing) byHash.TryAdd(asset.ContentSha256, asset);

        var created = new List<WebsiteMediaAsset>();
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
                byHash[image.Sha256] = asset;
                created.Add(asset);
            }
            await db.SaveChangesAsync();
        }
        catch
        {
            // Nothing was recorded, so no row points at what was uploaded so far: take it back out.
            foreach (var asset in created) await blob.DeleteAsync(asset.BlobUrl);
            throw;
        }

        var stored = package.Images.Values
            .OrderBy(i => i.Ref, StringComparer.Ordinal)
            .Select(i => new StoredImage(i.Ref, byHash[i.Sha256].BlobUrl, byHash[i.Sha256].Id, created.Contains(byHash[i.Sha256]), i.Sha256, i.Length))
            .ToList();
        var template = package.Template!;
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
        return import;
    }

    public static IReadOnlyList<StoredImage> StoredImages(WebsiteBuilderImport import)
    {
        try { return JsonSerializer.Deserialize<List<StoredImage>>(import.AssetMapJson) ?? new List<StoredImage>(); }
        catch (JsonException) { return new List<StoredImage>(); }
    }

    public sealed record PlanResult(BuilderPackage Package, SiteImportPlan? Plan, AgentWebsite Site);

    // The stored package read again and laid over the site as it is now. Plan is null when the
    // package no longer passes (Package.Problems says why).
    public static async Task<PlanResult?> PlanAsync(IPRODbContext db, WebsiteBuilderImport import, IReadOnlySet<int>? removePageIds = null)
    {
        var site = await db.AgentWebsites.AsNoTracking().Include(w => w.AgentUser).Include(w => w.Template)
            .FirstOrDefaultAsync(w => w.Id == import.AgentWebsiteId);
        if (site == null) return null;

        // Storage was settled when the package was stored (its images are already in the library).
        var package = BuilderPackageReader.ReadStored(import.TemplateJson, BuilderImportContext.DesignerTemplate());
        if (package.Template == null) return new PlanResult(package, null, site);

        var pages = await db.WebsitePages.AsNoTracking()
            .Where(p => p.AgentWebsiteId == site.Id)
            .Include(p => p.Blocks)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Title)
            .ToListAsync();
        var urls = StoredImages(import).ToDictionary(i => i.Ref, i => i.Url, StringComparer.Ordinal);
        var plan = SiteImportPlan.Build(package.Template, urls, site, pages, removePageIds ?? new HashSet<int>());
        return new PlanResult(package, plan, site);
    }

    // Brings the package in: the site's pages become the plan's pages. templateId, when given, also
    // switches the site to that design.
    public static async Task<ApplyResult> ApplyAsync(
        IPRODbContext db, WebsiteBuilderImport import, IReadOnlySet<int> removePageIds, int? templateId, string appliedBy)
    {
        if (import.Status != WebsiteBuilderImportStatus.Pending) return ApplyResult.Failed("This package has already been brought in or dropped.");
        var planned = await PlanAsync(db, import, removePageIds);
        if (planned == null) return ApplyResult.Failed("The customer's website no longer exists.");
        if (planned.Plan == null) return new ApplyResult(false, planned.Package.Problems.Select(p => $"{p.Where}: {p.Message}").ToList());
        if (planned.Plan.Problems.Count > 0) return new ApplyResult(false, planned.Plan.Problems);
        var plan = planned.Plan;

        if (templateId.HasValue && !await db.WebsiteTemplates.AnyAsync(t => t.Id == templateId.Value && t.IsActive))
        {
            return ApplyResult.Failed("That design is not available.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
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
    public static Task<WebsiteBuilderImport?> LatestAppliedAsync(IPRODbContext db, int websiteId) =>
        db.WebsiteBuilderImports
            .Where(i => i.AgentWebsiteId == websiteId && i.Status == WebsiteBuilderImportStatus.Applied)
            .OrderByDescending(i => i.AppliedAt).ThenByDescending(i => i.Id)
            .FirstOrDefaultAsync();

    // Puts the site back as it was the moment before this package was brought in. Anything changed
    // on the site since then goes with it, so the screen asks first.
    public static async Task<ApplyResult> RestoreAsync(IPRODbContext db, WebsiteBuilderImport import)
    {
        if (import.Status != WebsiteBuilderImportStatus.Applied || string.IsNullOrWhiteSpace(import.SnapshotJson))
        {
            return ApplyResult.Failed("This package is not on the site, so there is nothing to put back.");
        }
        var latest = await LatestAppliedAsync(db, import.AgentWebsiteId);
        if (latest == null || latest.Id != import.Id)
        {
            return ApplyResult.Failed("A newer package has been brought in since. Put that one back first.");
        }
        var snapshot = SiteSnapshot.FromJson(import.SnapshotJson);
        if (snapshot == null) return ApplyResult.Failed("The record of the previous site cannot be read.");

        await using var transaction = await db.Database.BeginTransactionAsync();
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
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ApplyResult.Done();
    }

    // Drops a package that was never brought in, and takes back out of the media library the images
    // it added that nothing uses. Rows first, files second, and a file only when nothing references it
    // (the rule the page editor's own delete follows).
    public static async Task<bool> DiscardAsync(IPRODbContext db, IBlobStorageService blob, WebsiteBuilderImport import)
    {
        if (import.Status != WebsiteBuilderImportStatus.Pending) return false;

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

        var freed = new List<string>();
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

        import.Status = WebsiteBuilderImportStatus.Discarded;
        import.ClosedAt = DateTime.UtcNow;
        import.PreviewKey = string.Empty;
        import.PreviewKeyExpiresAt = null;
        await db.SaveChangesAsync();

        foreach (var url in freed)
        {
            if (!await BlobReferences.IsReferencedAsync(db, url)) await blob.DeleteAsync(url);
        }
        return true;
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

    public string ToJson() => JsonSerializer.Serialize(this);

    public static SiteSnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<SiteSnapshot>(json); }
        catch (JsonException) { return null; }
    }
}
