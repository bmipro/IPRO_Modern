using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.Admin.Models;
using IPRO.Billing;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "AdminAccess")]
public class AgentsController : Controller
{
    private readonly IAgentService _agents;
    private readonly IWebsiteService _websites;
    private readonly IUnitOfWork _uow;
    private readonly IBillingService _billing;
    private readonly IPasswordHasher<AgentUser> _hasher;
    private readonly ILogger<AgentsController> _logger;
    private readonly IAdminAuditLogService _auditLog;
    private readonly IPRO.DataAccess.IPRODbContext _db;
    // Resolved lazily, never in the constructor: AzureBlobStorageService is a singleton whose
    // constructor reads Azure:StorageConnectionString, which the admin app does not configure. Taking
    // it as a constructor dependency made EVERY action on this controller throw
    // ArgumentNullException('connectionString') -- it took down the whole agent list. Same reason
    // ECardDesignsController resolves it through the service provider inside the action.
    private readonly IServiceProvider _services;

    public AgentsController(IAgentService agents, IWebsiteService websites,
        IUnitOfWork uow, IBillingService billing, IPasswordHasher<AgentUser> hasher, ILogger<AgentsController> logger, IAdminAuditLogService auditLog, IPRO.DataAccess.IPRODbContext db, IServiceProvider services)
    { _agents = agents; _websites = websites; _uow = uow; _billing = billing; _hasher = hasher; _logger = logger; _auditLog = auditLog; _db = db; _services = services; }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public async Task<IActionResult> Index(string? search, string? status, int page = 1)
    {
        var all = await _agents.GetAllAsync();
        if (!string.IsNullOrWhiteSpace(search))
        {
            all = int.TryParse(search, out var searchId)
                ? all.Where(a => a.Id == searchId)
                : all.Where(a => a.UserName.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || a.Email.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || a.FirstName.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || a.LastName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        if (status == "active")   all = all.Where(a => a.IsActive);
        if (status == "inactive") all = all.Where(a => !a.IsActive);

        ViewBag.Search     = search;
        ViewBag.Status     = status;
        ViewBag.TotalCount = all.Count();
        return View(PaginationHelper.Paginate(all.OrderByDescending(a => a.CreatedAt), page, 20));
    }

    public async Task<IActionResult> Details(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();

        var warnings = new List<string>();
        ViewBag.Website = await LoadDetailsPanelAsync(
            () => _websites.GetByAgentIdAsync(id),
            "Website details",
            warnings);
        ViewBag.Subscription = await LoadDetailsPanelAsync(
            () => _uow.Billings.FirstOrDefaultAsync(b => b.AgentUserId == id && b.Status == BillingStatus.Active),
            "Subscription details",
            warnings);
        ViewBag.Billings = await LoadDetailsPanelAsync(
            async () => (await _uow.Billings.FindAsync(b => b.AgentUserId == id)).OrderByDescending(b => b.CreatedAt).ToList(),
            "Billing history",
            warnings) ?? new List<IPRO.Entities.Billing>();
        ViewBag.SubscriptionChanges = await LoadDetailsPanelAsync(
            async () => (await _uow.SubscriptionChanges.FindAsync(c => c.AgentUserId == id)).OrderByDescending(c => c.CreatedAt).ToList(),
            "Subscription changes",
            warnings) ?? new List<SubscriptionChange>();
        ViewBag.Invoices = await LoadDetailsPanelAsync(
            async () => (await _uow.Invoices.FindAsync(i => i.AgentUserId == id)).OrderByDescending(i => i.IssuedAt).Take(10),
            "Invoices",
            warnings) ?? Enumerable.Empty<Invoice>();
        ViewBag.OpenInvoices = await LoadDetailsPanelAsync(
            async () => (await _uow.Invoices.FindAsync(i => i.AgentUserId == id && !i.IsPaid)).OrderByDescending(i => i.IssuedAt).ToList(),
            "Open invoices",
            warnings) ?? new List<Invoice>();
        ViewBag.FailedPaymentInvoices = await LoadDetailsPanelAsync(
            async () => (await _uow.Invoices.FindAsync(i =>
                    i.AgentUserId == id &&
                    !i.IsPaid &&
                    i.PayPalTransactionId.StartsWith("PAYPAL_FAILED:")))
                .OrderByDescending(i => i.IssuedAt)
                .ToList(),
            "Failed payment invoices",
            warnings) ?? new List<Invoice>();
        ViewBag.PackageLookup = (await LoadDetailsPanelAsync(
            async () => (await _uow.BillingRules.GetAllAsync()).ToDictionary(p => p.Id),
            "Packages",
            warnings)) ?? new Dictionary<int, BillingRule>();
        ViewBag.ClientCount = await LoadDetailsPanelAsync(
            () => _uow.Clients.CountAsync(c => c.AgentUserId == id),
            "Client count",
            warnings);
        ViewBag.Logs = await LoadDetailsPanelAsync(
            async () => (await _uow.OperateLogs.FindAsync(l => l.AgentUserId == id)).OrderByDescending(l => l.CreatedAt).Take(20),
            "Activity log",
            warnings) ?? Enumerable.Empty<OperateLog>();
        ViewBag.BillingLogs = await LoadDetailsPanelAsync(
            async () => (await _uow.OperateLogs.FindAsync(l =>
                    l.AgentUserId == id &&
                    (l.Module == "Billing" || l.Action.Contains("Invoice") || l.Action.Contains("Billing"))))
                .OrderByDescending(l => l.CreatedAt)
                .Take(25)
                .ToList(),
            "Billing activity log",
            warnings) ?? new List<OperateLog>();
        ViewBag.DetailsWarnings = warnings;
        return View(agent);
    }

    // Resources provisioning only runs when the agent has no page with the "resources" slug, so an
    // agent provisioned before three-tier navigation keeps the old flat shape forever. Deleting the
    // subtree lets WebsiteStarterResourcesHelper rebuild it the next time they open Website Pages;
    // their Article rows are reused by title rather than duplicated, so nothing they wrote is lost.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RebuildResources(int id)
    {
        var website = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(_db.AgentWebsites, w => w.AgentUserId == id);
        if (website == null)
        {
            TempData["Error"] = "This agent has no website yet, so there is nothing to rebuild.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var pages = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(_db.WebsitePages.Where(p => p.AgentWebsiteId == website.Id));
        var resources = pages.FirstOrDefault(p => p.Slug == "resources");
        if (resources == null)
        {
            TempData["Error"] = "This agent has no Resources page. It will be created the next time they open Website Pages.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var byParent = pages.ToLookup(p => p.ParentPageId);
        var doomed = new List<WebsitePage> { resources };
        var pending = new Queue<int>();
        pending.Enqueue(resources.Id);
        while (pending.Count > 0)
        {
            foreach (var child in byParent[pending.Dequeue()])
            {
                doomed.Add(child);
                pending.Enqueue(child.Id);
            }
        }

        var doomedIds = doomed.Select(p => p.Id).ToList();
        var blocks = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(_db.WebsiteContentBlocks.Where(b => doomedIds.Contains(b.WebsitePageId)));
        _db.WebsiteContentBlocks.RemoveRange(blocks);
        _db.WebsitePages.RemoveRange(doomed);
        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentRebuildResources",
            $"Removed {doomed.Count} Resources pages for agent #{id} so they rebuild at three levels");
        TempData["Success"] = $"Removed {doomed.Count} Resources page{(doomed.Count == 1 ? "" : "s")}. They rebuild with the three-level structure the next time this agent opens Website Pages.";
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        await LoadActivePackagesAsync();
        return View(ToEditModel(agent));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, AgentEditViewModel model)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();

        NormalizeAgent(model);
        ValidateAgentEdit(model);
        await ValidateUniqueAgentFieldsAsync(id, model);
        if (!ModelState.IsValid)
        {
            await LoadActivePackagesAsync();
            return View(model);
        }

        ApplyEditModel(agent, model);

        await _agents.UpdateAsync(agent);
        await LogAsync(id, "Edit", "Agent profile updated");
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentEdit", $"Agent '{agent.UserName}' profile updated");
        TempData["Success"] = $"Agent {agent.UserName} updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Read-only "what would be deleted" view. Deliberately the same predicates the erasure runs, so the
    // preview can never claim something different from what deletion actually does.
    [Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> ErasurePreview(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        ViewBag.Agent = agent;
        return View(await IPRO.DataAccess.AgentDataEraser.PreviewAsync(_db, id));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> Delete(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        var userName = agent.UserName;

        // Blob storage is resolved up front and treated as required, for the same reason billing is:
        // once the rows are deleted, the URLs of the agent's uploaded files are gone with them, so
        // proceeding without working storage would strand every file permanently and unrecoverably.
        IBlobStorageService blobs;
        try
        {
            blobs = _services.GetRequiredService<IBlobStorageService>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blob storage unavailable; refusing to delete agent {AgentId}", id);
            TempData["Error"] = $"File storage isn't configured for the admin app, so {userName}'s uploaded files " +
                                "(logo, photo, documents, gallery) could not be deleted. The account was NOT deleted -- " +
                                "deleting the rows first would strand those files permanently. Set Azure__StorageConnectionString " +
                                "and Azure__StorageAccountName on the admin app, then retry.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // Billing next, and fatal if it fails. Deleting the account while the PayPal subscription is
        // still live would keep charging a customer who no longer exists in the system, with no record
        // left to trace the charge back to -- strictly worse than refusing to delete.
        //
        // The active-subscription check has to happen here rather than relying on the return value:
        // CancelSubscriptionAsync returns false both when cancellation fails AND when there is simply
        // nothing to cancel. Treating those the same makes free/promo agents, and any agent whose
        // billing rows are already gone, permanently undeletable.
        var hasActiveSubscription = (await _uow.Billings.FindAsync(
            b => b.AgentUserId == id && b.Status == BillingStatus.Active)).Any();
        try
        {
            if (hasActiveSubscription && !await _billing.CancelSubscriptionAsync(id))
            {
                TempData["Error"] = $"Could not cancel {userName}'s PayPal subscription, so the account was NOT deleted. " +
                                    "Cancel the subscription in PayPal first, then retry -- otherwise billing would continue with no account attached.";
                return RedirectToAction(nameof(Details), new { id });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayPal cancellation failed for agent {AgentId}; deletion aborted", id);
            TempData["Error"] = $"PayPal cancellation errored ({ex.Message}). Agent {userName} was NOT deleted.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // Values needed after the rows are gone must be read while they still exist.
        var domainName = agent.DomainName;
        _db.ChangeTracker.Clear();

        // FILES BEFORE ROWS, deliberately. The rows are the only record of where an agent's files live,
        // so deleting rows first makes any later failure unrecoverable -- the files are stranded with
        // nothing left pointing at them. Doing files first is retryable: if anything fails afterwards,
        // the rows still hold the URLs and blob deletion is idempotent. (Learned the hard way on
        // 2026-08-04: an exception between the two stranded 10 files permanently.)
        var plan = await IPRO.DataAccess.AgentDataEraser.PreviewAsync(_db, id);

        // Logged before anything is destroyed, so even an unexpected crash leaves a recoverable record
        // of exactly which files belonged to this agent.
        if (plan.Blobs.Count > 0)
        {
            _logger.LogInformation("Deleting agent {AgentId} ({UserName}); files to remove: {BlobUrls}",
                id, userName, string.Join(" | ", plan.Blobs));
        }

        var blobsDeleted = 0;
        foreach (var url in plan.Blobs)
        {
            try
            {
                if (await blobs.DeleteAsync(url)) blobsDeleted++;
            }
            catch (Exception ex)
            {
                // A single unreachable blob shouldn't block the erasure; the URL is in the log above.
                _logger.LogError(ex, "Failed deleting blob {BlobUrl} for agent {AgentId}", url, id);
            }
        }

        var report = await IPRO.DataAccess.AgentDataEraser.EraseAsync(_db, id);


        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentDelete",
            $"Agent '{userName}' (id {id}) permanently deleted. PayPal subscription cancelled. " +
            $"{report.TotalRows} rows across {report.TableCount} tables; {blobsDeleted}/{plan.Blobs.Count} files removed; " +
            $"{plan.SharedBlobsKept.Count} shared library files kept.");

        TempData["Warning"] = $"Agent {userName} deleted: {report.TotalRows} rows across {report.TableCount} tables, " +
                              $"{blobsDeleted} uploaded files, PayPal subscription cancelled.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> ResetPassword(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();

        var temporaryPassword = BuildTemporaryPassword();
        agent.PasswordHash = _hasher.HashPassword(agent, temporaryPassword);
        agent.MustChangePassword = true;
        agent.PasswordChangedAt = null;

        await _agents.UpdateAsync(agent);
        await LogAsync(id, "ResetPassword", "Temporary password reset by Super Admin");
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentResetPassword", $"Temporary password reset for agent '{agent.UserName}'");

        TempData["Success"] = $"Temporary password for {agent.UserName} reset to: {temporaryPassword}";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EmailInvoice(int id, int invoiceId)
    {
        var invoice = await _uow.Invoices.GetByIdAsync(invoiceId);
        if (invoice == null || invoice.AgentUserId != id)
        {
            return NotFound();
        }

        var result = await _billing.EmailPaidInvoiceAsync(invoiceId, force: true);
        if (result.Success)
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentEmailInvoice", $"Invoice {invoice.InvoiceNumber} resent to agent id {id}");
        }
        TempData[result.Success ? "Success" : "Error"] = result.Success
            ? $"Invoice {invoice.InvoiceNumber} emailed to the agent's current email address."
            : result.Message;

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        agent.IsActive = true;
        await _agents.UpdateAsync(agent);
        await LogAsync(id, "Activate", "Agent activated");
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentActivate", $"Agent '{agent.UserName}' activated");
        TempData["Success"] = $"Agent {agent.UserName} activated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(int id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent == null) return NotFound();
        await _agents.DeactivateAsync(id);
        await LogAsync(id, "Deactivate", "Agent deactivated");
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AgentDeactivate", $"Agent '{agent.UserName}' deactivated");
        TempData["Warning"] = $"Agent {agent.UserName} deactivated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ProvisionHosting/PleskLogin removed 2026-08-04 along with the rest of the Plesk integration:
    // agent sites are hosted on Azure now, so there is no control panel to provision against or log
    // into. Agent domains are managed through AgentDomain + the domain automation job instead.

    private async Task LogAsync(int agentId, string action, string desc)
    {
        await _uow.OperateLogs.AddAsync(new OperateLog { AgentUserId = agentId, Action = action, Module = "Agents", Description = desc, CreatedAt = DateTime.UtcNow });
        await _uow.SaveChangesAsync();
    }

    private async Task<T?> LoadDetailsPanelAsync<T>(Func<Task<T>> load, string panelName, List<string> warnings)
    {
        try
        {
            return await load();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load {PanelName} on agent details page.", panelName);
            warnings.Add($"{panelName} could not be loaded because some legacy data needs cleanup.");
            return default;
        }
    }

    // DeleteAgentOwnedDataAsync/RemoveEach removed 2026-08-04: they covered 15 entity types and had
    // fallen ~25 behind the schema, silently orphaning everything added since. Replaced by
    // IPRO.DataAccess.AgentDataEraser, which drives deletion from one declarative table map that also
    // powers the preview screen. Add new agent-owned tables there, not here.

    private static string BuildTemporaryPassword() => EncryptionService.GenerateToken(12);

    private static AgentEditViewModel ToEditModel(AgentUser agent) => new()
    {
        Id = agent.Id,
        UserName = agent.UserName ?? "",
        Email = agent.Email ?? "",
        FirstName = agent.FirstName ?? "",
        LastName = agent.LastName ?? "",
        Designation = agent.Designation ?? "",
        CompanyName = agent.CompanyName ?? "",
        CompanyAddress = agent.CompanyAddress ?? "",
        City = agent.City ?? "",
        Province = agent.Province ?? "",
        PostalCode = agent.PostalCode ?? "",
        Country = agent.Country ?? "",
        TimeZone = agent.TimeZone ?? "",
        Phone = agent.Phone ?? "",
        BusinessFax = agent.BusinessFax ?? "",
        CellPhone = agent.CellPhone ?? "",
        BusinessType = agent.BusinessType ?? "",
        DomainName = agent.DomainName ?? "",
        PackageId = agent.PackageId,
        PromotionCode = agent.PromotionCode ?? "",
        IsActive = agent.IsActive,
        MustChangePassword = agent.MustChangePassword
    };

    private static void ApplyEditModel(AgentUser agent, AgentEditViewModel model)
    {
        agent.UserName = model.UserName;
        agent.Email = model.Email;
        agent.FirstName = model.FirstName;
        agent.LastName = model.LastName;
        agent.Designation = model.Designation ?? "";
        agent.CompanyName = model.CompanyName;
        agent.CompanyAddress = model.CompanyAddress ?? "";
        agent.City = model.City;
        agent.Province = model.Province;
        agent.PostalCode = model.PostalCode;
        agent.Country = model.Country;
        agent.TimeZone = model.TimeZone ?? "";
        agent.Phone = model.Phone;
        agent.BusinessFax = model.BusinessFax ?? "";
        agent.CellPhone = model.CellPhone ?? "";
        agent.BusinessType = model.BusinessType;
        agent.DomainName = model.DomainName ?? "";
        agent.PackageId = model.PackageId;
        agent.PromotionCode = model.PromotionCode ?? "";
        agent.IsActive = model.IsActive;
        agent.MustChangePassword = model.MustChangePassword;
    }

    private static void NormalizeAgent(AgentEditViewModel agent)
    {
        agent.UserName = agent.UserName?.Trim() ?? "";
        agent.Email = (agent.Email?.Trim() ?? "").ToLowerInvariant();
        agent.FirstName = agent.FirstName?.Trim() ?? "";
        agent.LastName = agent.LastName?.Trim() ?? "";
        agent.Designation = agent.Designation?.Trim() ?? "";
        agent.CompanyName = agent.CompanyName?.Trim() ?? "";
        agent.CompanyAddress = agent.CompanyAddress?.Trim() ?? "";
        agent.City = agent.City?.Trim() ?? "";
        agent.Province = agent.Province?.Trim() ?? "";
        agent.PostalCode = agent.PostalCode?.Trim() ?? "";
        agent.Country = agent.Country?.Trim() ?? "";
        agent.TimeZone = agent.TimeZone?.Trim() ?? "";
        agent.Phone = agent.Phone?.Trim() ?? "";
        agent.BusinessFax = agent.BusinessFax?.Trim() ?? "";
        agent.CellPhone = agent.CellPhone?.Trim() ?? "";
        agent.BusinessType = agent.BusinessType?.Trim() ?? "";
        agent.DomainName = agent.DomainName?.Trim() ?? "";
        agent.PromotionCode = agent.PromotionCode?.Trim() ?? "";
    }

    private void ValidateAgentEdit(AgentEditViewModel agent)
    {
        if (string.IsNullOrWhiteSpace(agent.UserName)) ModelState.AddModelError("", "Username is required.");
        if (string.IsNullOrWhiteSpace(agent.Email)) ModelState.AddModelError("", "Email is required.");
        if (string.IsNullOrWhiteSpace(agent.FirstName)) ModelState.AddModelError("", "First name is required.");
        if (string.IsNullOrWhiteSpace(agent.LastName)) ModelState.AddModelError("", "Last name is required.");
        if (string.IsNullOrWhiteSpace(agent.CompanyName)) ModelState.AddModelError("", "Company name is required.");
        if (string.IsNullOrWhiteSpace(agent.City)) ModelState.AddModelError("", "City is required.");
        if (string.IsNullOrWhiteSpace(agent.Province)) ModelState.AddModelError("", "Province is required.");
        if (string.IsNullOrWhiteSpace(agent.PostalCode)) ModelState.AddModelError("", "Postal code is required.");
        if (string.IsNullOrWhiteSpace(agent.Country)) ModelState.AddModelError("", "Country is required.");
        if (string.IsNullOrWhiteSpace(agent.Phone)) ModelState.AddModelError("", "Business phone is required.");
        if (string.IsNullOrWhiteSpace(agent.BusinessType)) ModelState.AddModelError("", "Business type is required.");
        if (agent.PackageId <= 0) ModelState.AddModelError("", "Package is required.");
    }

    private async Task ValidateUniqueAgentFieldsAsync(int id, AgentEditViewModel agent)
    {
        var existingUserName = await _uow.AgentUsers.FirstOrDefaultAsync(a => a.UserName == agent.UserName && a.Id != id);
        if (existingUserName != null) ModelState.AddModelError("", "Username is already used by another agent.");

        var existingEmail = await _uow.AgentUsers.FirstOrDefaultAsync(a => a.Email == agent.Email && a.Id != id);
        if (existingEmail != null) ModelState.AddModelError("", "Email is already used by another agent.");

        if (!string.IsNullOrWhiteSpace(agent.DomainName))
        {
            var existingDomain = await _uow.AgentUsers.FirstOrDefaultAsync(a => a.DomainName == agent.DomainName && a.Id != id);
            if (existingDomain != null) ModelState.AddModelError("", "Domain is already used by another agent.");
        }
    }

    private async Task LoadActivePackagesAsync()
    {
        var packages = await _uow.BillingRules.FindAsync(p => p.IsActive);
        ViewBag.Packages = packages
            .OrderBy(GetPackageRank)
            .ThenBy(p => p.MonthlyPrice <= 0 ? decimal.MaxValue : p.MonthlyPrice)
            .ThenBy(p => p.PackageName)
            .ToList();
    }

    private static int GetPackageRank(BillingRule package) => package.PackageName switch
    {
        "IPro Silver" => 1,
        "IPro Gold" => 2,
        "IPro Platinum" => 3,
        "Broker Package" => 4,
        _ => 50
    };
}
