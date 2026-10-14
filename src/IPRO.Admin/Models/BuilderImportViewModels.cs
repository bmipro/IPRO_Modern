using IPRO.Business.SiteImport;
using IPRO.Entities;

namespace IPRO.Admin.Models;

// 571: bringing a customer's site in from the Template Builder.
public sealed class BuilderImportIndexViewModel
{
    public AgentUser Agent { get; init; } = null!;
    public AgentWebsite? Website { get; init; }
    public IReadOnlyList<WebsiteBuilderImport> Imports { get; init; } = Array.Empty<WebsiteBuilderImport>();
    // The one package that can be taken back out: the newest one on the site.
    public int? RestorableId { get; init; }
    // Set when an upload was just refused: why, and which file.
    public IReadOnlyList<ImportProblem> Problems { get; init; } = Array.Empty<ImportProblem>();
    public string RefusedFileName { get; init; } = string.Empty;
    // 572: the newest package of this site made for the builder (null: never handed over), and,
    // when a hand-over was just refused, why.
    public WebsiteBuilderHandover? LatestHandover { get; init; }
    public IReadOnlyList<string> HandoverBlockers { get; init; } = Array.Empty<string>();
    public double StorageUsedMb { get; init; }
    public int StorageLimitMb { get; init; }
    public string Zone { get; init; } = string.Empty;
}

public sealed class BuilderImportReviewViewModel
{
    public AgentUser Agent { get; init; } = null!;
    public AgentWebsite Website { get; init; } = null!;
    public WebsiteBuilderImport Import { get; init; } = null!;
    public BuilderPackage Package { get; init; } = null!;
    // Null when the stored package no longer passes; Package.Problems then says why.
    public SiteImportPlan? Plan { get; init; }
    public IReadOnlyList<BuilderImportService.StoredImage> Images { get; init; } = Array.Empty<BuilderImportService.StoredImage>();
    public IReadOnlyList<WebsiteTemplate> Designs { get; init; } = Array.Empty<WebsiteTemplate>();
    // The operator's choices so far: the design to switch to (null = leave it) and the pages to remove.
    public int? Design { get; init; }
    public IReadOnlySet<int> Without { get; init; } = new HashSet<int>();
    // 572: the hand-over this package answers (null for a site built from scratch), and the pages
    // removed in the builder that the operator keeps after all.
    public WebsiteBuilderHandover? Handover { get; init; }
    public IReadOnlySet<int> Keep { get; init; } = new HashSet<int>();
    // 576: the sections of replaced pages the operator keeps, by their rows (SectionGoing.BlockId).
    public IReadOnlySet<int> KeepSections { get; init; } = new HashSet<int>();
    // The address of the look for one page: this, with the page's slug appended.
    public string LookAddress { get; init; } = string.Empty;
    public string Zone { get; init; } = string.Empty;

    // The site's design whose key the package's theme hint points at ("modern" -> modern-professional).
    public WebsiteTemplate? BuiltFor => Designs.FirstOrDefault(d => d.TemplateKey.StartsWith(Import.Theme, StringComparison.OrdinalIgnoreCase));
    public WebsiteTemplate? Current => Designs.FirstOrDefault(d => d.Id == Website.TemplateId);
}
