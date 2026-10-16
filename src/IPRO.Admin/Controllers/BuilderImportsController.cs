using System.Security.Claims;
using IPRO.Admin.Infrastructure;
using IPRO.Admin.Models;
using IPRO.Business.Interfaces;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

// 571: bringing a customer's site in from the Template Builder (stage 1: the owner or his designer
// builds the site in the builder on his own machine and the package comes in here).
//
// Four steps, and the site changes only at the third:
//   Upload   the package is checked against the export contract; a refusal lists every reason.
//   Review   what it would do to the site, page by page, with a look at each page as it would be.
//   Apply    the site's pages become the package's; the site as it was is kept.
//   Restore  puts the site back as it was.
//
// 572: and the way out. Handover makes the package of a customer's site that the builder opens; the
// package that comes back from it is held to that hand-over.
//
// SuperAdmin only: every one of these reads or replaces a customer's content.
[Authorize(Policy = "SuperAdmin")]
public class BuilderImportsController : Controller
{
    // The contract's 40 MB and a little room for the form around the file.
    private const long UploadLimit = BuilderContract.MaxZipBytes + 1024 * 1024;

    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IAdminAuditLogService _auditLog;
    private readonly IConfiguration _configuration;
    // Resolved inside the action, never in the constructor: see AgentsController on why the blob
    // service must not be a constructor dependency in this app.
    private readonly IServiceProvider _services;
    private readonly ILogger<BuilderImportsController> _logger;

    public BuilderImportsController(IPRODbContext db, IPackageEntitlementService entitlements, IAdminAuditLogService auditLog,
        IConfiguration configuration, IServiceProvider services, ILogger<BuilderImportsController>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BuilderImportsController>.Instance;
        _db = db;
        _entitlements = entitlements;
        _auditLog = auditLog;
        _configuration = configuration;
        _services = services;
    }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public async Task<IActionResult> Index(int agentId)
    {
        var model = await IndexModelAsync(agentId);
        return model == null ? NotFound() : View(model);
    }

    // 572: the package of this site as it is now, for the builder to open. A site that does not fit
    // the contract is not handed over; the reasons are listed instead.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Handover(int agentId)
    {
        var site = await _db.AgentWebsites.Include(w => w.AgentUser).Include(w => w.Template).FirstOrDefaultAsync(w => w.AgentUserId == agentId);
        if (site == null) return NotFound();

        var allowed = SiteHandoverService.AllowedBlockTypes(
            articles: (await _entitlements.GetAccessAsync(agentId, PackageFeatureCodes.Newsletters)).IsIncluded,
            blog: (await _entitlements.GetAccessAsync(agentId, PackageFeatureCodes.ManagedBlog)).IsIncluded,
            customForms: (await _entitlements.GetAccessAsync(agentId, PackageFeatureCodes.CustomForms)).IsIncluded);
        var capacity = await _entitlements.GetAccessAsync(agentId, PackageFeatureCodes.FileUploadCapacity);
        var blob = Storage();
        if (blob == null) return RedirectToAction(nameof(Index), new { agentId });
        var handover = await SiteHandoverService.BuildAsync(_db, blob, site, allowed, AgentStorage.LimitBytes(capacity.LimitValue), ReadStarterBannerAsync);
        if (!handover.CanHandOver)
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderHandoverRefused",
                Short($"Agent id {agentId}'s website was not handed to the Template Builder: {handover.Blockers.Count} reason(s); the first: {handover.Blockers[0]}"));
            var model = await IndexModelAsync(agentId, handoverBlockers: handover.Blockers);
            return model == null ? NotFound() : View(nameof(Index), model);
        }

        var record = await SiteHandoverService.RecordAsync(_db, site, handover, CurrentAdminUsername);
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderHandoverMade",
            Short($"Handed agent id {agentId}'s website to the Template Builder as {record.HandoverId}: {record.PageCount} page(s), {record.ImageCount} picture(s)" +
                  $"{(handover.HostSections.Count > 0 ? $", {handover.HostSections.Count} section(s) as locked boxes" : string.Empty)}. " +
                  "Only a package made from this hand-over can come back."));
        return File(handover.Zip, "application/zip", handover.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(UploadLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadLimit)]
    public async Task<IActionResult> Upload(int agentId, IFormFile? package)
    {
        var site = await _db.AgentWebsites.Include(w => w.AgentUser).FirstOrDefaultAsync(w => w.AgentUserId == agentId);
        if (site == null) return NotFound();
        if (package == null || package.Length == 0)
        {
            TempData["Error"] = "Choose the package the builder exported (a .zip file).";
            return RedirectToAction(nameof(Index), new { agentId });
        }

        BuilderPackage read;
        if (package.Length > BuilderContract.MaxZipBytes)
        {
            read = new BuilderPackage
            {
                Problems = new[] { new ImportProblem(null, null, "1, rule 5", $"The package is {package.Length / 1024.0 / 1024.0:0.0} MB. A package may be at most 40 MB.") }
            };
        }
        else
        {
            using var buffer = new MemoryStream();
            await package.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            var capacity = await _entitlements.GetAccessAsync(agentId, PackageFeatureCodes.FileUploadCapacity);
            // A package that answers a hand-over is held to that hand-over; one built from scratch is not.
            var context = await BuilderImportService.ContextAsync(_db, site, AgentStorage.LimitBytes(capacity.LimitValue), BuilderPackageReader.PeekHandoverId(bytes));
            read = BuilderPackageReader.Read(bytes, context);
        }

        var fileName = Path.GetFileName(package.FileName);
        if (fileName.Length > 120) fileName = fileName[..120];
        if (!read.IsValid)
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderImportRefused",
                Short($"Template Builder package \"{fileName}\" for agent id {agentId} was not taken: {read.Problems.Count} problem(s); the first: {read.Problems[0].Message}"));
            var model = await IndexModelAsync(agentId, read.Problems, fileName);
            return model == null ? NotFound() : View(nameof(Index), model);
        }

        var blob = Storage();
        if (blob == null) return RedirectToAction(nameof(Index), new { agentId });
        WebsiteBuilderImport import;
        try
        {
            import = await BuilderImportService.StoreAsync(_db, blob, site, read, fileName, CurrentAdminUsername);
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or IOException or InvalidOperationException or DbUpdateException)
        {
            // Nothing is half-stored: StoreAsync takes back what it uploaded when the record is not written.
            _logger.LogError(ex, "Template Builder package for agent {AgentId} could not be stored", agentId);
            TempData["Error"] = "The package passed every rule, but its pictures could not be stored (file storage did not answer). Nothing was kept and the site is unchanged. Try again in a minute.";
            return RedirectToAction(nameof(Index), new { agentId });
        }
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderImportStored",
            Short($"Template Builder package \"{fileName}\" for agent id {agentId} passed the contract and is waiting (import id {import.Id}; " +
                  $"{import.PageCount} page(s), {import.NewImageCount} new image(s){(import.HandoverId.Length > 0 ? $"; the return of hand-over {import.HandoverId}" : string.Empty)}). The site is unchanged."));
        TempData["Success"] = "The package passed every rule. Nothing on the site has changed yet: review it below, then bring it in.";
        return RedirectToAction(nameof(Review), new { id = import.Id });
    }

    // without: the ids of the pages to remove; keep: the ids of pages removed in the builder that
    // are to stay after all. Both comma-separated ("12,15"), as the look's address carries them.
    // sections (576): the rows of the sections of replaced pages to keep ("812,815").
    // moved (581): which page of the site each package page at a new address is ("about-us:12,team:0").
    public async Task<IActionResult> Review(int id, int? design, string? without, string? keep = null, string? sections = null, string? moved = null)
    {
        var import = await _db.WebsiteBuilderImports.FirstOrDefaultAsync(i => i.Id == id);
        if (import == null) return NotFound();
        if (import.Status != WebsiteBuilderImportStatus.Pending) return RedirectToAction(nameof(Index), new { agentId = import.AgentUserId });

        var removed = BuilderImportService.ParseIds(without);
        var kept = BuilderImportService.ParseIds(keep);
        var keptSections = BuilderImportService.ParseIds(sections);
        var moves = BuilderImportService.ParseMoves(moved);
        var planned = await BuilderImportService.PlanAsync(_db, import, removed, kept, keptSections, moves);
        if (planned == null) return NotFound();

        var designs = await _db.WebsiteTemplates.AsNoTracking().Where(t => t.IsActive || t.Id == planned.Site.TemplateId).OrderBy(t => t.Name).ToListAsync();
        if (design.HasValue && (design == planned.Site.TemplateId || designs.All(d => d.Id != design))) design = null;

        // The look opens on the portal, where a SuperAdmin is not signed in: the link carries the key.
        var key = await BuilderImportService.LookKeyAsync(_db, import);
        var look = $"{PortalBaseUrl()}/PublicWebsite/PreviewBuilderImport?id={import.Id}&key={key}";
        if (design.HasValue) look += $"&design={design}";
        if (removed.Count > 0) look += "&without=" + string.Join(",", removed.OrderBy(x => x));
        if (kept.Count > 0) look += "&keep=" + string.Join(",", kept.OrderBy(x => x));
        if (keptSections.Count > 0) look += "&sections=" + string.Join(",", keptSections.OrderBy(x => x));
        if (moves.Count > 0) look += "&moved=" + Uri.EscapeDataString(BuilderImportService.JoinMoves(moves)!);

        return View(new BuilderImportReviewViewModel
        {
            Agent = planned.Site.AgentUser,
            Website = planned.Site,
            Import = import,
            Package = planned.Package,
            Plan = planned.Plan,
            Handover = planned.Handover,
            Images = BuilderImportService.StoredImages(import),
            Designs = designs,
            Design = design,
            Without = removed,
            Keep = kept,
            KeepSections = keptSections,
            Moved = moves,
            LookAddress = look + "&page=",
            Zone = AdminClock.Zone(_configuration)
        });
    }

    // The review screen's choices (the design, the pages to remove or keep) are a form; this turns
    // them into the review's own address so the looks and the "bring it in" button all read the same
    // choices.
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Choose(int id, int? design, int[]? without, int[]? keep = null, int[]? sections = null, string[]? moved = null) =>
        RedirectToAction(nameof(Review), new { id, design, without = Joined(without), keep = Joined(keep), sections = Joined(sections), moved = JoinedMoves(moved) });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(int id, int? design, int[]? without, int[]? keep = null, string? plan = null, int[]? sections = null, string[]? moved = null)
    {
        var import = await _db.WebsiteBuilderImports.FirstOrDefaultAsync(i => i.Id == id);
        if (import == null) return NotFound();
        var site = await _db.AgentWebsites.AsNoTracking().FirstOrDefaultAsync(w => w.Id == import.AgentWebsiteId);
        if (site == null) return NotFound();

        var removed = (without ?? Array.Empty<int>()).ToHashSet();
        var kept = (keep ?? Array.Empty<int>()).ToHashSet();
        var keptSections = (sections ?? Array.Empty<int>()).Where(x => x > 0).ToHashSet();
        var moves = BuilderImportService.ParseMoves(moved);
        int? switchTo = design.HasValue && design != site.TemplateId ? design : null;
        BuilderImportService.ApplyResult result;
        try
        {
            // plan: what the review screen showed, posted back with the button (574).
            result = await BuilderImportService.ApplyAsync(_db, import, removed, switchTo, CurrentAdminUsername, kept, plan ?? string.Empty, keptSections, moves);
        }
        catch (DbUpdateException ex)
        {
            // The database refused a row (two things written to one site at the same moment, an address
            // taken meanwhile). The transaction is gone with it: nothing was changed.
            _logger.LogWarning(ex, "Template Builder import {ImportId} was not written", id);
            _db.ChangeTracker.Clear();
            result = BuilderImportService.ApplyResult.Failed("The site was being changed at the same moment, so nothing was written. Read the review again.");
        }
        if (!result.Ok)
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderImportNotApplied",
                Short($"Template Builder package \"{import.FileName}\" (import id {id}) was NOT brought into agent id {import.AgentUserId}'s website: {result.Problems.FirstOrDefault()}"));
            TempData["Error"] = "The package was not brought in. " + string.Join(" ", result.Problems);
            var stillWaiting = await _db.WebsiteBuilderImports.AsNoTracking().AnyAsync(i => i.Id == id && i.Status == WebsiteBuilderImportStatus.Pending);
            return stillWaiting
                ? RedirectToAction(nameof(Review), new { id, design, without = Joined(without), keep = Joined(keep), sections = Joined(sections), moved = JoinedMoves(moved) })
                : RedirectToAction(nameof(Index), new { agentId = import.AgentUserId });
        }

        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderImportApplied",
            Short($"Brought Template Builder package \"{import.FileName}\" (import id {import.Id}) into agent id {import.AgentUserId}'s website: " +
                  $"{import.PageCount} page(s) from the package{(switchTo.HasValue ? $", design switched to template id {switchTo}" : string.Empty)}" +
                  $"{(removed.Count > 0 ? $", pages removed by choice: ids {string.Join(" ", removed.OrderBy(x => x))}" : string.Empty)}" +
                  $"{(kept.Except(result.MovedPageIds).Any() ? $", pages kept though removed in the builder: ids {string.Join(" ", kept.Except(result.MovedPageIds).OrderBy(x => x))}" : string.Empty)}" +
                  $"{(result.KeptSections > 0 ? $", {result.KeptSections} section(s) of replaced pages kept though not in the package" : string.Empty)}" +
                  $"{(result.MovedPageIds.Count > 0 ? $", pages at a new address kept as the page they were: ids {string.Join(" ", result.MovedPageIds.OrderBy(x => x))}" : string.Empty)}" +
                  $"{(result.MovedWithoutOldAddress.Count > 0 ? $" (old address not forwarded for ids {string.Join(" ", result.MovedWithoutOldAddress.OrderBy(x => x))})" : string.Empty)}. " +
                  "The site as it was is kept and can be put back."));
        TempData["Success"] = site.IsPublished
            ? "The package is on the site and live now. If it is not right, \"Put the previous site back\" below undoes it."
            : "The package is on the site. The site is not published yet, so visitors see nothing until the customer publishes it.";
        return RedirectToAction(nameof(Index), new { agentId = import.AgentUserId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Discard(int id)
    {
        var import = await _db.WebsiteBuilderImports.FirstOrDefaultAsync(i => i.Id == id);
        if (import == null) return NotFound();
        var blob = Storage();
        if (blob == null) return RedirectToAction(nameof(Index), new { agentId = import.AgentUserId });
        if (await BuilderImportService.DiscardAsync(_db, blob, import))
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderImportDiscarded",
                Short($"Dropped Template Builder package \"{import.FileName}\" (import id {import.Id}) for agent id {import.AgentUserId} without bringing it in."));
            TempData["Success"] = "The package was dropped. The site was never changed by it.";
        }
        else
        {
            TempData["Error"] = "That package is no longer waiting, so there is nothing to drop.";
        }
        return RedirectToAction(nameof(Index), new { agentId = import.AgentUserId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(int id)
    {
        var import = await _db.WebsiteBuilderImports.FirstOrDefaultAsync(i => i.Id == id);
        if (import == null) return NotFound();
        var blob = Storage();
        if (blob == null) return RedirectToAction(nameof(Index), new { agentId = import.AgentUserId });
        BuilderImportService.ApplyResult result;
        try
        {
            result = await BuilderImportService.RestoreAsync(_db, import, blob);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Template Builder import {ImportId} was not put back", id);
            _db.ChangeTracker.Clear();
            result = BuilderImportService.ApplyResult.Failed("The site was being changed at the same moment, so nothing was put back. Try again.");
        }
        if (!result.Ok)
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderImportNotRestored",
                Short($"Agent id {import.AgentUserId}'s website was NOT put back from Template Builder package \"{import.FileName}\" (import id {id}): {result.Problems.FirstOrDefault()}"));
        }
        if (result.Ok)
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderImportRestored",
                Short($"Put agent id {import.AgentUserId}'s website back as it was before Template Builder package \"{import.FileName}\" (import id {import.Id}) was brought in."));
            TempData["Success"] = "The site is back as it was before that package was brought in.";
        }
        else
        {
            TempData["Error"] = string.Join(" ", result.Problems);
        }
        return RedirectToAction(nameof(Index), new { agentId = import.AgentUserId });
    }

    private async Task<BuilderImportIndexViewModel?> IndexModelAsync(
        int agentId, IReadOnlyList<ImportProblem>? problems = null, string refusedFileName = "", IReadOnlyList<string>? handoverBlockers = null)
    {
        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent == null) return null;
        var site = await _db.AgentWebsites.AsNoTracking().Include(w => w.Template).FirstOrDefaultAsync(w => w.AgentUserId == agentId);
        var imports = site == null
            ? new List<WebsiteBuilderImport>()
            : await _db.WebsiteBuilderImports.AsNoTracking()
                .Where(i => i.AgentWebsiteId == site.Id)
                .OrderByDescending(i => i.Id)
                .Take(30)
                // The page lists packages; the long columns are read only where they are used.
                .Select(i => new WebsiteBuilderImport
                {
                    Id = i.Id, AgentUserId = i.AgentUserId, AgentWebsiteId = i.AgentWebsiteId, Status = i.Status, FileName = i.FileName,
                    TemplateName = i.TemplateName, Theme = i.Theme, HandoverId = i.HandoverId, PageCount = i.PageCount, BlockCount = i.BlockCount,
                    NewImageCount = i.NewImageCount, NewImageBytes = i.NewImageBytes, UploadedBy = i.UploadedBy, AppliedBy = i.AppliedBy,
                    CreatedAt = i.CreatedAt, AppliedAt = i.AppliedAt, ClosedAt = i.ClosedAt
                })
                .ToListAsync();
        var capacity = await _entitlements.GetAccessAsync(agentId, PackageFeatureCodes.FileUploadCapacity);
        return new BuilderImportIndexViewModel
        {
            Agent = agent,
            Website = site,
            Imports = imports,
            RestorableId = site == null ? null : await BuilderImportService.LatestAppliedIdAsync(_db, site.Id),
            Problems = problems ?? Array.Empty<ImportProblem>(),
            RefusedFileName = refusedFileName,
            LatestHandover = site == null ? null : await SiteHandoverService.LatestAsync(_db, site.Id),
            HandoverBlockers = handoverBlockers ?? Array.Empty<string>(),
            StorageUsedMb = AgentStorage.ToMb(await AgentStorage.TotalBytesAsync(_db, agentId)),
            StorageLimitMb = AgentStorage.DisplayLimitMb(capacity.LimitValue),
            Zone = AdminClock.Zone(_configuration)
        };
    }

    private static string? Joined(int[]? ids) =>
        ids == null || ids.Length == 0 ? null : string.Join(",", ids.Where(id => id > 0).Distinct().OrderBy(id => id));

    // 581: the review's choices on pages at a new address, one "slug:row" per page, as one value.
    private static string? JoinedMoves(string[]? moved) => BuilderImportService.JoinMoves(BuilderImportService.ParseMoves(moved));

    // An audit entry quotes what a package or a page says about itself: kept inside the audit log's
    // own column (500 characters; a longer line failed the write and the request with it).
    private static string Short(string details) => details.Length <= 480 ? details : details[..480] + "...";

    // File storage, or null with the reason on the screen. Resolved here and not in the constructor
    // (see AgentsController); without this the admin gets a raw error page.
    private IBlobStorageService? Storage()
    {
        try
        {
            return _services.GetRequiredService<IBlobStorageService>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blob storage unavailable for the Template Builder screens");
            TempData["Error"] = "File storage isn't configured for the admin app, so a site's pictures can be neither read nor stored. Nothing was changed. " +
                                "Set Azure__StorageConnectionString and Azure__StorageAccountName on the admin app, then try again.";
            return null;
        }
    }

    // One of the page editor's starter banners. They are files of the portal, not of storage, so the
    // hand-over asks the portal for the file by its name (which SiteHandoverService has already matched
    // against the list the editor offers: nothing a customer typed reaches this address).
    private async Task<byte[]?> ReadStarterBannerAsync(string fileName)
    {
        try
        {
            using var client = _services.GetRequiredService<IHttpClientFactory>().CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.GetAsync($"{PortalBaseUrl()}/images/starter-banners/{Uri.EscapeDataString(fileName)}", HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > BuilderContract.MaxAssetBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > BuilderContract.MaxAssetBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogWarning(ex, "Starter banner {File} could not be read from the portal", fileName);
            return null;
        }
    }

    // The portal's own address: the one definition both apps share (WebAppUrlHelper).
    private string PortalBaseUrl() => WebAppUrlHelper.GetWebAppBaseUrl(_configuration);
}
