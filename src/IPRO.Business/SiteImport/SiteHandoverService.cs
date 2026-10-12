using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.SiteImport;

// 572: the reading and record keeping around SiteHandoverBuilder. Reads a customer's site, articles
// and forms, fetches the site's pictures from storage, and -- when a package is actually handed out --
// records the hand-over so the package that comes back can be checked against it.
public static class SiteHandoverService
{
    // The public containers a website's pictures live in. A page's image address is text a customer
    // can type, so the hand-over reads a picture only from these: never from the private containers
    // (documents, portal files) that share the storage account.
    private static readonly string[] ImageContainers = { "website-media", "agent-logos", "article-media", "starter-content", "agent-photos" };

    // The section types this customer's package includes: the five every site has, and the three that
    // follow a paid feature (the same three gates the page editor's "add section" applies).
    public static IReadOnlySet<string> AllowedBlockTypes(bool articles, bool blog, bool customForms)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            WebsiteBlockTypes.Hero, WebsiteBlockTypes.Text, WebsiteBlockTypes.Services, WebsiteBlockTypes.CallToAction, WebsiteBlockTypes.ContactForm
        };
        if (articles) allowed.Add(WebsiteBlockTypes.ArticleContent);
        if (blog) allowed.Add(WebsiteBlockTypes.Blog);
        if (customForms) allowed.Add(WebsiteBlockTypes.Form);
        return allowed;
    }

    // The package for this site as it is now, or the reasons it cannot be handed over. Nothing is
    // written: call RecordAsync when the package is given out.
    public static async Task<SiteHandover> BuildAsync(
        IPRODbContext db, IBlobStorageService blob, AgentWebsite site, IReadOnlySet<string> allowedBlockTypes, long storageLimitBytes)
    {
        var agent = site.AgentUser ?? await db.AgentUsers.AsNoTracking().FirstAsync(a => a.Id == site.AgentUserId);
        var template = site.Template ?? await db.WebsiteTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == site.TemplateId);
        var pages = await db.WebsitePages.AsNoTracking()
            .Where(p => p.AgentWebsiteId == site.Id)
            .Include(p => p.Blocks)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Title)
            .ToListAsync();

        var articles = allowedBlockTypes.Contains(WebsiteBlockTypes.ArticleContent) || allowedBlockTypes.Contains(WebsiteBlockTypes.Blog)
            ? await db.Articles.AsNoTracking()
                .Where(a => a.AgentUserId == site.AgentUserId && a.IsPublished)
                .OrderByDescending(a => a.PublishedAt ?? a.CreatedAt).ThenByDescending(a => a.Id)
                .Select(a => new Article { Id = a.Id, Title = a.Title, Summary = a.Summary, ImageUrl = a.ImageUrl, PublishedAt = a.PublishedAt, CreatedAt = a.CreatedAt })
                .ToListAsync()
            : new List<Article>();

        var forms = new List<HandoverForm>();
        if (allowedBlockTypes.Contains(WebsiteBlockTypes.Form))
        {
            var rows = await db.WebsiteForms.AsNoTracking().Where(f => f.AgentUserId == site.AgentUserId && f.IsActive).OrderBy(f => f.Title).ToListAsync();
            var formIds = rows.Select(f => f.Id).ToList();
            var fields = await db.WebsiteFormFields.AsNoTracking().Where(x => formIds.Contains(x.WebsiteFormId)).ToListAsync();
            var fieldIds = fields.Select(x => x.Id).ToList();
            var options = (await db.WebsiteFormFieldOptions.AsNoTracking().Where(o => fieldIds.Contains(o.WebsiteFormFieldId)).ToListAsync())
                .GroupBy(o => o.WebsiteFormFieldId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.OrderBy(o => o.SortOrder).ThenBy(o => o.Id).Select(o => o.Text).ToList());
            forms = rows.Select(f => new HandoverForm(f, fields.Where(x => x.WebsiteFormId == f.Id).ToList(), options)).ToList();
        }

        var remaining = storageLimitBytes <= 0
            ? long.MaxValue
            : Math.Max(0, storageLimitBytes - await AgentStorage.TotalBytesAsync(db, site.AgentUserId));
        var businessName = !string.IsNullOrWhiteSpace(agent.CompanyName) ? agent.CompanyName
            : !string.IsNullOrWhiteSpace(site.SiteTitle) ? site.SiteTitle
            : $"{agent.FirstName} {agent.LastName}";

        var input = new SiteHandoverBuilder.Input(site, businessName, agent.BusinessType ?? string.Empty, template?.TemplateKey ?? string.Empty,
            pages, allowedBlockTypes, articles, forms, remaining, DateTime.UtcNow);
        return await SiteHandoverBuilder.BuildAsync(input, url => FetchImageAsync(blob, url));
    }

    // The hand-over is on record from the moment its package is given out. Each delivered picture
    // that is a row of the site's media library gets its content hash, which is how the returning
    // package's unchanged pictures are recognized.
    public static async Task<WebsiteBuilderHandover> RecordAsync(IPRODbContext db, AgentWebsite site, SiteHandover handover, string issuedBy)
    {
        if (!handover.CanHandOver) throw new InvalidOperationException("Only a package that was made is recorded.");

        var byUrl = handover.Delivered.GroupBy(d => d.Url).ToDictionary(g => g.Key, g => g.First().Sha256, StringComparer.Ordinal);
        var urls = byUrl.Keys.ToList();
        var assets = await db.WebsiteMediaAssets.Where(a => a.AgentWebsiteId == site.Id && urls.Contains(a.BlobUrl)).ToListAsync();
        foreach (var asset in assets.Where(a => string.IsNullOrEmpty(a.ContentSha256))) asset.ContentSha256 = byUrl[asset.BlobUrl];

        var row = new WebsiteBuilderHandover
        {
            AgentUserId = site.AgentUserId,
            AgentWebsiteId = site.Id,
            HandoverId = handover.HandoverId,
            IssuedAt = handover.IssuedAt,
            SessionExpiresAt = handover.SessionExpiresAt,
            SessionTokenSha256 = BuilderPackageReader.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(handover.SessionToken))),
            IssuedBy = issuedBy.Length <= 100 ? issuedBy : issuedBy[..100],
            TemplateName = handover.TemplateName,
            PackageSha256 = BuilderPackageReader.Hex(SHA256.HashData(handover.Zip)),
            PageCount = handover.Pages.Count,
            ImageCount = handover.Delivered.Count,
            AllowedBlockTypesJson = JsonSerializer.Serialize(handover.AllowedBlockTypes),
            ArticleIdsJson = JsonSerializer.Serialize(handover.ArticleIds),
            FormIdsJson = JsonSerializer.Serialize(handover.FormIds),
            PagesJson = JsonSerializer.Serialize(handover.Pages),
            DeliveredJson = JsonSerializer.Serialize(handover.Delivered),
            NotesJson = JsonSerializer.Serialize(handover.Notes)
        };
        db.WebsiteBuilderHandovers.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    public static Task<WebsiteBuilderHandover?> FindAsync(IPRODbContext db, int websiteId, string handoverId) =>
        db.WebsiteBuilderHandovers.AsNoTracking().FirstOrDefaultAsync(h => h.AgentWebsiteId == websiteId && h.HandoverId == handoverId);

    // The newest hand-over of a site: the only one whose package may come back, because every later
    // hand-over carries a later state of the site.
    public static Task<WebsiteBuilderHandover?> LatestAsync(IPRODbContext db, int websiteId) =>
        db.WebsiteBuilderHandovers.AsNoTracking()
            .Where(h => h.AgentWebsiteId == websiteId)
            .OrderByDescending(h => h.IssuedAt).ThenByDescending(h => h.Id)
            .FirstOrDefaultAsync();

    public static IReadOnlyList<T> List<T>(string json)
    {
        try { return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>(); }
        catch (JsonException) { return new List<T>(); }
    }

    // A picture's bytes, only when its address is a file in one of the public image containers.
    // Null for anything else: an address on another site, a path on this one, a private container.
    public static async Task<byte[]?> FetchImageAsync(IBlobStorageService blob, string url)
    {
        if (!IsSiteImageAddress(url)) return null;
        try
        {
            await using var stream = await blob.DownloadAsync(url);
            if (stream == null) return null;
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                // One byte over the limit is enough to know it is too large; the builder says so.
                if (buffer.Length + read > BuilderContract.MaxAssetBytes + 1) { buffer.Write(chunk, 0, 1); break; }
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or Azure.RequestFailedException)
        {
            return null;
        }
    }

    // "https://<account>.blob.core.windows.net/<container>/<name>", or the local emulator's
    // "http://127.0.0.1:10000/<account>/<container>/<name>": the container is the first or the second
    // part of the path. No part may climb ("..") and nothing may still be percent-encoded after one
    // decode (the trick that walked past the media proxy's list in August).
    public static bool IsSiteImageAddress(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.Contains('%') || path.Contains('\\')) return false;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is ".." or ".")) return false;
        // On the real storage host the container is the first part. Only the local emulator puts the
        // account name before it; anywhere else a second-place match would be a private container
        // with a public one's name inside the file name.
        var at = uri.IsLoopback && parts.Length > 1 && !ImageContainers.Contains(parts[0], StringComparer.Ordinal) ? 1 : 0;
        return parts.Length > at + 1 && ImageContainers.Contains(parts[at], StringComparer.Ordinal);
    }
}
