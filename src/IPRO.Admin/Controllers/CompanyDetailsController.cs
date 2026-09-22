using System.Security.Claims;
using IPRO.Admin.Models;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

// 514 (2026-09-22): the supplier as it appears on every invoice -- name, address, GST/HST
// registration number, billing email and website -- edited here instead of living in the
// application settings (the owner: "can u not hardcode the elements needed i.e. GST/HST and
// addresses so we could populate it from superadmin"). One row (BillingCompanyProfile); a field
// left blank falls back to the setting that served before, so nothing on an invoice goes missing
// while the page is being filled in. Read by the customer invoice page, the paid-invoice email and
// the Revenue print view through BillingCompanyDetails.
[Authorize(Policy = "SuperAdmin")]
public class CompanyDetailsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IAdminAuditLogService _auditLog;

    public CompanyDetailsController(IPRODbContext db, IConfiguration configuration, IAdminAuditLogService auditLog)
    {
        _db = db;
        _configuration = configuration;
        _auditLog = auditLog;
    }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";
    private string? Setting(string key) => _configuration[key];

    public async Task<IActionResult> Index()
    {
        var row = await _db.BillingCompanyProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == 1);
        var model = row != null ? CompanyDetailsViewModel.From(row) : CompanyDetailsViewModel.FromSettings(Setting);
        ViewBag.Preview = BillingCompanyDetails.From(row, Setting);
        ViewBag.HasSavedRow = row != null;
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(CompanyDetailsViewModel model)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.Preview = await BillingCompanyDetails.LoadAsync(_db, Setting);
            ViewBag.HasSavedRow = await _db.BillingCompanyProfiles.AnyAsync(p => p.Id == 1);
            return View(model);
        }

        var before = await BillingCompanyDetails.LoadAsync(_db, Setting);
        await BillingCompanyDetails.SaveAsync(_db, model.ToProfile(), DateTime.UtcNow);
        var after = await BillingCompanyDetails.LoadAsync(_db, Setting);

        // The audit line carries before and after, so a wrong GST/HST number or address on the
        // invoices can be reconstructed from the log.
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "CompanyDetailsUpdate", Describe(before) == Describe(after)
            ? $"Saved company details; nothing changed: {Describe(after)}"
            : $"Company details changed: {Describe(before)} -> {Describe(after)}");
        TempData["Success"] = "Company details saved. Every invoice, the paid-invoice email and the Revenue print view now show them.";
        return RedirectToAction(nameof(Index));
    }

    private static string Describe(BillingCompanyDetails d) =>
        $"{d.Name} | {(d.AddressLines.Count == 0 ? "(no address)" : string.Join(", ", d.AddressLines))} | GST/HST {(d.TaxRegistrationNumber.Length == 0 ? "(none)" : d.TaxRegistrationNumber)} | {d.Email} | {d.Website}";
}
