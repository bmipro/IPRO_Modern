using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class TrialInviteCodesController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IAdminAuditLogService _auditLog;
    private readonly IConfiguration _configuration;

    public TrialInviteCodesController(IUnitOfWork uow, IAdminAuditLogService auditLog, IConfiguration configuration)
    {
        _uow = uow;
        _auditLog = auditLog;
        _configuration = configuration;
    }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public async Task<IActionResult> Index()
    {
        var codes = (await _uow.TrialInviteCodes.GetAllAsync()).ToList();
        var packages = (await _uow.BillingRules.GetAllAsync()).ToDictionary(p => p.Id, p => p.PackageName);
        ViewBag.PackageNames = packages;

        var configured = _configuration["App:PortalBaseUrl"]?.Trim().TrimEnd('/');
        ViewBag.PortalBaseUrl = Uri.TryCreate(configured, UriKind.Absolute, out _) && !configured!.Contains("YOUR_", StringComparison.OrdinalIgnoreCase)
            ? configured
            : "https://ipro-prod-web.azurewebsites.net";

        return View(codes.OrderByDescending(c => c.CreatedAt));
    }

    public async Task<IActionResult> Create()
    {
        ViewBag.Packages = (await _uow.BillingRules.GetAllAsync()).Where(p => p.IsTrialPackage).OrderBy(p => p.PackageName).ToList();
        return View("Edit", new TrialInviteCode { IsActive = true });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var code = await _uow.TrialInviteCodes.GetByIdAsync(id);
        if (code == null) return NotFound();

        ViewBag.Packages = (await _uow.BillingRules.GetAllAsync()).Where(p => p.IsTrialPackage).OrderBy(p => p.PackageName).ToList();
        return View(code);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(TrialInviteCode model)
    {
        model.Code = model.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        model.Description = model.Description?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(model.Code))
        {
            ModelState.AddModelError(nameof(model.Code), "Code is required.");
        }
        else
        {
            var duplicate = await _uow.TrialInviteCodes.ExistsAsync(c => c.Id != model.Id && c.Code == model.Code);
            if (duplicate)
            {
                ModelState.AddModelError(nameof(model.Code), "That code is already in use.");
            }
        }

        if (model.BillingRuleId <= 0)
        {
            ModelState.AddModelError(nameof(model.BillingRuleId), "Choose which trial package this code unlocks.");
        }
        else
        {
            var package = await _uow.BillingRules.GetByIdAsync(model.BillingRuleId);
            if (package == null || !package.IsTrialPackage)
            {
                ModelState.AddModelError(nameof(model.BillingRuleId), "Choose an active trial package.");
            }
        }

        if (!ModelState.IsValid)
        {
            ViewBag.Packages = (await _uow.BillingRules.GetAllAsync()).Where(p => p.IsTrialPackage).OrderBy(p => p.PackageName).ToList();
            return View(model);
        }

        var isNew = model.Id == 0;
        if (isNew)
        {
            model.CreatedAt = DateTime.UtcNow;
            await _uow.TrialInviteCodes.AddAsync(model);
        }
        else
        {
            var existing = await _uow.TrialInviteCodes.GetByIdAsync(model.Id);
            if (existing == null) return NotFound();

            existing.Code = model.Code;
            existing.Description = model.Description;
            existing.BillingRuleId = model.BillingRuleId;
            existing.IsActive = model.IsActive;
            existing.ExpiresAt = model.ExpiresAt;
            existing.MaxRedemptions = model.MaxRedemptions;

            _uow.TrialInviteCodes.Update(existing);
        }

        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, isNew ? "TrialInviteCreate" : "TrialInviteEdit", $"Trial invite code '{model.Code}' {(isNew ? "created" : "updated")}");
        TempData["Success"] = "Trial invite code saved.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Redemptions(int id)
    {
        var code = await _uow.TrialInviteCodes.GetByIdAsync(id);
        if (code == null) return NotFound();

        var redemptions = (await _uow.TrialInviteCodeRedemptions.FindAsync(r => r.TrialInviteCodeId == id))
            .OrderByDescending(r => r.RedeemedAt)
            .ToList();
        var agents = (await _uow.AgentUsers.GetAllAsync()).ToDictionary(a => a.Id);

        ViewBag.Code = code;
        ViewBag.Agents = agents;
        return View(redemptions);
    }
}
