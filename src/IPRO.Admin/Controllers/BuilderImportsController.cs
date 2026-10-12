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

    public BuilderImportsController(IPRODbContext db, IPackageEntitlementService entitlements, IAdminAuditLogService auditLog,
        IConfiguration configuration, IServiceProvider services)
    {
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
        var model = await IndexModelAsync(agentId, Array.Empty<ImportProblem>(), string.Empty);
        return model == null ? NotFound() : View(model);
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
            var capacity = await _entitlements.GetAccessAsync(agentId, PackageFeatureCodes.FileUploadCapacity);
            var context = await BuilderImportService.ContextAsync(_db, site, AgentStorage.LimitBytes(capacity.LimitValue));
            read = BuilderPackageReader.Read(buffer.ToArray(), context);
        }

        var fileName = Path.GetFileName(package.FileName);
        if (!read.IsValid)
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderImportRefused",
                $"Template Builder package \"{fileName}\" for agent id {agentId} was not taken: {read.Problems.Count} problem(s); the first: {read.Problems[0].Message}");
            var model = await IndexModelAsync(agentId, read.Problems, fileName);
            return model == null ? NotFound() : View(nameof(Index), model);
        }

        var blob = _services.GetRequiredService<IBlobStorageService>();
        var import = await BuilderImportService.StoreAsync(_db, blob, site, read, fileName, CurrentAdminUsername);
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderImportStored",
            $"Template Builder package \"{fileName}\" for agent id {agentId} passed the contract and is waiting (import id {import.Id}; " +
            $"{import.PageCount} page(s), {import.NewImageCount} new image(s)). The site is unchanged.");
        TempData["Success"] = "The package passed every rule. Nothing on the site has changed yet: review it below, then bring it in.";
        return RedirectToAction(nameof(Review), new { id = import.Id });
    }

    // without: the ids of the pages to remove, comma-separated ("12,15"), as the look's address carries them.
    public async Task<IActionResult> Review(int id, int? design, string? without)
    {
        var import = await _db.WebsiteBuilderImports.FirstOrDefaultAsync(i => i.Id == id);
        if (import == null) return NotFound();
        if (import.Status != WebsiteBuilderImportStatus.Pending) return RedirectToAction(nameof(Index), new { agentId = import.AgentUserId });

        var removed = Ids(without);
        var planned = await BuilderImportService.PlanAsync(_db, import, removed);
        if (planned == null) return NotFound();

        var designs = await _db.WebsiteTemplates.AsNoTracking().Where(t => t.IsActive || t.Id == planned.Site.TemplateId).OrderBy(t => t.Name).ToListAsync();
        if (design.HasValue && (design == planned.Site.TemplateId || designs.All(d => d.Id != design))) design = null;

        // The look opens on the portal, where a SuperAdmin is not signed in: the link carries the key.
        var key = await BuilderImportService.LookKeyAsync(_db, import);
        var look = $"{PortalBaseUrl()}/PublicWebsite/PreviewBuilderImport?id={import.Id}&key={key}";
        if (design.HasValue) look += $"&design={design}";
        if (removed.Count > 0) look += "&without=" + string.Join(",", removed.OrderBy(x => x));

        return View(new BuilderImportReviewViewModel
        {
            Agent = planned.Site.AgentUser,
            Website = planned.Site,
            Import = import,
            Package = planned.Package,
            Plan = planned.Plan,
            Images = BuilderImportService.StoredImages(import),
            Designs = designs,
            Design = design,
            Without = removed,
            LookAddress = look + "&page=",
            Zone = AdminClock.Zone(_configuration)
        });
    }

    // The review screen's choices (the design, the pages to remove) are a form; this turns them into
    // the review's own address so the looks and the "bring it in" button all read the same choices.
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Choose(int id, int? design, int[]? without) =>
        RedirectToAction(nameof(Review), new { id, design, without = Joined(without) });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(int id, int? design, int[]? without)
    {
        var import = await _db.WebsiteBuilderImports.FirstOrDefaultAsync(i => i.Id == id);
        if (import == null) return NotFound();
        var site = await _db.AgentWebsites.AsNoTracking().FirstOrDefaultAsync(w => w.Id == import.AgentWebsiteId);
        if (site == null) return NotFound();

        var removed = (without ?? Array.Empty<int>()).ToHashSet();
        int? switchTo = design.HasValue && design != site.TemplateId ? design : null;
        var result = await BuilderImportService.ApplyAsync(_db, import, removed, switchTo, CurrentAdminUsername);
        if (!result.Ok)
        {
            TempData["Error"] = "The package was not brought in. " + string.Join(" ", result.Problems);
            return import.Status == WebsiteBuilderImportStatus.Pending
                ? RedirectToAction(nameof(Review), new { id, design, without = Joined(without) })
                : RedirectToAction(nameof(Index), new { agentId = import.AgentUserId });
        }

        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderImportApplied",
            $"Brought Template Builder package \"{import.FileName}\" (import id {import.Id}) into agent id {import.AgentUserId}'s website: " +
            $"{import.PageCount} page(s) from the package, {removed.Count} page(s) removed{(switchTo.HasValue ? $", design switched to template id {switchTo}" : string.Empty)}. " +
            "The site as it was is kept and can be put back.");
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
        var blob = _services.GetRequiredService<IBlobStorageService>();
        if (await BuilderImportService.DiscardAsync(_db, blob, import))
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderImportDiscarded",
                $"Dropped Template Builder package \"{import.FileName}\" (import id {import.Id}) for agent id {import.AgentUserId} without bringing it in.");
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
        var result = await BuilderImportService.RestoreAsync(_db, import);
        if (result.Ok)
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "BuilderImportRestored",
                $"Put agent id {import.AgentUserId}'s website back as it was before Template Builder package \"{import.FileName}\" (import id {import.Id}) was brought in.");
            TempData["Success"] = "The site is back as it was before that package was brought in.";
        }
        else
        {
            TempData["Error"] = string.Join(" ", result.Problems);
        }
        return RedirectToAction(nameof(Index), new { agentId = import.AgentUserId });
    }

    private async Task<BuilderImportIndexViewModel?> IndexModelAsync(int agentId, IReadOnlyList<ImportProblem> problems, string refusedFileName)
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
                // The page lists packages; the two long columns are read only where they are used.
                .Select(i => new WebsiteBuilderImport
                {
                    Id = i.Id, AgentUserId = i.AgentUserId, AgentWebsiteId = i.AgentWebsiteId, Status = i.Status, FileName = i.FileName,
                    TemplateName = i.TemplateName, Theme = i.Theme, PageCount = i.PageCount, BlockCount = i.BlockCount,
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
            RestorableId = site == null ? null : (await BuilderImportService.LatestAppliedAsync(_db, site.Id))?.Id,
            Problems = problems,
            RefusedFileName = refusedFileName,
            StorageUsedMb = AgentStorage.ToMb(await AgentStorage.TotalBytesAsync(_db, agentId)),
            StorageLimitMb = AgentStorage.DisplayLimitMb(capacity.LimitValue),
            Zone = AdminClock.Zone(_configuration)
        };
    }

    private static HashSet<int> Ids(string? list) =>
        (list ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var id) ? id : 0).Where(id => id > 0).ToHashSet();

    private static string? Joined(int[]? ids) =>
        ids == null || ids.Length == 0 ? null : string.Join(",", ids.Where(id => id > 0).Distinct().OrderBy(id => id));

    // The portal's own address: the one definition both apps share (WebAppUrlHelper).
    private string PortalBaseUrl() => WebAppUrlHelper.GetWebAppBaseUrl(_configuration);
}
