using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IPRO.Entities;

namespace IPRO.Business.SiteImport;

// 572: iPro hands a customer's site to the Template Builder (export contract v0.4, section 9). The
// package holds the site as a template (template.json, assets/) and what the builder needs to edit
// it without owning any of iPro's content (handover.json, catalog/).
//
// A site is handed over whole or not at all (contract section 12): every page and field within the
// contract, every section either one of the types the builder carries and the customer's package
// includes, or (576, contract v0.5) a locked box standing for a section the builder does not carry.
// Whether it fits is decided by the SAME door a returning package goes through
// (BuilderPackageReader), so iPro never hands over something it would refuse to take back.
//
// Nothing here touches the database or storage: the site comes in as rows, images through a
// function, and a package comes out. SiteHandoverService does the reading and the record keeping.

// An image the package carries and where iPro stores it. A returning package's unchanged image is
// recognized by its content and linked to that address again (section 10, rule 3).
public sealed record HandoverImage(string Ref, string Url, string Sha256, long Bytes);

// Fingerprint (574): what the page said when it was handed over (SiteFingerprint), so a change made in
// iPro afterwards is seen when the package comes back.
// 577: a menu label longer than the builder takes (contract section 3: 40 characters) goes shortened.
// SentLabel is what the builder was given, Label the page's own label then; both are null for a page
// whose own label went (and the record of such a page reads as it did before 577).
// 585: Sections, the rows of the page's sections at the hand-over ("12,15,18"), so a section added in
// iPro since is known for one when the package comes back (null on a record made before 585). A string,
// not a list, so two records of the same page are equal.
public sealed record HandoverPage(
    int Id, string Slug, string Fingerprint = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SentLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sections = null)
{
    public IReadOnlySet<int> SectionIds() =>
        (Sections ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0).ToHashSet();
}

public sealed record HandoverForm(WebsiteForm Form, IReadOnlyList<WebsiteFormField> Fields, IReadOnlyDictionary<int, IReadOnlyList<string>> Options);

// 576: a section the builder does not carry (a calculator, a gallery, a price list), as it was at
// the hand-over. Id is the number its locked box carries (contract section 4.10); PageSlug is the
// address of the page it stood on, BlockId the row the section was. The rest is what the section
// said. When its box returns, the section is written as the site has it NOW if the site still has it
// (SiteImportPlan finds it by that row, or by what it says), and from this record if it does not.
// (A column added to WebsiteContentBlock belongs here too: HostSections576Tests has a test that
// every one of them makes the trip.)
public sealed record HandoverHostSection(
    int Id, string PageSlug, string BlockType, string Heading, string Subheading, string Body, string ImageUrl,
    string ButtonText, string ButtonUrl, string SettingsJson, string LayoutVariant, bool IsVisible, int BlockId = 0)
{
    public static HandoverHostSection From(WebsiteContentBlock block, int id, string pageSlug) => new(
        id, pageSlug, block.BlockType, block.Heading ?? string.Empty, block.Subheading ?? string.Empty, block.Body ?? string.Empty,
        block.ImageUrl ?? string.Empty, block.ButtonText ?? string.Empty, block.ButtonUrl ?? string.Empty,
        string.IsNullOrWhiteSpace(block.SettingsJson) ? "{}" : block.SettingsJson, block.LayoutVariant ?? string.Empty, block.IsVisible, block.Id);

    // Does this section of the site still say what was recorded? Everything a visitor would read
    // or see, and not whether it is shown: a section the customer hid since is still that section.
    public bool Says(WebsiteContentBlock block) =>
        this with { Id = 0, PageSlug = string.Empty, IsVisible = false, BlockId = 0 } == From(block, 0, string.Empty) with { IsVisible = false, BlockId = 0 };

    // A section of the site as a new row at another place, saying and showing what it does now.
    public static WebsiteContentBlock Copy(WebsiteContentBlock block, int sortOrder) => From(block, 0, string.Empty).ToBlock(sortOrder, block.IsVisible);

    // The section as a row again. Where it stands and whether it shows are the box's own: the
    // designer may have moved or hidden it.
    public WebsiteContentBlock ToBlock(int sortOrder, bool isVisible) => new()
    {
        BlockType = BlockType, Heading = Heading, Subheading = Subheading, Body = Body, ImageUrl = ImageUrl,
        ButtonText = ButtonText, ButtonUrl = ButtonUrl, SettingsJson = SettingsJson, LayoutVariant = LayoutVariant,
        SortOrder = sortOrder, IsVisible = isVisible
    };
}

public sealed class SiteHandover
{
    // Why the site cannot be handed over. Empty = the package below is ready.
    public IReadOnlyList<string> Blockers { get; init; } = Array.Empty<string>();
    // What was left out or tidied on the way; the site can still be handed over.
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public bool CanHandOver => Blockers.Count == 0;

    public string HandoverId { get; init; } = string.Empty;
    // The one credential the builder holds for this hand-over. iPro keeps only its hash.
    public string SessionToken { get; init; } = string.Empty;
    public DateTime IssuedAt { get; init; }
    public DateTime SessionExpiresAt { get; init; }
    public string TemplateName { get; init; } = string.Empty;
    public string TemplateJson { get; init; } = string.Empty;
    public string HandoverJson { get; init; } = string.Empty;
    public byte[] Zip { get; init; } = Array.Empty<byte>();
    public string FileName { get; init; } = string.Empty;
    public IReadOnlyList<HandoverImage> Delivered { get; init; } = Array.Empty<HandoverImage>();
    public IReadOnlyList<HandoverPage> Pages { get; init; } = Array.Empty<HandoverPage>();
    // 574: the site's logo address at the hand-over (a logo changed in iPro since is said at the return).
    public string LogoUrl { get; init; } = string.Empty;
    public IReadOnlyList<string> AllowedBlockTypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> ArticleIds { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> FormIds { get; init; } = Array.Empty<int>();
    // 576: the sections that went out as locked boxes, each as it was.
    public IReadOnlyList<HandoverHostSection> HostSections { get; init; } = Array.Empty<HandoverHostSection>();
}

public static class SiteHandoverBuilder
{
    public const string HandoverVersion = "1.0";
    // 576: handover.json lists the locked boxes (catalog.hostSections); contract section 9.2.
    public const string HandoverVersionWithHostSections = "1.1";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);     // section 9.2

    // The catalogue's pictures are for the builder's stand-ins only. They are sent for the newest
    // articles, within a budget, so a customer with hundreds of articles still gets a small package.
    public const int CatalogueImagesMax = 12;
    public const long CatalogueImageBytesMax = 6L * 1024 * 1024;
    public const int CatalogueTriesMax = 20;

    public sealed record Input(
        AgentWebsite Site,
        string BusinessName,
        string BusinessType,
        string TemplateKey,
        IReadOnlyList<WebsitePage> Pages,
        IReadOnlySet<string> AllowedBlockTypes,
        IReadOnlyList<Article> PublishedArticles,
        IReadOnlyList<HandoverForm> ActiveForms,
        long RemainingBytes,
        DateTime UtcNow);

    // fetchImage: the bytes of an image iPro stores, or null when it cannot be read.
    public static async Task<SiteHandover> BuildAsync(Input input, Func<string, Task<byte[]?>> fetchImage)
    {
        var blockers = new List<string>();
        var notes = new List<string>();
        SiteHandover Refused() => new() { Blockers = blockers, Notes = notes };

        if (input.Pages.Count == 0)
        {
            blockers.Add("The site has no pages yet.");
            return Refused();
        }

        // 574: what can be told from the rows alone is told first, and a page that does not fit has
        // none of its pictures fetched. The pictures of the pages that do fit are fetched only up to
        // what a package can hold (AssetAsync). A site with hundreds of large pictures used to be read
        // into memory whole and only then refused.
        var unfit = new HashSet<int>();
        if (input.Pages.Count > BuilderContract.MaxPages)
        {
            blockers.Add($"The site has {input.Pages.Count} pages, and a package holds at most {BuilderContract.MaxPages}.");
        }
        foreach (var page in input.Pages.OrderBy(p => p.SortOrder).ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase))
        {
            var name = string.IsNullOrWhiteSpace(page.Title) ? "/" + page.Slug : page.Title.Trim();
            // 577: the page's title as the builder takes it (contract section 3), said where it is
            // changed. (A menu label that is too long goes shortened instead: see the pages, below.)
            var title = page.Title ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title) || title.Length > BuilderContract.MaxTitle)
            {
                blockers.Add(string.IsNullOrWhiteSpace(title)
                    ? $"The page at /{page.Slug} has no title. Give it one in the page editor (its Page Title field)."
                    : $"\"{BuilderPackageReader.Shorten(name)}\" has a title of {title.Length} characters, and the builder takes at most {BuilderContract.MaxTitle}. " +
                      "Shorten it in the page editor (its Page Title field).");
                unfit.Add(page.Id);
            }
            var types = page.Blocks.Select(b => b.BlockType).ToList();
            if (types.Count == 0)
            {
                blockers.Add($"\"{name}\" has no sections of its own (it shows the design's standard layout), so there is nothing to hand over. Add a section to it in the page editor first.");
                unfit.Add(page.Id);
                continue;
            }
            if (types.Count > BuilderContract.MaxBlocksPerPage)
            {
                blockers.Add($"\"{name}\" has {types.Count} sections, and a page in a package holds at most {BuilderContract.MaxBlocksPerPage}.");
                unfit.Add(page.Id);
            }
            // 576: a section of a kind the builder does not carry (a calculator, a gallery, a price
            // list) no longer stops the hand-over: it travels as a locked box (the pages, below).
            var outside = types.Where(t => BuilderContract.BlockTypes.Contains(t, StringComparer.Ordinal) && !input.AllowedBlockTypes.Contains(t)).Distinct().ToList();
            if (outside.Count > 0)
            {
                blockers.Add($"\"{name}\" has sections this customer's package no longer includes: {string.Join(", ", outside.Select(WebsiteBlockTypes.DisplayName))}.");
                unfit.Add(page.Id);
            }
        }
        if (input.Pages.Count > BuilderContract.MaxPages) return Refused();

        var handoverId = "h_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var businessName = Clip(OneLine(input.BusinessName), 120);
        if (businessName.Length == 0) businessName = "Website";
        var templateName = TemplateNameFor(businessName, input.Site.Id);

        // ---- images: every picture the site shows, each file once ----
        var files = new List<(string Name, byte[] Bytes)>();
        var refByUrl = new Dictionary<string, string>(StringComparer.Ordinal);
        var refBySha = new Dictionary<string, string>(StringComparer.Ordinal);
        var delivered = new List<HandoverImage>();

        long pictureBytes = 0;
        var pastWhatAPackageHolds = false;
        async Task<string> AssetAsync(string url, string where)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            if (refByUrl.TryGetValue(url, out var known)) return known;
            // 574: once the pictures alone are past what a package may hold, no more are fetched.
            if (pastWhatAPackageHolds) return refByUrl[url] = string.Empty;
            var bytes = await fetchImage(url);
            if (bytes == null || bytes.Length == 0)
            {
                blockers.Add($"The picture {where} could not be read from iPro's storage ({ShortUrl(url)}). Choose it again in the page editor.");
                return refByUrl[url] = string.Empty;
            }
            var extension = ExtensionFor(bytes);
            if (extension.Length == 0)
            {
                blockers.Add($"The picture {where} is not a JPG, PNG, GIF or WebP image ({ShortUrl(url)}), so the builder cannot carry it.");
                return refByUrl[url] = string.Empty;
            }
            if (bytes.LongLength > BuilderContract.MaxAssetBytes)
            {
                blockers.Add($"The picture {where} is larger than 8 MB ({ShortUrl(url)}).");
                return refByUrl[url] = string.Empty;
            }
            var sha = BuilderPackageReader.Hex(SHA256.HashData(bytes));
            if (!refBySha.TryGetValue(sha, out var assetRef))
            {
                pictureBytes += bytes.LongLength;
                if (pictureBytes > BuilderContract.MaxZipBytes)
                {
                    pastWhatAPackageHolds = true;
                    blockers.Add("The site's pictures come to more than a 40 MB package can hold. Use fewer or smaller pictures on the site, then hand it over.");
                    return refByUrl[url] = string.Empty;
                }
                assetRef = BuilderContract.AssetFolder + ImageFileName(url, sha, extension);
                refBySha[sha] = assetRef;
                files.Add((assetRef, bytes));
                delivered.Add(new HandoverImage(assetRef, url, sha, bytes.LongLength));
            }
            return refByUrl[url] = assetRef;
        }

        // ---- pages ----
        var byId = input.Pages.ToDictionary(p => p.Id);
        var orderInGroup = new Dictionary<int, int>();
        foreach (var group in input.Pages.GroupBy(p => p.ParentPageId.HasValue && byId.ContainsKey(p.ParentPageId.Value) ? p.ParentPageId.Value : 0))
        {
            var order = 0;
            foreach (var page in group.OrderBy(p => p.SortOrder).ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)) orderInGroup[page.Id] = order++;
        }

        var articleIds = input.PublishedArticles.Select(a => a.Id).ToHashSet();
        var formIds = input.ActiveForms.Select(f => f.Form.Id).ToHashSet();
        var pagesOut = new List<(WebsitePage Page, List<Dictionary<string, object?>> Blocks)>();
        var hostSections = new List<HandoverHostSection>();
        foreach (var page in input.Pages.OrderBy(p => p.ParentPageId.HasValue ? 1 : 0).ThenBy(p => orderInGroup[p.Id]))
        {
            var name = string.IsNullOrWhiteSpace(page.Title) ? "/" + page.Slug : page.Title.Trim();
            if (unfit.Contains(page.Id)) continue;
            var blocks = page.Blocks.OrderBy(b => b.SortOrder).ThenBy(b => b.Id).ToList();
            var written = new List<Dictionary<string, object?>>();
            var position = 0;
            foreach (var block in blocks)
            {
                if (!BuilderContract.BlockTypes.Contains(block.BlockType, StringComparer.Ordinal))
                {
                    // 576: the section stays iPro's. Its box carries a number and nothing else, and
                    // handover.json names it by its kind and its heading. Nothing else of it leaves
                    // (not its text, its settings or its pictures): the section is kept on the
                    // hand-over's record, to be written back when the box returns.
                    var host = HandoverHostSection.From(block, hostSections.Count + 1, page.Slug);
                    hostSections.Add(host);
                    written.Add(HostBlock(host.Id, position++, block.IsVisible));
                    continue;
                }
                written.Add(await BlockAsync(block, position++, name, notes, AssetAsync));
            }
            pagesOut.Add((page, written));
        }
        if (hostSections.Count > 0)
        {
            var kinds = hostSections.GroupBy(h => h.BlockType)
                .Select(g => (g.Count() > 1 ? g.Count() + " x " : string.Empty) + WebsiteBlockTypes.DisplayName(g.Key));
            notes.Add((hostSections.Count == 1 ? "One section travels as a locked box" : $"{hostSections.Count} sections travel as locked boxes") +
                $" ({string.Join(", ", kinds)}): the builder shows where each stands and can move, hide or remove it. What it says stays as iPro has it.");
        }

        // 577: the menu label each page goes with. One longer than the builder takes (contract section
        // 3) goes shortened, and the hand-over keeps the page's own: a label that comes back as it went
        // is the page's own label again (SiteImportPlan). iPro's starter articles are named in the menu
        // by their whole title, so most sites have such a page; every one of them used to stop the hand-over.
        // (A label that is long only for its spaces goes on one line, which is tidying and not shortening.)
        var sentLabel = new Dictionary<int, string>();
        var shortened = new List<(int PageId, string Own, string Sent)>();
        foreach (var (page, _) in pagesOut)
        {
            var menu = MenuText(page);
            sentLabel[page.Id] = BuilderLabel(menu);
            if (sentLabel[page.Id] != menu && sentLabel[page.Id] != OneLine(menu)) shortened.Add((page.Id, OneLine(menu), sentLabel[page.Id]));
        }
        if (shortened.Count > 0)
        {
            var which = string.Join("; ", shortened.Select(s => $"\"{s.Own}\" as \"{s.Sent}\""));
            notes.Add((shortened.Count == 1 ? "One menu label is" : $"{shortened.Count} menu labels are") +
                $" longer than the {BuilderContract.MaxNavigationLabel} characters the builder takes, so the builder shows " +
                (shortened.Count == 1 ? "it" : "them") + $" shortened: {which}. " +
                "Left unchanged in the builder, a label comes back as the page's own; a label changed in the builder is used.");
        }
        var shortenedIds = shortened.Select(s => s.PageId).ToHashSet();
        // v0.5 is stated only where it is used: a site with no such section is a v0.4 package as before.
        var version = hostSections.Count > 0 ? BuilderContract.VersionWithHostSections : BuilderContract.Version;

        var logoRef = await AssetAsync(input.Site.LogoUrl ?? string.Empty, "used as the site's logo");
        if (blockers.Count > 0) return Refused();

        // ---- the catalogue: what the customer's article and form sections can point at ----
        var catalogueFiles = new List<(string Name, byte[] Bytes)>();
        var articles = new List<Dictionary<string, object?>>();
        long catalogueBytes = 0;
        var catalogueNames = new HashSet<string>(StringComparer.Ordinal);
        // 574: the budget is on what is FETCHED, not only on what is kept. Before, a customer with
        // hundreds of articles had every remaining picture downloaded and thrown away once the
        // bytes were spent.
        var catalogueTries = 0;
        var catalogueFull = false;
        foreach (var article in input.PublishedArticles)
        {
            var imageRef = string.Empty;
            if (!string.IsNullOrWhiteSpace(article.ImageUrl) && !catalogueFull && catalogueFiles.Count < CatalogueImagesMax && catalogueTries < CatalogueTriesMax)
            {
                catalogueTries++;
                var bytes = await fetchImage(article.ImageUrl);
                var extension = bytes == null ? string.Empty : ExtensionFor(bytes);
                if (bytes != null && extension.Length > 0 && bytes.LongLength <= BuilderContract.MaxAssetBytes
                    && catalogueBytes + bytes.LongLength > CatalogueImageBytesMax)
                {
                    catalogueFull = true;
                }
                else if (bytes != null && extension.Length > 0 && bytes.LongLength <= BuilderContract.MaxAssetBytes)
                {
                    var file = ImageFileName(article.ImageUrl, BuilderPackageReader.Hex(SHA256.HashData(bytes)), extension);
                    if (catalogueNames.Add(file))
                    {
                        catalogueFiles.Add(("catalog/" + file, bytes));
                        catalogueBytes += bytes.LongLength;
                        imageRef = "catalog/" + file;
                    }
                }
            }
            articles.Add(new Dictionary<string, object?>
            {
                ["id"] = article.Id,
                ["title"] = Clip(OneLine(article.Title), 200),
                ["summary"] = Clip(OneLine(article.Summary), 500),
                ["imageRef"] = imageRef,
                ["publishedAt"] = Iso(article.PublishedAt ?? article.CreatedAt)
            });
        }

        var forms = input.ActiveForms.Select(f => new Dictionary<string, object?>
        {
            ["id"] = f.Form.Id,
            ["title"] = Clip(OneLine(f.Form.Title), 200),
            ["description"] = Clip(f.Form.Description ?? string.Empty, 1000),
            ["submitButtonText"] = Clip(OneLine(f.Form.SubmitButtonText), 60),
            ["fields"] = f.Fields.OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
                .Where(x => WebsiteFormFieldTypes.All.Contains(x.FieldType))
                .Select(x => (object?)new Dictionary<string, object?>
                {
                    ["id"] = x.Id,
                    ["type"] = x.FieldType,
                    ["label"] = Clip(OneLine(x.Label), 200),
                    ["placeholder"] = Clip(OneLine(x.Placeholder), 200),
                    ["helpText"] = Clip(OneLine(x.HelpText), 500),
                    ["required"] = x.FieldType != WebsiteFormFieldTypes.Section && x.IsRequired,
                    ["options"] = WebsiteFormFieldTypes.SupportsOptions(x.FieldType) && f.Options.TryGetValue(x.Id, out var options)
                        ? options.Select(o => (object?)Clip(OneLine(o), 200)).ToList()
                        : new List<object?>()
                }).ToList()
        }).ToList();

        // ---- template.json ----
        var slugById = input.Pages.ToDictionary(p => p.Id, p => p.Slug);
        var fingerprints = SiteFingerprint.OfPages(input.Pages);
        var template = new Dictionary<string, object?>
        {
            ["contractVersion"] = version,
            ["templateName"] = templateName,
            ["theme"] = ThemeFor(input.TemplateKey),
            ["handoverId"] = handoverId,
            ["logoRef"] = logoRef,
            ["logoAltText"] = logoRef.Length == 0 ? string.Empty : Clip(businessName, BuilderContract.MaxLogoAltText),
            ["pages"] = pagesOut.Select(p => (object?)new Dictionary<string, object?>
            {
                ["slug"] = p.Page.Slug,
                ["parentSlug"] = p.Page.ParentPageId.HasValue && slugById.TryGetValue(p.Page.ParentPageId.Value, out var parent) ? parent : null,
                ["title"] = p.Page.Title,
                ["navigationLabel"] = sentLabel[p.Page.Id],
                ["isHomePage"] = p.Page.IsHomePage,
                ["sortOrder"] = orderInGroup[p.Page.Id],
                ["blocks"] = p.Blocks.Cast<object?>().ToList()
            }).ToList(),
            ["assets"] = files.OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => (object?)new Dictionary<string, object?> { ["ref"] = f.Name, ["type"] = BuilderContract.ContentTypeFor(f.Name) }).ToList()
        };
        var templateJson = Json(template);

        // ---- the door: would iPro take this back unchanged? ----
        var allowed = BuilderContract.BlockTypes.Where(input.AllowedBlockTypes.Contains).ToList();
        var context = new BuilderImportContext(handoverId, allowed.ToHashSet(StringComparer.Ordinal), articleIds, formIds,
            delivered.Select(d => d.Sha256).ToHashSet(StringComparer.Ordinal), input.RemainingBytes,
            hostSections.Select(h => h.Id).ToHashSet());
        var asReturn = BuilderPackageReader.Read(Zip(new[] { (BuilderContract.TemplateFile, Utf8(templateJson)) }.Concat(files)), context);
        if (!asReturn.IsValid)
        {
            blockers.AddRange(asReturn.Problems.Select(p => $"{p.Where}: {p.Message}"));
            return Refused();
        }

        // ---- handover.json ----
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var issuedAt = new DateTime(input.UtcNow.Ticks - input.UtcNow.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var expiresAt = issuedAt.Add(SessionLifetime);
        var catalogue = new Dictionary<string, object?> { ["articles"] = articles.Cast<object?>().ToList(), ["forms"] = forms.Cast<object?>().ToList() };
        if (hostSections.Count > 0)
        {
            // Section 9.3: enough to name each box (its kind, and its heading to tell two of a kind
            // apart), and nothing else of what its section holds.
            catalogue["hostSections"] = hostSections.Select(h => (object?)new Dictionary<string, object?>
            {
                ["id"] = h.Id,
                ["kind"] = HostKind(h.BlockType),
                ["label"] = HostLabel(h.BlockType),
                ["title"] = Clip(OneLine(h.Heading), BuilderContract.MaxHostSectionTitle)
            }).ToList();
        }
        var handover = new Dictionary<string, object?>
        {
            ["handoverVersion"] = hostSections.Count > 0 ? HandoverVersionWithHostSections : HandoverVersion,
            ["handoverId"] = handoverId,
            ["contractVersion"] = version,
            ["issuedAt"] = Iso(issuedAt),
            ["sessionToken"] = sessionToken,
            ["sessionExpiresAt"] = Iso(expiresAt),
            ["adviser"] = new Dictionary<string, object?> { ["businessName"] = businessName, ["vertical"] = VerticalFor(input.BusinessType) },
            ["allowedBlockTypes"] = allowed.Cast<object?>().ToList(),
            ["uploads"] = new Dictionary<string, object?>
            {
                ["maxFileBytes"] = BuilderContract.MaxAssetBytes,
                ["remainingBytes"] = Math.Max(0, input.RemainingBytes == long.MaxValue ? 1024L * 1024 * 1024 : input.RemainingBytes)
            },
            ["catalog"] = catalogue
        };
        var handoverJson = Json(handover);

        var zip = Zip(new[] { ("handover.json", Utf8(handoverJson)), (BuilderContract.TemplateFile, Utf8(templateJson)) }.Concat(files).Concat(catalogueFiles));
        if (zip.LongLength > BuilderContract.MaxZipBytes)
        {
            blockers.Add($"The site's pictures make a package of {zip.LongLength / 1024.0 / 1024.0:0.0} MB, and a package may be at most 40 MB.");
            return Refused();
        }

        return new SiteHandover
        {
            Notes = notes,
            HandoverId = handoverId,
            SessionToken = sessionToken,
            IssuedAt = issuedAt,
            SessionExpiresAt = expiresAt,
            TemplateName = templateName,
            TemplateJson = templateJson,
            HandoverJson = handoverJson,
            Zip = zip,
            FileName = $"ipro-handover-{templateName}-{issuedAt:yyyyMMdd-HHmm}.zip",
            Delivered = delivered,
            Pages = pagesOut.Select(p => shortenedIds.Contains(p.Page.Id)
                ? new HandoverPage(p.Page.Id, p.Page.Slug, fingerprints[p.Page.Id], p.Page.NavigationLabel ?? string.Empty, sentLabel[p.Page.Id], SectionIds(p.Page))
                : new HandoverPage(p.Page.Id, p.Page.Slug, fingerprints[p.Page.Id], Sections: SectionIds(p.Page))).ToList(),
            LogoUrl = input.Site.LogoUrl ?? string.Empty,
            AllowedBlockTypes = allowed,
            ArticleIds = articleIds.OrderBy(id => id).ToList(),
            FormIds = formIds.OrderBy(id => id).ToList(),
            HostSections = hostSections
        };
    }

    // 585: the rows of a page's sections as it is handed over (HandoverPage.Sections).
    private static string SectionIds(WebsitePage page) =>
        string.Join(",", page.Blocks.Select(b => b.Id).Where(id => id > 0).OrderBy(id => id).Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    // Section 4.10: a locked box. Every field empty; the section it stands for is named by number.
    private static Dictionary<string, object?> HostBlock(int id, int sortOrder, bool isVisible) => new()
    {
        ["type"] = BuilderContract.HostSection,
        ["heading"] = string.Empty,
        ["subheading"] = string.Empty,
        ["body"] = string.Empty,
        ["imageRef"] = string.Empty,
        ["buttonText"] = string.Empty,
        ["buttonUrl"] = string.Empty,
        ["layoutVariant"] = string.Empty,
        ["sortOrder"] = sortOrder,
        ["isVisible"] = isVisible,
        ["settings"] = new Dictionary<string, object?> { ["hostSectionId"] = id }
    };

    // iPro's own name for the kind of section, for the builder's icon at most (section 9.3).
    private static string HostKind(string blockType) =>
        BuilderContract.HostSectionKind.IsMatch(blockType ?? string.Empty) ? blockType! : "Section";

    // What the box is called: the name the page editor gives that kind of section.
    private static string HostLabel(string blockType)
    {
        var label = Clip(OneLine(WebsiteBlockTypes.DisplayName(blockType ?? string.Empty)), BuilderContract.MaxHostSectionLabel);
        return label.Length > 0 ? label : "Section";
    }

    // One section as the contract writes it: every key, a field its type does not use left empty.
    private static async Task<Dictionary<string, object?>> BlockAsync(
        WebsiteContentBlock block, int sortOrder, string pageName, List<string> notes, Func<string, string, Task<string>> assetAsync)
    {
        var type = block.BlockType;
        var unused = BuilderContract.UnusedFields(type);
        string Field(string key, string? value) => unused.Contains(key) ? string.Empty : value ?? string.Empty;

        var buttonText = Field("buttonText", block.ButtonText).Trim();
        var buttonUrl = LowerScheme(Field("buttonUrl", block.ButtonUrl).Trim());
        if (type == WebsiteBlockTypes.Text && (buttonText.Length == 0) != (buttonUrl.Length == 0))
        {
            // On the live site such a button either leads nowhere or is not drawn at all.
            if (buttonText.Length > 0) notes.Add($"\"{pageName}\": the button \"{buttonText}\" has no link, so it is left out.");
            buttonText = buttonUrl = string.Empty;
        }

        var usesImage = !unused.Contains("imageRef");
        var imageRef = usesImage ? await assetAsync(block.ImageUrl ?? string.Empty, $"on \"{pageName}\"") : string.Empty;

        object settings = type switch
        {
            WebsiteBlockTypes.Hero => HeroSettings(WebsiteHeroSettings.FromJson(block.SettingsJson)),
            WebsiteBlockTypes.ContactForm => new Dictionary<string, object?> { ["showPhoto"] = WebsiteContactFormSettings.FromJson(block.SettingsJson).ShowPhoto },
            WebsiteBlockTypes.ArticleContent => new Dictionary<string, object?> { ["articleId"] = WebsiteArticleContentSettings.FromJson(block.SettingsJson).ArticleId },
            WebsiteBlockTypes.Blog => BlogSettings(WebsiteBlogSettings.FromJson(block.SettingsJson)),
            WebsiteBlockTypes.Form => new Dictionary<string, object?> { ["websiteFormId"] = WebsiteFormSettings.FromJson(block.SettingsJson).WebsiteFormId },
            _ => new Dictionary<string, object?>()
        };

        return new Dictionary<string, object?>
        {
            ["type"] = type,
            ["heading"] = Field("heading", block.Heading),
            ["subheading"] = Field("subheading", block.Subheading),
            ["body"] = Field("body", block.Body).Replace("\r\n", "\n").Replace('\r', '\n'),
            ["imageRef"] = imageRef,
            ["buttonText"] = buttonText,
            ["buttonUrl"] = buttonUrl,
            ["layoutVariant"] = WebsiteBlockLayoutVariants.Normalize(type, block.LayoutVariant),
            ["sortOrder"] = sortOrder,
            ["isVisible"] = block.IsVisible,
            ["settings"] = settings
        };
    }

    // iPro's page editor takes "Tel:" and "MAILTO:" as typed; the contract writes a scheme in lower case.
    private static string LowerScheme(string url)
    {
        foreach (var scheme in new[] { "https://", "mailto:", "tel:" })
        {
            if (url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return scheme + url[scheme.Length..];
        }
        return url;
    }

    private static Dictionary<string, object?> HeroSettings(WebsiteHeroSettings hero) => new()
    {
        ["layout"] = hero.Layout,
        ["imagePosition"] = hero.ImagePosition,
        ["textAlignment"] = hero.TextAlignment,
        ["height"] = hero.Height,
        ["overlayStrength"] = hero.OverlayStrength
    };

    private static Dictionary<string, object?> BlogSettings(WebsiteBlogSettings blog) => new()
    {
        ["postCount"] = blog.EffectivePostCount,
        ["showImages"] = blog.ShowImages
    };

    // ---- small things ----

    // The menu label a page goes to the builder with: its own, or its title when it has none. (The page
    // editor never saves an empty label; the public menu prints the label as it is.)
    private static string MenuText(WebsitePage page) =>
        string.IsNullOrWhiteSpace(page.NavigationLabel) ? page.Title ?? string.Empty : page.NavigationLabel;

    // 577: a menu label as the builder takes it (contract section 3: at most 40 characters). One that
    // fits goes as it is. A longer one goes on one line and, if still too long, ends in an ellipsis, so
    // the designer can see it was shortened: it is cut after the last whole word that fits, unless that
    // would keep less than half of the room (one very long word), and then inside the word.
    public static string BuilderLabel(string? label)
    {
        var text = label ?? string.Empty;
        if (text.Length <= BuilderContract.MaxNavigationLabel) return text;
        text = OneLine(text);
        if (text.Length <= BuilderContract.MaxNavigationLabel) return text;
        var room = BuilderContract.MaxNavigationLabel - 1;                 // the ellipsis takes the last place
        var space = text.LastIndexOf(' ', room);                           // the words before it fit whole
        var cut = space >= room / 2 ? text[..space].TrimEnd(Untidy) : string.Empty;
        if (cut.Length == 0)
        {
            if (char.IsHighSurrogate(text[room - 1])) room--;              // never half of a character
            cut = text[..room];
        }
        return cut + Ellipsis;
    }

    // What a label does not end on before its ellipsis: a space, or a mark that joins it to the words cut off.
    private static readonly char[] Untidy = { ' ', ',', ';', ':', '-', '/', '&', '(', (char)0x2013, (char)0x2014 };

    private static readonly string Ellipsis = ((char)0x2026).ToString();

    // The host's design a site uses, as the contract's theme hint.
    public static string ThemeFor(string? templateKey)
    {
        var key = templateKey ?? string.Empty;
        return key.Contains("classic", StringComparison.OrdinalIgnoreCase) ? "classic"
            : key.Contains("editorial", StringComparison.OrdinalIgnoreCase) ? "editorial"
            : "modern";
    }

    // The contract names three verticals. A business of any other kind travels as "advisers" until the
    // contract has a value for it (v0.5 did not add one); the builder uses this for display defaults only.
    public static string VerticalFor(string? businessType)
    {
        var type = businessType ?? string.Empty;
        return type.Contains("Accountant", StringComparison.OrdinalIgnoreCase) ? "accountants"
            : type.Contains("Mortgage", StringComparison.OrdinalIgnoreCase) ? "mortgage"
            : "advisers";
    }

    public static string TemplateNameFor(string businessName, int siteId)
    {
        var slug = Regex.Replace(businessName.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 60) slug = slug[..60].Trim('-');
        return slug.Length >= 3 ? slug : $"site-{siteId}";
    }

    // What the file is, by its first bytes: "jpg", "png", "gif", "webp", or "" for anything else.
    public static string ExtensionFor(byte[] bytes)
    {
        foreach (var extension in new[] { "jpg", "png", "gif", "webp" })
        {
            if (BuilderContract.HasImageSignature(bytes, "x." + extension)) return extension;
        }
        return string.Empty;
    }

    // A file name the contract allows, readable where it can be and unique by content: iPro's own
    // "<32 hex>_" storage prefix goes, the first eight digits of the hash come on the end.
    public static string ImageFileName(string url, string sha256, string extension)
    {
        var last = url.Split('?')[0].Split('/').LastOrDefault() ?? string.Empty;
        try { last = Uri.UnescapeDataString(last); } catch (UriFormatException) { }
        last = Regex.Replace(last, "^[0-9a-fA-F]{32}_", string.Empty);
        var stem = Path.GetFileNameWithoutExtension(last).ToLowerInvariant();
        stem = Regex.Replace(stem, "[^a-z0-9]+", "-").Trim('-');
        if (stem.Length > 60) stem = stem[..60].Trim('-');
        if (stem.Length == 0) stem = "image";
        return $"{stem}-{sha256[..8]}.{extension}";
    }

    private static string Iso(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string OneLine(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];

    private static string ShortUrl(string url)
    {
        var last = url.Split('?')[0].Split('/').LastOrDefault() ?? url;
        return last.Length <= 60 ? last : last[..60] + "...";
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

    // Indented, LF line endings, a final newline, and text left readable (the file is read by
    // people as well as by the builder; it is never placed inside a web page).
    private static string Json(Dictionary<string, object?> value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            Write(writer, value);
        }
        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case int number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case Dictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map)
                {
                    writer.WritePropertyName(key);
                    Write(writer, item);
                }
                writer.WriteEndObject();
                break;
            case IEnumerable<object?> list:
                writer.WriteStartArray();
                foreach (var item in list) Write(writer, item);
                writer.WriteEndArray();
                break;
            default: throw new InvalidOperationException($"Cannot write a {value.GetType().Name} into a package.");
        }
    }

    private static byte[] Zip(IEnumerable<(string Name, byte[] Bytes)> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                // Images are compressed already; storing them keeps the package quick to make.
                var entry = zip.CreateEntry(name, name.EndsWith(".json", StringComparison.Ordinal) ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        return buffer.ToArray();
    }
}
