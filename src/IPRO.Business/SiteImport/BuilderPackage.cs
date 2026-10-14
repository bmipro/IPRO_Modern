namespace IPRO.Business.SiteImport;

// 571: what a Template Builder package holds once it has been read.

// One reason a package is not taken. Page is the page's slug and Block the block's sortOrder, as
// contract section 11.5 names them; both are null for a problem with the package as a whole. Rule is
// the section of CONTRACT.md the package breaks, so the builder's developer can look it up.
public sealed record ImportProblem(string? Page, int? Block, string Rule, string Message)
{
    public string Where => Page == null ? "The package"
        : Block == null ? $"Page \"{Page}\""
        : $"Page \"{Page}\", section {Block}";
}

public sealed record BuilderTemplate(
    string TemplateName, string Theme, string HandoverId, string LogoRef, string LogoAltText,
    IReadOnlyList<BuilderPage> Pages)
{
    // "0.4", or "0.5" for a package that may hold locked boxes (576).
    public string ContractVersion { get; init; } = BuilderContract.Version;

    // Every image the site would show: the logo and each block's picture.
    public IEnumerable<string> UsedImageRefs =>
        Pages.SelectMany(p => p.Blocks).Select(b => b.ImageRef).Append(LogoRef).Where(r => r.Length > 0).Distinct();
}

public sealed record BuilderPage(
    string Slug, string? ParentSlug, string Title, string NavigationLabel, bool IsHomePage, int SortOrder,
    IReadOnlyList<BuilderBlock> Blocks);

// SettingsJson is the block's settings as iPro stores them (the entity settings classes), not as the
// package wrote them. ArticleId and WebsiteFormId are 0 unless the block is of that kind.
// HostSectionId (576): for a locked box, the number of the section it stands for; 0 otherwise.
public sealed record BuilderBlock(
    string Type, string Heading, string Subheading, string Body, string ImageRef, string ButtonText, string ButtonUrl,
    string LayoutVariant, int SortOrder, bool IsVisible, string SettingsJson, int ArticleId, int WebsiteFormId, int HostSectionId = 0);

public sealed record BuilderImage(string Ref, string FileName, string ContentType, string Sha256, byte[] Bytes)
{
    public long Length => Bytes.LongLength;
}

// What the importer checks a package against, beyond the contract's fixed rules.
//
// HandoverId: "" when iPro handed nothing over for this site -- then only a package built from
// scratch in the builder (its own handoverId empty) is taken. Otherwise the hand-over the package must
// carry, with the block types, articles and forms that hand-over offered.
// KnownImageHashes: SHA-256 (lower-case hex) of images the site already stores; an image with the same
// bytes is linked and costs no storage (section 10, rule 3). RemainingBytes: the storage left for new
// images (rule 4). HostSectionIds (576): the locked boxes the hand-over sent out; only those may come back.
public sealed record BuilderImportContext(
    string HandoverId,
    IReadOnlySet<string> AllowedBlockTypes,
    IReadOnlySet<int> ArticleIds,
    IReadOnlySet<int> FormIds,
    IReadOnlySet<string> KnownImageHashes,
    long RemainingBytes,
    IReadOnlySet<int>? HostSectionIds = null)
{
    public static BuilderImportContext DesignerTemplate(IReadOnlySet<string>? knownImageHashes = null, long remainingBytes = long.MaxValue) =>
        new(string.Empty,
            new HashSet<string>(BuilderContract.BlockTypes, StringComparer.Ordinal),
            new HashSet<int>(), new HashSet<int>(),
            knownImageHashes ?? new HashSet<string>(StringComparer.Ordinal),
            remainingBytes);
}

public sealed class BuilderPackage
{
    public IReadOnlyList<ImportProblem> Problems { get; init; } = Array.Empty<ImportProblem>();
    public BuilderTemplate? Template { get; init; }
    // template.json as it arrived.
    public string TemplateJson { get; init; } = string.Empty;
    // The images the template uses, by ref. An image nothing uses is not here (it is ignored).
    public IReadOnlyDictionary<string, BuilderImage> Images { get; init; } = new Dictionary<string, BuilderImage>();
    public IReadOnlyList<string> IgnoredAssets { get; init; } = Array.Empty<string>();
    public string Sha256 { get; init; } = string.Empty;

    public bool IsValid => Problems.Count == 0 && Template != null;

    // The images the site does not store yet: each distinct file once, whatever name it travels under.
    public IReadOnlyList<BuilderImage> NewImages(IReadOnlySet<string> knownImageHashes) =>
        Images.Values.Where(i => !knownImageHashes.Contains(i.Sha256))
            .GroupBy(i => i.Sha256).Select(g => g.First()).ToList();
}
