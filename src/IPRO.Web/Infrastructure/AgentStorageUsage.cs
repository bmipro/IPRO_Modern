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

        return documentBytes + websiteMediaBytes + portalDocumentBytes + await GalleryBytesAsync(db, agentId);
    }

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
