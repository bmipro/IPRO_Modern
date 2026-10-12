using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPRO.Entities;

namespace IPRO.Business.SiteImport;

// 571: reads a Template Builder package and says whether iPro takes it. Every reason for refusing is
// a rule CONTRACT.md states (v0.4, with the clarifications of 2026-09-29), named with its section so
// the builder's developer can look it up; every reason is reported, not only the first.
//
// Nothing here touches the database or storage. A package is bytes in, a verdict out.
public static class BuilderPackageReader
{
    // A package as it arrives: the ZIP.
    public static BuilderPackage Read(byte[] zip, BuilderImportContext context)
    {
        var problems = new ProblemList();
        var sha = Hex(SHA256.HashData(zip));
        BuilderPackage Fail(string json = "") => new() { Problems = problems.ToList(), TemplateJson = json, Sha256 = sha };

        if (zip.LongLength > BuilderContract.MaxZipBytes)
        {
            problems.Add(null, null, "1, rule 5", $"The package is {Mb(zip.LongLength)} MB. A package may be at most 40 MB.");
            return Fail();
        }

        byte[]? templateBytes = null;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);   // "assets/<name>" -> the file
        var oversize = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
            if (archive.Entries.Count > BuilderContract.MaxEntries)
            {
                problems.Add(null, null, "1", $"The package holds {archive.Entries.Count} entries, far more than a site has images. It was not read.");
                return Fail();
            }

            long expanded = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var handoverPackage = false;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (name.EndsWith('/')) continue;   // a folder entry: allowed and ignored (only files count)
                if (!seen.Add(name))
                {
                    problems.Add(null, null, "1", $"\"{name}\" is in the package twice.");
                    continue;
                }
                if (name.Contains('\\') || name.StartsWith('/') || name.Split('/').Any(part => part is ".." or "."))
                {
                    problems.Add(null, null, "1, rule 4", $"\"{name}\" is not a file name a package may hold (no backslashes, no \"..\", no leading slash).");
                    continue;
                }

                if (name == BuilderContract.TemplateFile)
                {
                    templateBytes = ReadCapped(entry, BuilderContract.MaxTemplateJsonBytes, ref expanded);
                    if (templateBytes == null) problems.Add(null, null, "1", "template.json is larger than any site's content can be. It was not read.");
                    continue;
                }

                if (name.StartsWith(BuilderContract.AssetFolder, StringComparison.Ordinal))
                {
                    var file = name[BuilderContract.AssetFolder.Length..];
                    if (file.Contains('/'))
                    {
                        problems.Add(null, null, "1, rule 2", $"\"{name}\" is inside a folder under assets/. The assets folder is flat.");
                        continue;
                    }
                    if (BuilderContract.ContentTypeFor(file).Length == 0)
                    {
                        problems.Add(null, null, "1, rule 3", $"\"{name}\" is not an image type a package may hold (.jpg, .jpeg, .png, .gif or .webp).");
                        continue;
                    }
                    if (!BuilderContract.AssetFileName.IsMatch(file))
                    {
                        problems.Add(null, null, "1, rule 4", $"\"{name}\": an image's name is lower-case letters, digits and hyphens, then its extension.");
                        continue;
                    }
                    if (file.Length > BuilderContract.MaxFileNameLength)
                    {
                        problems.Add(null, null, "1, rule 4", $"\"{name}\": a file name may be at most {BuilderContract.MaxFileNameLength} characters, extension included.");
                        continue;
                    }
                    var bytes = ReadCapped(entry, BuilderContract.MaxAssetBytes, ref expanded);
                    if (bytes == null)
                    {
                        problems.Add(null, null, "1, rule 5", $"\"{name}\" is larger than 8 MB.");
                        oversize.Add(name);
                        continue;
                    }
                    files[name] = bytes;
                    continue;
                }

                if (name == "handover.json" || name.StartsWith("catalog/", StringComparison.Ordinal))
                {
                    handoverPackage = true;
                    continue;
                }

                problems.Add(null, null, "1, rule 3", name.EndsWith("/" + BuilderContract.TemplateFile, StringComparison.Ordinal)
                    ? $"template.json is inside a folder (\"{name}\"). It must be at the top level of the ZIP."
                    : $"\"{name}\" may not be in a package. A package holds template.json and the images in assets/, nothing else.");
            }

            if (handoverPackage)
            {
                problems.Add(null, null, "9", "This is a package iPro sends to the builder (it has handover.json or a catalog folder), " +
                    "not one the builder sends back. Export the site from the builder and upload that file.");
            }
        }
        catch (ExpandedTooFarException)
        {
            problems.Add(null, null, "1, rule 5", "The package unpacks to far more than a 40 MB package can hold. It was not read.");
            return Fail();
        }
        catch (InvalidDataException)
        {
            problems.Add(null, null, "1", "The file is not a ZIP package the importer can read.");
            return Fail();
        }

        if (templateBytes == null)
        {
            if (problems.Count == 0) problems.Add(null, null, "1, rule 1", "The package has no template.json at its top level.");
            return Fail();
        }

        if (templateBytes.Length >= 3 && templateBytes[0] == 0xEF && templateBytes[1] == 0xBB && templateBytes[2] == 0xBF)
        {
            problems.Add(null, null, "1, rule 1", "template.json starts with a byte-order mark. It must be UTF-8 without one.");
            templateBytes = templateBytes[3..];
        }

        string json;
        try
        {
            json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(templateBytes);
        }
        catch (DecoderFallbackException)
        {
            problems.Add(null, null, "1, rule 1", "template.json is not valid UTF-8.");
            return Fail();
        }

        var reader = new TemplateReader(problems, context);
        var template = reader.Read(json);
        if (template == null) return Fail(json);

        // Section 1, rule 7: assets[] and the files in assets/ match exactly, both ways.
        foreach (var asset in reader.DeclaredAssets)
        {
            if (oversize.Contains(asset)) continue;
            if (!files.TryGetValue(asset, out var bytes))
            {
                problems.Add(null, null, "1, rule 7", $"{asset} is listed in assets[] but the file is not in the package.");
                continue;
            }
            if (!BuilderContract.HasImageSignature(bytes, asset))
            {
                problems.Add(null, null, "1, rule 6", $"{asset} is not a real {Path.GetExtension(asset).TrimStart('.').ToUpperInvariant()} image: its contents do not match its name.");
            }
        }
        foreach (var file in files.Keys.Where(f => !reader.DeclaredAssets.Contains(f)).OrderBy(f => f, StringComparer.Ordinal))
        {
            problems.Add(null, null, "1, rule 7", $"{file} is in the package but assets[] does not list it.");
        }

        var used = template.UsedImageRefs.ToHashSet(StringComparer.Ordinal);
        var images = new Dictionary<string, BuilderImage>(StringComparer.Ordinal);
        foreach (var assetRef in used)
        {
            if (!files.TryGetValue(assetRef, out var bytes)) continue;   // already reported above
            var file = assetRef[BuilderContract.AssetFolder.Length..];
            images[assetRef] = new BuilderImage(assetRef, file, BuilderContract.ContentTypeFor(file), Hex(SHA256.HashData(bytes)), bytes);
        }

        var package = new BuilderPackage
        {
            Template = template,
            TemplateJson = json,
            Images = images,
            // An asset nothing references is ignored: not stored, not counted (clarification of 2026-09-29).
            IgnoredAssets = reader.DeclaredAssets.Where(a => !used.Contains(a)).OrderBy(a => a, StringComparer.Ordinal).ToList(),
            Sha256 = sha
        };

        // Section 10, rule 4: the new images fit in the storage the customer has left.
        var needed = package.NewImages(context.KnownImageHashes).Sum(i => i.Length);
        if (needed > context.RemainingBytes)
        {
            problems.Add(null, null, "10, rule 4", $"The package brings {Mb(needed)} MB of new images and this customer has " +
                $"{Mb(Math.Max(0, context.RemainingBytes))} MB of storage left.");
        }

        if (problems.Count == 0) return package;
        return Fail(json);
    }

    // A package's template.json that was stored when the package was taken: read again, against the
    // same rules, when it is looked at or brought in (a rule the site decides -- a reserved address,
    // a hand-over -- can have changed since).
    public static BuilderPackage ReadStored(string templateJson, BuilderImportContext context)
    {
        var problems = new ProblemList();
        var template = new TemplateReader(problems, context).Read(templateJson);
        return new BuilderPackage
        {
            Template = problems.Count == 0 ? template : null,
            TemplateJson = templateJson,
            Problems = problems.ToList()
        };
    }

    public static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    private static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class ExpandedTooFarException : Exception { }

    // The entry's contents, or null when it is larger than cap. The size in the entry's header is not
    // trusted: the bytes are counted as they come out.
    private static byte[]? ReadCapped(ZipArchiveEntry entry, long cap, ref long expanded)
    {
        if (entry.Length > cap) return null;
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            expanded += read;
            if (expanded > BuilderContract.MaxExpandedBytes) throw new ExpandedTooFarException();
            if (total > cap) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private sealed class ProblemList
    {
        private readonly List<ImportProblem> _items = new();
        private int _more;
        public int Count => _items.Count + _more;

        public void Add(string? page, int? block, string rule, string message)
        {
            if (_items.Count >= BuilderContract.MaxProblemsReported) { _more++; return; }
            _items.Add(new ImportProblem(page, block, rule, message));
        }

        public IReadOnlyList<ImportProblem> ToList() => _more == 0
            ? _items.ToList()
            : _items.Append(new ImportProblem(null, null, "7", $"...and {_more} more. Fix the ones above and upload the package again.")).ToList();
    }

    // template.json, read strictly: the keys the contract names and no others, each of the type it
    // names, and null never standing in for an empty string.
    private sealed class TemplateReader
    {
        private static readonly string[] TopKeys = { "contractVersion", "templateName", "theme", "handoverId", "logoRef", "logoAltText", "pages", "assets" };
        private static readonly string[] PageKeys = { "slug", "parentSlug", "title", "navigationLabel", "isHomePage", "sortOrder", "blocks" };
        private static readonly string[] BlockKeys = { "type", "heading", "subheading", "body", "imageRef", "buttonText", "buttonUrl", "layoutVariant", "sortOrder", "isVisible", "settings" };
        private static readonly string[] AssetKeys = { "ref", "type" };
        private static readonly string[] HeroKeys = { "layout", "imagePosition", "textAlignment", "height", "overlayStrength" };

        private readonly ProblemList _problems;
        private readonly BuilderImportContext _context;
        public HashSet<string> DeclaredAssets { get; } = new(StringComparer.Ordinal);

        public TemplateReader(ProblemList problems, BuilderImportContext context)
        {
            _problems = problems;
            _context = context;
        }

        public BuilderTemplate? Read(string json)
        {
            var before = _problems.Count;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            }
            catch (JsonException ex)
            {
                _problems.Add(null, null, "1, rule 1", $"template.json is not valid JSON (line {ex.LineNumber + 1}).");
                return null;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    _problems.Add(null, null, "2", "template.json must be one JSON object.");
                    return null;
                }

                // The version first and alone: a package written for another version is not held to
                // this one's rules.
                var version = root.TryGetProperty("contractVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (version != BuilderContract.Version)
                {
                    _problems.Add(null, null, "0", version == null
                        ? "template.json does not say which contract version it follows (contractVersion)."
                        : $"The package follows contract version \"{version}\". This iPro takes version \"{BuilderContract.Version}\" only.");
                    return null;
                }

                var top = Object(root, TopKeys, null, null, "template.json", "2");

                var templateName = Text(top, "templateName", null, null, "2") ?? string.Empty;
                if (top.ContainsKey("templateName") && (templateName.Length is < 3 or > 60 || !BuilderContract.Slug.IsMatch(templateName)))
                {
                    _problems.Add(null, null, "2", "templateName is 3 to 60 characters: lower-case letters and digits, in words joined by single hyphens.");
                }

                var theme = OneOf(top, "theme", BuilderContract.Themes, null, null, "2") ?? string.Empty;

                var handoverId = Text(top, "handoverId", null, null, "2") ?? string.Empty;
                if (handoverId.Length > 0 && !BuilderContract.HandoverId.IsMatch(handoverId))
                {
                    _problems.Add(null, null, "9.2", "handoverId is either empty or \"h_\" followed by 16 lower-case hexadecimal digits.");
                }
                else if (handoverId != _context.HandoverId)
                {
                    _problems.Add(null, null, "7", _context.HandoverId.Length == 0
                        ? $"This package was made from a site iPro handed to the builder ({handoverId}). iPro has not handed this site over, " +
                          "so it has nothing to check the package against. For now only a site built from scratch in the builder can be brought in."
                        : handoverId.Length == 0
                            ? $"This site was handed to the builder as {_context.HandoverId}, and the package says it was built from scratch (its handoverId is empty)."
                            : $"The package belongs to hand-over {handoverId}. This site was handed over as {_context.HandoverId}.");
                }

                ReadAssets(top);

                var logoRef = Text(top, "logoRef", null, null, "2") ?? string.Empty;
                var logoAltText = Text(top, "logoAltText", null, null, "2") ?? string.Empty;
                if (logoRef.Length > 0 && !DeclaredAssets.Contains(logoRef))
                {
                    _problems.Add(null, null, "2", $"logoRef \"{logoRef}\" is not one of the images listed in assets[].");
                }
                if (logoAltText.Length > BuilderContract.MaxLogoAltText)
                {
                    _problems.Add(null, null, "2", $"logoAltText is {logoAltText.Length} characters. It may be at most {BuilderContract.MaxLogoAltText}.");
                }
                if (logoRef.Length > 0 && string.IsNullOrWhiteSpace(logoAltText))
                {
                    _problems.Add(null, null, "2", "The package has a logo, so logoAltText must describe it (it is what a screen reader says).");
                }
                if (logoRef.Length == 0 && logoAltText.Length > 0)
                {
                    _problems.Add(null, null, "2", "The package has no logo, so logoAltText must be empty.");
                }

                var pages = ReadPages(top, handoverId);

                return _problems.Count == before
                    ? new BuilderTemplate(templateName, theme, handoverId, logoRef, logoAltText, pages)
                    : null;
            }
        }

        private void ReadAssets(Dictionary<string, JsonElement> top)
        {
            if (!Array(top, "assets", null, "2", out var assets)) return;
            var index = 0;
            foreach (var element in assets.EnumerateArray())
            {
                index++;
                var asset = Object(element, AssetKeys, null, null, $"assets[] entry {index}", "2");
                var assetRef = Text(asset, "ref", null, null, "2");
                var type = Text(asset, "type", null, null, "2");
                if (assetRef == null) continue;

                var file = assetRef.StartsWith(BuilderContract.AssetFolder, StringComparison.Ordinal) ? assetRef[BuilderContract.AssetFolder.Length..] : string.Empty;
                if (file.Length == 0 || !BuilderContract.AssetFileName.IsMatch(file) || file.Length > BuilderContract.MaxFileNameLength)
                {
                    _problems.Add(null, null, "1, rule 4", $"assets[] entry {index}: \"{assetRef}\" is not \"assets/\" followed by an image file name " +
                        $"(lower-case letters, digits and hyphens, then .jpg, .jpeg, .png, .gif or .webp; at most {BuilderContract.MaxFileNameLength} characters).");
                    continue;
                }
                if (!DeclaredAssets.Add(assetRef))
                {
                    _problems.Add(null, null, "1, rule 7", $"{assetRef} is listed in assets[] twice.");
                    continue;
                }
                if (type != null && type != BuilderContract.ContentTypeFor(file))
                {
                    _problems.Add(null, null, "2", $"{assetRef}: its type must be \"{BuilderContract.ContentTypeFor(file)}\" (it is \"{type}\").");
                }
            }
        }

        private List<BuilderPage> ReadPages(Dictionary<string, JsonElement> top, string handoverId)
        {
            var pages = new List<BuilderPage>();
            if (!Array(top, "pages", null, "2", out var pageArray)) return pages;
            var count = pageArray.GetArrayLength();
            if (count is < 1 or > BuilderContract.MaxPages)
            {
                _problems.Add(null, null, "2", $"The package has {count} pages. A site has 1 to {BuilderContract.MaxPages}.");
                if (count > BuilderContract.MaxPages) return pages;
            }

            var index = 0;
            foreach (var element in pageArray.EnumerateArray())
            {
                index++;
                // Until its slug is known to be usable, a page is named by its place in the list.
                var label = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("slug", out var s) && s.ValueKind == JsonValueKind.String
                            && BuilderContract.Slug.IsMatch(s.GetString() ?? string.Empty) && s.GetString()!.Length <= BuilderContract.MaxSlug
                    ? s.GetString()!
                    : $"#{index}";
                var page = Object(element, PageKeys, label, null, "The page", "3");

                var slug = Text(page, "slug", label, null, "3") ?? string.Empty;
                if (page.ContainsKey("slug") && label.StartsWith('#'))
                {
                    _problems.Add(label, null, "3", $"The slug \"{Shorten(slug)}\" is not an address a page can have: 1 to {BuilderContract.MaxSlug} characters, " +
                        "lower-case letters and digits, in words joined by single hyphens.");
                }

                string? parentSlug = null;
                if (page.TryGetValue("parentSlug", out var parent))
                {
                    if (parent.ValueKind == JsonValueKind.String) parentSlug = parent.GetString();
                    else if (parent.ValueKind != JsonValueKind.Null)
                    {
                        _problems.Add(label, null, "3", "parentSlug is null for a top-level page, or the slug of the page it sits under.");
                    }
                }

                var title = Text(page, "title", label, null, "3") ?? string.Empty;
                if (page.ContainsKey("title") && (string.IsNullOrWhiteSpace(title) || title.Length > BuilderContract.MaxTitle))
                {
                    _problems.Add(label, null, "3", $"The title is 1 to {BuilderContract.MaxTitle} characters (and not only spaces).");
                }
                var navigationLabel = Text(page, "navigationLabel", label, null, "3") ?? string.Empty;
                if (page.ContainsKey("navigationLabel") && (string.IsNullOrWhiteSpace(navigationLabel) || navigationLabel.Length > BuilderContract.MaxNavigationLabel))
                {
                    _problems.Add(label, null, "3", $"The menu label (navigationLabel) is 1 to {BuilderContract.MaxNavigationLabel} characters (and not only spaces).");
                }

                var isHomePage = YesNo(page, "isHomePage", label, null, "3") ?? false;
                var sortOrder = Whole(page, "sortOrder", label, null, "3", minimum: 0) ?? 0;

                var blocks = new List<BuilderBlock>();
                if (Array(page, "blocks", label, "3", out var blockArray))
                {
                    var blockCount = blockArray.GetArrayLength();
                    if (blockCount is < 1 or > BuilderContract.MaxBlocksPerPage)
                    {
                        _problems.Add(label, null, "3", $"The page has {blockCount} sections. A page has 1 to {BuilderContract.MaxBlocksPerPage}.");
                    }
                    if (blockCount <= BuilderContract.MaxBlocksPerPage)
                    {
                        var position = 0;
                        foreach (var blockElement in blockArray.EnumerateArray()) blocks.Add(ReadBlock(blockElement, label, position++, handoverId));
                    }
                    foreach (var repeated in blocks.GroupBy(b => b.SortOrder).Where(g => g.Count() > 1).Select(g => g.Key))
                    {
                        _problems.Add(label, repeated, "4", $"Two sections of the page have the sortOrder {repeated}. Each section's is its own.");
                    }
                }

                pages.Add(new BuilderPage(slug, parentSlug, title, navigationLabel, isHomePage, sortOrder, blocks));
            }

            CheckPageTree(pages);
            return pages;
        }

        // Section 3: addresses, the home page and the tree the pages form.
        private void CheckPageTree(List<BuilderPage> pages)
        {
            foreach (var repeated in pages.Where(p => p.Slug.Length > 0).GroupBy(p => p.Slug).Where(g => g.Count() > 1).Select(g => g.Key))
            {
                _problems.Add(repeated, null, "3", $"More than one page has the slug \"{repeated}\". A slug is used once in the whole site.");
            }
            foreach (var page in pages.Where(p => BuilderContract.ReservedSlugs.Contains(p.Slug, StringComparer.Ordinal)))
            {
                _problems.Add(page.Slug, null, "3", $"\"{page.Slug}\" is an address iPro keeps for itself, so no page can have it. Give the page another slug.");
            }
            foreach (var page in pages.Where(p => p.Slug == BuilderContract.HomeSlug && !p.IsHomePage))
            {
                _problems.Add(page.Slug, null, "3", "The address \"home\" belongs to the home page. Give this page another slug, or make it the home page.");
            }

            var homes = pages.Count(p => p.IsHomePage);
            if (pages.Count > 0 && homes != 1)
            {
                _problems.Add(null, null, "3", homes == 0
                    ? "No page is marked as the home page. Exactly one page has isHomePage true."
                    : $"{homes} pages are marked as the home page. Exactly one page has isHomePage true.");
            }
            foreach (var home in pages.Where(p => p.IsHomePage && p.ParentSlug != null))
            {
                _problems.Add(home.Slug, null, "3", "The home page is a top-level page: its parentSlug must be null.");
            }

            var bySlug = pages.Where(p => p.Slug.Length > 0).GroupBy(p => p.Slug).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var page in pages.Where(p => p.ParentSlug != null))
            {
                if (page.ParentSlug == page.Slug)
                {
                    _problems.Add(page.Slug, null, "3", "The page names itself as its parent.");
                }
                else if (!bySlug.ContainsKey(page.ParentSlug!))
                {
                    _problems.Add(page.Slug, null, "3", $"The page sits under \"{Shorten(page.ParentSlug!)}\", and the package has no page with that slug.");
                }
            }
            foreach (var page in pages)
            {
                var depth = 1;
                var seen = new HashSet<string>(StringComparer.Ordinal) { page.Slug };
                var current = page;
                while (current.ParentSlug != null && bySlug.TryGetValue(current.ParentSlug, out var above))
                {
                    if (!seen.Add(above.Slug))
                    {
                        _problems.Add(page.Slug, null, "3", "The page's parents form a circle: following parentSlug leads back to the same page.");
                        depth = 0;
                        break;
                    }
                    depth++;
                    current = above;
                }
                if (depth > BuilderContract.MaxDepth)
                {
                    _problems.Add(page.Slug, null, "3", $"The page is {depth} levels deep. A menu goes at most {BuilderContract.MaxDepth} levels (page, sub-page, sub-sub-page).");
                }
            }
            foreach (var group in pages.GroupBy(p => p.ParentSlug ?? string.Empty))
            {
                foreach (var repeated in group.GroupBy(p => p.SortOrder).Where(g => g.Count() > 1))
                {
                    _problems.Add(repeated.First().Slug, null, "3",
                        $"The pages {string.Join(", ", repeated.Select(p => "\"" + p.Slug + "\""))} share the sortOrder {repeated.Key}. Pages side by side in the menu each have their own.");
                }
            }
        }

        private BuilderBlock ReadBlock(JsonElement element, string page, int position, string handoverId)
        {
            // A block is named by its sortOrder (contract section 11.5), or by its place in the page
            // while that is not a usable number.
            var number = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("sortOrder", out var so)
                         && so.ValueKind == JsonValueKind.Number && so.TryGetInt32(out var parsed) && parsed >= 0
                ? parsed : position;
            var block = Object(element, BlockKeys, page, number, "The section", "4");

            var type = Text(block, "type", page, number, "4") ?? string.Empty;
            var known = BuilderContract.BlockTypes.Contains(type, StringComparer.Ordinal);
            if (block.ContainsKey("type") && !known)
            {
                _problems.Add(page, number, "4", $"\"{Shorten(type)}\" is not a section type the builder may send. The eight are: {string.Join(", ", BuilderContract.BlockTypes)}.");
            }
            else if (known && !_context.AllowedBlockTypes.Contains(type))
            {
                _problems.Add(page, number, "4", $"A {type} section is outside this customer's package (the hand-over did not offer it).");
            }
            else if (known && handoverId.Length == 0 && BuilderContract.HandoverOnlyTypes.Contains(type, StringComparer.Ordinal))
            {
                _problems.Add(page, number, type == WebsiteBlockTypes.Form ? "4.9" : "4.7",
                    $"A {type} section points at one customer's own {(type == WebsiteBlockTypes.Form ? "form" : "article")}, so it exists only in a site iPro handed to the builder. " +
                    "Leave it out of the package and add it in iPro's page editor.");
            }

            var heading = Capped(block, "heading", BuilderContract.MaxHeading, page, number);
            var subheading = Capped(block, "subheading", BuilderContract.MaxSubheading, page, number);
            var body = Capped(block, "body", BuilderContract.MaxBody, page, number);
            var buttonText = Capped(block, "buttonText", BuilderContract.MaxButtonText, page, number);
            var buttonUrl = Capped(block, "buttonUrl", BuilderContract.MaxButtonUrl, page, number);
            var imageRef = Text(block, "imageRef", page, number, "4") ?? string.Empty;
            var layoutVariant = Text(block, "layoutVariant", page, number, "4") ?? string.Empty;
            var sortOrder = Whole(block, "sortOrder", page, number, "4", minimum: 0) ?? number;
            var isVisible = YesNo(block, "isVisible", page, number, "4") ?? true;

            if (imageRef.Length > 0 && !DeclaredAssets.Contains(imageRef))
            {
                _problems.Add(page, number, "10, rule 1", $"imageRef \"{Shorten(imageRef)}\" is not one of the images listed in assets[].");
            }
            if (!BuilderContract.IsAllowedButtonUrl(buttonUrl))
            {
                _problems.Add(page, number, "4", $"buttonUrl \"{Shorten(buttonUrl)}\" is not a link a button may carry: a page of this site (/contact), " +
                    "an https:// address, mailto: or tel:.");
            }

            var settingsJson = "{}";
            int articleId = 0, websiteFormId = 0;
            if (known)
            {
                var variants = WebsiteBlockLayoutVariants.AllowedFor(type);
                if (layoutVariant.Length > 0 && !variants.Contains(layoutVariant, StringComparer.Ordinal))
                {
                    _problems.Add(page, number, "4", variants.Length == 0
                        ? $"A {type} section has no layoutVariant: it must be empty (it is \"{Shorten(layoutVariant)}\")."
                        : $"layoutVariant \"{Shorten(layoutVariant)}\" is not one a {type} section has: empty, or {string.Join(", ", variants)}.");
                }

                var values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["heading"] = heading, ["subheading"] = subheading, ["body"] = body,
                    ["imageRef"] = imageRef, ["buttonText"] = buttonText, ["buttonUrl"] = buttonUrl
                };
                foreach (var unused in BuilderContract.UnusedFields(type).Where(f => values[f].Length > 0))
                {
                    _problems.Add(page, number, unused == "imageRef" ? "10, rule 7" : "4",
                        $"A {type} section does not use {unused}. It must be empty.");
                }

                switch (type)
                {
                    case WebsiteBlockTypes.Hero:
                        if (string.IsNullOrWhiteSpace(heading)) _problems.Add(page, number, "4.1", "A Hero section needs a heading.");
                        break;
                    case WebsiteBlockTypes.Text:
                        if (string.IsNullOrWhiteSpace(heading) && string.IsNullOrWhiteSpace(body))
                        {
                            _problems.Add(page, number, "4.2", "A Text section needs a heading or a body.");
                        }
                        if ((buttonText.Length == 0) != (buttonUrl.Length == 0))
                        {
                            _problems.Add(page, number, "4.2", "A Text section's button has both its words (buttonText) and its link (buttonUrl), or neither.");
                        }
                        break;
                    case WebsiteBlockTypes.Services:
                        if (string.IsNullOrWhiteSpace(body)) _problems.Add(page, number, "4.3", "A Services section needs a body: one service per line.");
                        break;
                    case WebsiteBlockTypes.CallToAction:
                        if (string.IsNullOrWhiteSpace(heading)) _problems.Add(page, number, "4.4", "A CallToAction section needs a heading.");
                        break;
                }

                if (block.TryGetValue("settings", out var settings))
                {
                    if (settings.ValueKind != JsonValueKind.Object)
                    {
                        _problems.Add(page, number, "4", "settings must be an object ({} when the section has none).");
                    }
                    else
                    {
                        (settingsJson, articleId, websiteFormId) = ReadSettings(type, settings, page, number);
                    }
                }
            }

            return new BuilderBlock(type, heading, subheading, body, imageRef, buttonText, buttonUrl, layoutVariant,
                sortOrder, isVisible, settingsJson, articleId, websiteFormId);
        }

        // Each type's settings, exactly the keys the contract names, turned into the settings iPro stores.
        private (string Json, int ArticleId, int WebsiteFormId) ReadSettings(string type, JsonElement element, string page, int number)
        {
            switch (type)
            {
                case WebsiteBlockTypes.Hero:
                {
                    var s = Object(element, HeroKeys, page, number, "A Hero section's settings", "4.1");
                    var hero = new WebsiteHeroSettings
                    {
                        Layout = OneOf(s, "layout", BuilderContract.HeroLayouts, page, number, "4.1") ?? "split",
                        ImagePosition = OneOf(s, "imagePosition", BuilderContract.HeroImagePositions, page, number, "4.1") ?? "center",
                        TextAlignment = OneOf(s, "textAlignment", BuilderContract.HeroTextAlignments, page, number, "4.1") ?? "left",
                        Height = OneOf(s, "height", BuilderContract.HeroHeights, page, number, "4.1") ?? "standard",
                        OverlayStrength = Whole(s, "overlayStrength", page, number, "4.1", minimum: 0, maximum: BuilderContract.HeroOverlayMax) ?? 45
                    };
                    return (hero.ToJson(), 0, 0);
                }
                case WebsiteBlockTypes.ContactForm:
                {
                    var s = Object(element, new[] { "showPhoto" }, page, number, "A ContactForm section's settings", "4.5");
                    return (new WebsiteContactFormSettings { ShowPhoto = YesNo(s, "showPhoto", page, number, "4.5") ?? true }.ToJson(), 0, 0);
                }
                case WebsiteBlockTypes.ArticleContent:
                {
                    var s = Object(element, new[] { "articleId" }, page, number, "An ArticleContent section's settings", "4.7");
                    var id = Whole(s, "articleId", page, number, "4.7", minimum: 1) ?? 0;
                    if (id > 0 && _context.HandoverId.Length > 0 && !_context.ArticleIds.Contains(id))
                    {
                        _problems.Add(page, number, "7", $"Article {id} is not one of this customer's published articles.");
                    }
                    return (new WebsiteArticleContentSettings { ArticleId = id }.ToJson(), id, 0);
                }
                case WebsiteBlockTypes.Blog:
                {
                    var s = Object(element, new[] { "postCount", "showImages" }, page, number, "A Blog section's settings", "4.8");
                    var blog = new WebsiteBlogSettings
                    {
                        PostCount = Whole(s, "postCount", page, number, "4.8", WebsiteBlogSettings.MinPostCount, WebsiteBlogSettings.MaxPostCount) ?? 6,
                        ShowImages = YesNo(s, "showImages", page, number, "4.8") ?? true
                    };
                    return (blog.ToJson(), 0, 0);
                }
                case WebsiteBlockTypes.Form:
                {
                    var s = Object(element, new[] { "websiteFormId" }, page, number, "A Form section's settings", "4.9");
                    var id = Whole(s, "websiteFormId", page, number, "4.9", minimum: 1) ?? 0;
                    if (id > 0 && _context.HandoverId.Length > 0 && !_context.FormIds.Contains(id))
                    {
                        _problems.Add(page, number, "7", $"Form {id} is not one of this customer's active forms.");
                    }
                    return (new WebsiteFormSettings { WebsiteFormId = id }.ToJson(), 0, id);
                }
                default:
                    Object(element, System.Array.Empty<string>(), page, number, $"A {type} section's settings", "4");
                    return ("{}", 0, 0);
            }
        }

        // ---- strict JSON reading ----

        // The object's members by key. Reports every key the contract does not have, every one it has
        // and the object lacks, and a key written twice; what is returned holds only the known keys.
        private Dictionary<string, JsonElement> Object(JsonElement element, string[] keys, string? page, int? block, string what, string rule)
        {
            var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (element.ValueKind != JsonValueKind.Object)
            {
                _problems.Add(page, block, rule, $"{what} must be a JSON object.");
                return members;
            }
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Contains(property.Name, StringComparer.Ordinal))
                {
                    _problems.Add(page, block, rule, $"{what} has a key the contract does not have: \"{Shorten(property.Name)}\".");
                }
                else if (!members.TryAdd(property.Name, property.Value))
                {
                    _problems.Add(page, block, rule, $"{what} has the key \"{property.Name}\" twice.");
                }
            }
            foreach (var missing in keys.Where(k => !members.ContainsKey(k)))
            {
                _problems.Add(page, block, rule, $"{what} is missing \"{missing}\". Every key is always written, empty when unused.");
            }
            return members;
        }

        private bool Array(Dictionary<string, JsonElement> members, string key, string? page, string rule, out JsonElement array)
        {
            array = default;
            if (!members.TryGetValue(key, out var value)) return false;   // its absence is already reported
            if (value.ValueKind != JsonValueKind.Array)
            {
                _problems.Add(page, null, rule, $"\"{key}\" must be a list.");
                return false;
            }
            array = value;
            return true;
        }

        private string? Text(Dictionary<string, JsonElement> members, string key, string? page, int? block, string rule)
        {
            if (!members.TryGetValue(key, out var value)) return null;
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
            _problems.Add(page, block, rule, value.ValueKind == JsonValueKind.Null
                ? $"\"{key}\" is null. An empty field is written \"\", never null."
                : $"\"{key}\" must be text.");
            return null;
        }

        private string Capped(Dictionary<string, JsonElement> members, string key, int cap, string page, int block)
        {
            var value = Text(members, key, page, block, "4") ?? string.Empty;
            // Every limit counts UTF-16 code units: string.Length here, .length in JavaScript.
            if (value.Length > cap) _problems.Add(page, block, "4", $"{key} is {value.Length} characters. It may be at most {cap}.");
            return value;
        }

        private string? OneOf(Dictionary<string, JsonElement> members, string key, string[] allowed, string? page, int? block, string rule)
        {
            var value = Text(members, key, page, block, rule);
            if (value == null) return null;
            if (allowed.Contains(value, StringComparer.Ordinal)) return value;
            _problems.Add(page, block, rule, $"\"{key}\" is \"{Shorten(value)}\". It is one of: {string.Join(", ", allowed)}.");
            return null;
        }

        private int? Whole(Dictionary<string, JsonElement> members, string key, string? page, int? block, string rule, int minimum, int maximum = int.MaxValue)
        {
            if (!members.TryGetValue(key, out var value)) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            {
                _problems.Add(page, block, rule, $"\"{key}\" must be a whole number.");
                return null;
            }
            if (number < minimum || number > maximum)
            {
                _problems.Add(page, block, rule, maximum == int.MaxValue
                    ? $"\"{key}\" is {number}. It must be {minimum} or more."
                    : $"\"{key}\" is {number}. It must be from {minimum} to {maximum}.");
                return null;
            }
            return number;
        }

        private bool? YesNo(Dictionary<string, JsonElement> members, string key, string? page, int? block, string rule)
        {
            if (!members.TryGetValue(key, out var value)) return null;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            _problems.Add(page, block, rule, $"\"{key}\" must be true or false.");
            return null;
        }

        // A value quoted in a message: short, and on one line.
        private static string Shorten(string value)
        {
            var oneLine = value.Replace('\r', ' ').Replace('\n', ' ');
            return oneLine.Length <= 60 ? oneLine : oneLine[..60] + "...";
        }
    }
}
