namespace IPRO.Entities;

// 571: a site brought in from the Template Builder (export contract v0.4). One row per package a
// SuperAdmin uploads for a customer's site. The package is checked at the door; what is kept here is
// its template.json, where each of its images was stored, and -- once it is brought in -- the site as
// it was before, so the previous site can be put back.
//
// No navigation properties and no foreign keys on purpose: the row is a record of an operation. It
// goes with the adviser (AgentDataEraser) and carries no file of its own -- the images are ordinary
// rows of the site's media library.
public static class WebsiteBuilderImportStatus
{
    public const string Pending = "Pending";       // checked and stored, not on the site
    public const string Applied = "Applied";       // its pages are the site's pages
    public const string Discarded = "Discarded";   // dropped before it was brought in
    public const string Restored = "Restored";     // brought in, then the previous site was put back
}

public class WebsiteBuilderImport
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int AgentWebsiteId { get; set; }
    public string Status { get; set; } = WebsiteBuilderImportStatus.Pending;
    public string FileName { get; set; } = string.Empty;
    public string PackageSha256 { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public string Theme { get; set; } = string.Empty;
    public string HandoverId { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public int BlockCount { get; set; }
    public int NewImageCount { get; set; }
    public long NewImageBytes { get; set; }
    // The package's template.json exactly as it arrived (it passed every rule when it was stored).
    public string TemplateJson { get; set; } = string.Empty;
    // Where each image the package uses was stored: see BuilderImportService.StoredImage.
    public string AssetMapJson { get; set; } = "[]";
    // The whole site as it was the moment before the package was brought in. Null until then.
    public string? SnapshotJson { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public string AppliedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AppliedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    // The one-hour key a SuperAdmin's "look at it" link carries. Empty = no look is open.
    public string PreviewKey { get; set; } = string.Empty;
    public DateTime? PreviewKeyExpiresAt { get; set; }
}
