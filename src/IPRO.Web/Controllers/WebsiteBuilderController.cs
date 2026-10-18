using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.Business.SiteImport;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

// 585: the new website builder (Beta) from the adviser's side of iPro. The card on My Website posts
// here; the builder itself is a separate app (Builder:BaseUrl), which the adviser reaches with a
// one-time pass and nothing else (BuilderBeta, CONTRACT.md section 11).
//
//   Open      "Try the new builder", "Continue my draft" or "Start again from my live site": a pass,
//             carried to the builder by a form that sends itself. Only "Start again" says anything
//             to the builder (startFresh); whether a draft is continued is the builder's to decide.
//   Review    what the adviser sent, page by page, with a look at each, for an adviser a SuperAdmin
//             lets publish (the owner's decision 3 C; otherwise iPro's staff review it).
//   Publish   brings it in, keeping everything the adviser did not remove in the builder.
//   PutBack   puts the previous site back.
[Authorize]
public class WebsiteBuilderController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _services;
    private readonly ILogger<WebsiteBuilderController> _logger;

    public WebsiteBuilderController(IPRODbContext db, IPackageEntitlementService entitlements, IConfiguration configuration,
        IServiceProvider services, ILogger<WebsiteBuilderController> logger)
    {
        _db = db;
        _entitlements = entitlements;
        _configuration = configuration;
        _services = services;
        _logger = logger;
    }

    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // The builder's address, or null where it is not set up (the card then says the Beta is not
    // available here, and nothing posts anywhere).
    public static string? BuilderBaseUrl(IConfiguration configuration)
    {
        var value = configuration["Builder:BaseUrl"]?.Trim().TrimEnd('/');
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? value : null;
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Open(bool startFresh = false)
    {
        var gate = await GateAsync();
        if (gate.Refusal != null) return gate.Refusal;
        var builder = BuilderBaseUrl(_configuration);
        if (builder == null)
        {
            TempData["Error"] = "The new builder is not available here yet.";
            return RedirectToAction("Index", "Website");
        }

        var pass = await BuilderBeta.IssuePassAsync(_db, gate.Site!, startFresh, DateTime.UtcNow);
        _logger.LogInformation("Agent {AgentId} opened the builder ({Kind})", AgentId, startFresh ? "start again" : "open");
        Response.Headers.CacheControl = "no-store";
        return View(new WebsiteBuilderOpenViewModel(builder + "/open", pass));
    }

    public async Task<IActionResult> Review(int id)
    {
        var (import, refusal) = await AdviserImportAsync(id);
        if (import == null) return refusal!;

        var planned = await PlanAsync(import);
        if (planned?.Plan == null)
        {
            TempData["Error"] = "What you sent can no longer be brought in as it is: " +
                string.Join(" ", (planned?.Package.Problems ?? Array.Empty<ImportProblem>()).Take(3).Select(p => p.Message)) +
                " Open the builder again from My Website.";
            return RedirectToAction("Index", "Website");
        }

        var key = await BuilderImportService.LookKeyAsync(_db, import);
        var keep = KeptSections(planned.Plan);
        var look = $"/PublicWebsite/PreviewBuilderImport?id={import.Id}&key={key}"
            + (keep.Count > 0 ? "&sections=" + string.Join(",", keep.OrderBy(x => x)) : string.Empty) + "&page=";
        return View(new WebsiteBuilderReviewViewModel(import, planned.Plan, look, planned.Site.IsPublished));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(int id, string? plan)
    {
        var (import, refusal) = await AdviserImportAsync(id);
        if (import == null) return refusal!;
        var planned = await PlanAsync(import);
        if (planned?.Plan == null) return RedirectToAction(nameof(Review), new { id });

        BuilderImportService.ApplyResult result;
        try
        {
            result = await BuilderImportService.ApplyAsync(_db, import, new HashSet<int>(), null, "the customer (Beta)",
                keepPageIds: new HashSet<int>(), reviewedPlan: plan ?? string.Empty, keepSectionIds: KeptSections(planned.Plan));
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Agent {AgentId}'s builder package {ImportId} was not written", AgentId, id);
            _db.ChangeTracker.Clear();
            result = BuilderImportService.ApplyResult.Failed("Your website was being changed at the same moment, so nothing was written. Look at the review again, then publish.");
        }
        if (!result.Ok)
        {
            TempData["Error"] = "Nothing was published. " + string.Join(" ", result.Problems);
            var waiting = await _db.WebsiteBuilderImports.AsNoTracking().AnyAsync(i => i.Id == id && i.Status == WebsiteBuilderImportStatus.Pending);
            return waiting ? RedirectToAction(nameof(Review), new { id }) : RedirectToAction("Index", "Website");
        }

        _logger.LogInformation("Agent {AgentId} published builder package {ImportId}", AgentId, id);
        TempData["Success"] = planned.Site.IsPublished
            ? "Your changes are on your website now. If something is not right, \"Put my previous site back\" below undoes it."
            : "Your changes are on your website. It is not published yet, so visitors see them once you publish it.";
        return RedirectToAction("Index", "Website");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Discard(int id)
    {
        var (import, refusal) = await AdviserImportAsync(id);
        if (import == null) return refusal!;
        var dropped = await BuilderImportService.DiscardAsync(_db, _services.GetRequiredService<IBlobStorageService>(), import);
        TempData[dropped ? "Success" : "Error"] = dropped
            ? "What you sent was dropped. Your website did not change, and your draft is still in the builder."
            : "That is no longer waiting, so there is nothing to drop.";
        return RedirectToAction("Index", "Website");
    }

    // The site before the adviser's newest package from the builder, back. Only that package can be
    // taken back out (it is the site's newest), and what was changed since goes with it: the card
    // lists those pages before the button is pressed.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PutBack()
    {
        // Also when the Beta has been switched off since: the site can always be put back.
        var gate = await GateAsync(requireBeta: false);
        if (gate.Refusal != null) return gate.Refusal;
        var state = await BuilderBeta.StateAsync(_db, gate.Site!, DateTime.UtcNow);
        if (state.PutBack == null)
        {
            TempData["Error"] = "There is no change from the new builder to undo.";
            return RedirectToAction("Index", "Website");
        }
        var import = await _db.WebsiteBuilderImports.FirstAsync(i => i.Id == state.PutBack.Id);
        BuilderImportService.ApplyResult result;
        try
        {
            result = await BuilderImportService.RestoreAsync(_db, import, _services.GetRequiredService<IBlobStorageService>());
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Agent {AgentId}'s previous site was not put back", AgentId);
            _db.ChangeTracker.Clear();
            result = BuilderImportService.ApplyResult.Failed("Your website was being changed at the same moment, so nothing was put back. Try again.");
        }
        if (result.Ok) _logger.LogInformation("Agent {AgentId} put their previous site back (import {ImportId})", AgentId, import.Id);
        TempData[result.Ok ? "Success" : "Error"] = result.Ok
            ? "Your previous site is back, as it was before your changes from the new builder."
            : string.Join(" ", result.Problems);
        return RedirectToAction("Index", "Website");
    }

    // ---- shared ----

    private async Task<(AgentWebsite? Site, IActionResult? Refusal)> GateAsync(bool requireBeta = true)
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.InstantWebsite);
        if (!access.IsIncluded)
        {
            TempData["Error"] = access.UpgradeMessage;
            return (null, RedirectToAction("Index", "Billing"));
        }
        if (requireBeta && !await BuilderBeta.IsOnAsync(_db, AgentId)) return (null, NotFound());
        var site = await _db.AgentWebsites.AsNoTracking().Include(w => w.Template).FirstOrDefaultAsync(w => w.AgentUserId == AgentId);
        if (site == null)
        {
            TempData["Error"] = "Set up your website first.";
            return (null, RedirectToAction("Index", "Website"));
        }
        return (site, null);
    }

    // A package this adviser sent from the builder, still waiting, on their own site, and theirs to
    // publish. Anything else is not found: an adviser never sees another's package, nor one iPro's
    // staff are to review.
    private async Task<(WebsiteBuilderImport? Import, IActionResult? Refusal)> AdviserImportAsync(int id)
    {
        var gate = await GateAsync();
        if (gate.Refusal != null) return (null, gate.Refusal);
        var beta = await BuilderBeta.FindAsync(_db, AgentId);
        var import = await _db.WebsiteBuilderImports.FirstOrDefaultAsync(i => i.Id == id && i.AgentWebsiteId == gate.Site!.Id);
        if (beta is not { AdviserPublishes: true } || import == null || import.Source != WebsiteBuilderImportSources.Customer) return (null, NotFound());
        if (import.Status != WebsiteBuilderImportStatus.Pending)
        {
            TempData["Error"] = "That is no longer waiting to be published.";
            return (null, RedirectToAction("Index", "Website"));
        }
        return (import, null);
    }

    // The plan with the adviser's defaults: every section the package does not bring back and that
    // they did not remove in the builder is kept (a section added in the page editor meanwhile). A
    // page they removed in the builder goes; a page at a new address is iPro's reading of it.
    private async Task<BuilderImportService.PlanResult?> PlanAsync(WebsiteBuilderImport import)
    {
        var first = await BuilderImportService.PlanAsync(_db, import);
        if (first?.Plan == null) return first;
        var keep = KeptSections(first.Plan);
        return keep.Count == 0 ? first : await BuilderImportService.PlanAsync(_db, import, keepSectionIds: keep);
    }

    internal static HashSet<int> KeptSections(SiteImportPlan plan) =>
        plan.Pages.SelectMany(p => p.SectionsGoing).Where(g => !g.RemovedInBuilder).Select(g => g.BlockId).ToHashSet();
}
