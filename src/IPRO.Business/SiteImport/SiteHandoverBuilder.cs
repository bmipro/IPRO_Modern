using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using IPRO.Entities;

namespace IPRO.Business.SiteImport;

// 572: iPro hands a customer's site to the Template Builder (export contract v0.4, section 9). The
// package holds the site as a template (template.json, assets/) and what the builder needs to edit
// it without owning any of iPro's content (handover.json, catalog/).
//
// A site is handed over whole or not at all (contract section 12): every section one of the types
// the builder carries and the customer's package includes, every page and field within the
// contract. Whether it fits is decided by the SAME door a returning package goes through
// (BuilderPackageReader), so iPro never hands over something it would refuse to take back.
//
// Nothing here touches the database or storage: the site comes in as rows, images through a
// function, and a package comes out. SiteHandoverService does the reading and the record keeping.

// An image the package carries and where iPro stores it. A returning package's unchanged image is
// recognized by its content and linked to that address again (section 10, rule 3).
public sealed record HandoverImage(string Ref, string Url, string Sha256, long Bytes);

public sealed record HandoverPage(int Id, string Slug);

public sealed record HandoverForm(WebsiteForm Form, IReadOnlyList<WebsiteFormField> Fields, IReadOnlyDictionary<int, IReadOnlyList<string>> Options);

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
    public IReadOnlyList<string> AllowedBlockTypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> ArticleIds { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> FormIds { get; init; } = Array.Empty<int>();
}

public static class SiteHandoverBuilder
{
    public const string HandoverVersion = "1.0";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);     // section 9.2

    // The catalogue's pictures are for the builder's stand-ins only. They are sent for the newest
    // articles, within a budget, so a customer with hundreds of articles still gets a small package.
    public const int CatalogueImagesMax = 12;
    public const long CatalogueImageBytesMax = 6L * 1024 * 1024;

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

        var handoverId = "h_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var businessName = Clip(OneLine(input.BusinessName), 120);
        if (businessName.Length == 0) businessName = "Website";
        var templateName = TemplateNameFor(businessName, input.Site.Id);

        // ---- images: every picture the site shows, each file once ----
        var files = new List<(string Name, byte[] Bytes)>();
        var refByUrl = new Dictionary<string, string>(StringComparer.Ordinal);
        var refBySha = new Dictionary<string, string>(StringComparer.Ordinal);
        var delivered = new List<HandoverImage>();

        async Task<string> AssetAsync(string url, string where)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            if (refByUrl.TryGetValue(url, out var known)) return known;
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
        foreach (var page in input.Pages.OrderBy(p => p.ParentPageId.HasValue ? 1 : 0).ThenBy(p => orderInGroup[p.Id]))
        {
            var name = string.IsNullOrWhiteSpace(page.Title) ? "/" + page.Slug : page.Title.Trim();
            var blocks = page.Blocks.OrderBy(b => b.SortOrder).ThenBy(b => b.Id).ToList();
            if (blocks.Count == 0)
            {
                blockers.Add($"\"{name}\" has no sections of its own (it shows the design's standard layout), so there is nothing to hand over. Add a section to it in the page editor first.");
                continue;
            }
            var foreign = blocks.Select(b => b.BlockType).Where(t => !BuilderContract.BlockTypes.Contains(t, StringComparer.Ordinal)).Distinct().ToList();
            if (foreign.Count > 0)
            {
                blockers.Add($"\"{name}\" has sections the builder cannot carry yet: {string.Join(", ", foreign.Select(WebsiteBlockTypes.DisplayName))}.");
            }
            var outside = blocks.Select(b => b.BlockType).Where(t => BuilderContract.BlockTypes.Contains(t, StringComparer.Ordinal) && !input.AllowedBlockTypes.Contains(t)).Distinct().ToList();
            if (outside.Count > 0)
            {
                blockers.Add($"\"{name}\" has sections this customer's package no longer includes: {string.Join(", ", outside.Select(WebsiteBlockTypes.DisplayName))}.");
            }
            if (foreign.Count > 0 || outside.Count > 0) continue;

            var written = new List<Dictionary<string, object?>>();
            var position = 0;
            foreach (var block in blocks)
            {
                written.Add(await BlockAsync(block, position++, name, notes, AssetAsync));
            }
            pagesOut.Add((page, written));
        }

        var logoRef = await AssetAsync(input.Site.LogoUrl ?? string.Empty, "used as the site's logo");
        if (blockers.Count > 0) return Refused();

        // ---- the catalogue: what the customer's article and form sections can point at ----
        var catalogueFiles = new List<(string Name, byte[] Bytes)>();
        var articles = new List<Dictionary<string, object?>>();
        long catalogueBytes = 0;
        var catalogueNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var article in input.PublishedArticles)
        {
            var imageRef = string.Empty;
            if (!string.IsNullOrWhiteSpace(article.ImageUrl) && catalogueFiles.Count < CatalogueImagesMax)
            {
                var bytes = await fetchImage(article.ImageUrl);
                var extension = bytes == null ? string.Empty : ExtensionFor(bytes);
                if (bytes != null && extension.Length > 0 && bytes.LongLength <= BuilderContract.MaxAssetBytes
                    && catalogueBytes + bytes.LongLength <= CatalogueImageBytesMax)
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
        var template = new Dictionary<string, object?>
        {
            ["contractVersion"] = BuilderContract.Version,
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
                ["navigationLabel"] = string.IsNullOrWhiteSpace(p.Page.NavigationLabel) ? p.Page.Title : p.Page.NavigationLabel,
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
            delivered.Select(d => d.Sha256).ToHashSet(StringComparer.Ordinal), input.RemainingBytes);
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
        var handover = new Dictionary<string, object?>
        {
            ["handoverVersion"] = HandoverVersion,
            ["handoverId"] = handoverId,
            ["contractVersion"] = BuilderContract.Version,
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
            ["catalog"] = new Dictionary<string, object?> { ["articles"] = articles.Cast<object?>().ToList(), ["forms"] = forms.Cast<object?>().ToList() }
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
            Pages = pagesOut.Select(p => new HandoverPage(p.Page.Id, p.Page.Slug)).ToList(),
            AllowedBlockTypes = allowed,
            ArticleIds = articleIds.OrderBy(id => id).ToList(),
            FormIds = formIds.OrderBy(id => id).ToList()
        };
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

    // The host's design a site uses, as the contract's theme hint.
    public static string ThemeFor(string? templateKey)
    {
        var key = templateKey ?? string.Empty;
        return key.Contains("classic", StringComparison.OrdinalIgnoreCase) ? "classic"
            : key.Contains("editorial", StringComparison.OrdinalIgnoreCase) ? "editorial"
            : "modern";
    }

    // v0.4 names three verticals. A business of any other kind travels as "advisers" until the
    // contract has a value for it (the v0.5 draft); the builder uses this for display defaults only.
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
