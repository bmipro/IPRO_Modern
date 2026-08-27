using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Infrastructure;

// One shared definition of "how much storage has this agent used", because FileUploadCapacity is a
// single pool spanning documents and gallery photos.
//
// It used to be computed in two places with two different answers: the gallery upload counted documents
// AND gallery, while the document upload counted documents only. That let an agent with a full gallery
// keep uploading documents past their limit, since the document check couldn't see the gallery at all.
// Both paths now call this, so the two can no longer disagree.
public static class AgentStorageUsage
{
    public static async Task<long> TotalBytesAsync(IPRODbContext db, int agentId)
    {
        var documentBytes = await db.AgentDocuments
            .Where(d => d.AgentUserId == agentId)
            .SumAsync(d => (long?)d.FileSizeBytes) ?? 0;

        // Website media (the page-editor image library) and client portal documents were uploaded
        // into the same storage account but counted by nobody: neither their upload paths nor this
        // total (2026-08-14 ultra-audit). An agent could push unbounded images through the page
        // editor and unbounded files through the portal, and the two paths that DO enforce the quota
        // reported a usage figure well below reality. Both tables already record a size; they just
        // were never summed.
        var websiteMediaBytes = await db.WebsiteMediaAssets
            .Where(m => m.AgentWebsite.AgentUserId == agentId)
            .SumAsync(m => (long?)m.FileSize) ?? 0;

        var portalDocumentBytes = await db.PortalDocuments
            .Where(d => d.Client.AgentUserId == agentId)
            .SumAsync(d => (long?)d.FileSizeBytes) ?? 0;

        // A5-M-QUOTA (2026-08-20): article images now count too. Sizes are captured at upload,
        // so images from before then contribute 0 -- the total only ever grows toward honesty.
        // Agent photos and website logos stay deliberately excluded: one bounded file each,
        // replaced in place, immaterial next to a shared pool measured in hundreds of MB.
        var articleImageBytes = await db.Articles
            .Where(a => a.AgentUserId == agentId)
            .SumAsync(a => (long?)a.ImageSizeBytes) ?? 0;

        return documentBytes + websiteMediaBytes + portalDocumentBytes + articleImageBytes + await GalleryBytesAsync(db, agentId);
    }

    // A5-M-QUOTA: a package with no FileUploadCapacity limit value used to mean UNLIMITED --
    // an omission in package setup silently disabled the quota. It now means this default.
    public const int DefaultLimitMb = 1024;

    public static long LimitBytes(int? limitValueMb) =>
        (long)(limitValueMb ?? DefaultLimitMb) * 1024 * 1024;

    // M10: the DISPLAY companion of LimitBytes. Every user-facing surface used `LimitValue ?? 0`,
    // so a blank limit -- a deliberately supported configuration -- rendered as "1150.3 MB of
    // 0 MB used" while the 1024 MB default was quietly enforced. Display and enforcement must
    // read the same fallback or the page tells the agent a falsehood.
    public static int DisplayLimitMb(int? limitValueMb) => limitValueMb ?? DefaultLimitMb;

    // Gallery photos record their size inside the block's settings JSON rather than in a column, so this
    // cannot be summed in SQL the way documents can.
    public static async Task<long> GalleryBytesAsync(IPRODbContext db, int agentId)
    {
        var galleryJsons = await db.WebsiteContentBlocks
            .Where(b => b.BlockType == WebsiteBlockTypes.Gallery
                        && b.WebsitePage.AgentWebsite.AgentUserId == agentId)
            .Select(b => b.SettingsJson)
            .ToListAsync();

        return galleryJsons.Sum(json => WebsiteGallerySettings.FromJson(json).TotalBytes());
    }

    public static double ToMb(long bytes) => Math.Round(bytes / 1024.0 / 1024.0, 1);
}
