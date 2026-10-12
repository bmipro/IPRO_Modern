using IPRO.Business.Services;
using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class RecurringInvoicesController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public RecurringInvoicesController(IPRODbContext db, IPackageEntitlementService entitlements)
    {
        _db = db;
        _entitlements = entitlements;
    }

    public async Task<IActionResult> Index()
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var schedules = await _db.RecurringInvoiceSchedules
            .AsNoTracking()
            .Include(s => s.Client)
            .Where(s => s.AgentUserId == AgentId)
            .OrderBy(s => s.NextRunDate)
            .ToListAsync();

        return View(schedules);
    }

    public async Task<IActionResult> Create()
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        await LoadClientsAsync();
        return View("Edit", new RecurringInvoiceEditViewModel());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(RecurringInvoiceEditViewModel model)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == model.ClientId && c.AgentUserId == AgentId);
        if (client == null)
        {
            ModelState.AddModelError(nameof(model.ClientId), "Choose a client.");
        }
        if (model.LineItems.Count == 0 || model.LineItems.All(l => string.IsNullOrWhiteSpace(l.Description)))
        {
            ModelState.AddModelError(string.Empty, "Add at least one line item.");
        }
        if (!ModelState.IsValid)
        {
            await LoadClientsAsync();
            return View("Edit", model);
        }

        var schedule = new RecurringInvoiceSchedule
        {
            AgentUserId = AgentId,
            ClientId = model.ClientId,
            Frequency = model.Frequency,
            NextRunDate = model.NextRunDate,
            DueInDays = model.DueInDays,
            Notes = model.Notes?.Trim(),
            IsActive = true
        };

        ApplyDiscount(schedule, model);
        ApplyLineItems(schedule, model.LineItems, model.DiscountMode);

        _db.RecurringInvoiceSchedules.Add(schedule);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Recurring schedule created.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var schedule = await _db.RecurringInvoiceSchedules.Include(s => s.LineItems).FirstOrDefaultAsync(s => s.Id == id && s.AgentUserId == AgentId);
        if (schedule == null) return NotFound();

        await LoadClientsAsync();
        return View(new RecurringInvoiceEditViewModel
        {
            Id = schedule.Id,
            ClientId = schedule.ClientId,
            Frequency = schedule.Frequency,
            NextRunDate = schedule.NextRunDate,
            DueInDays = schedule.DueInDays,
            Notes = schedule.Notes,
            // 566: the discount as it was saved -- on the whole invoice, on the items, or none.
            DiscountMode = schedule.DiscountType != InvoiceDiscountType.None && schedule.DiscountValue > 0 ? ClientInvoiceMath.ModeInvoice
                : schedule.LineItems.Any(l => l.DiscountType != InvoiceDiscountType.None && l.DiscountValue > 0) ? ClientInvoiceMath.ModeLines : ClientInvoiceMath.ModeNone,
            DiscountType = schedule.DiscountType == InvoiceDiscountType.None ? InvoiceDiscountType.Percent : schedule.DiscountType,
            DiscountValue = schedule.DiscountType == InvoiceDiscountType.None ? 0m : schedule.DiscountValue,
            LineItems = schedule.LineItems.OrderBy(l => l.SortOrder).Select(l => new ClientInvoiceLineItemInputModel
            {
                Description = l.Description,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                IsTaxable = l.IsTaxable,
                DiscountType = l.DiscountType == InvoiceDiscountType.None ? InvoiceDiscountType.Percent : l.DiscountType,
                DiscountValue = l.DiscountType == InvoiceDiscountType.None ? 0m : l.DiscountValue
            }).ToList()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, RecurringInvoiceEditViewModel model)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var schedule = await _db.RecurringInvoiceSchedules.Include(s => s.LineItems).FirstOrDefaultAsync(s => s.Id == id && s.AgentUserId == AgentId);
        if (schedule == null) return NotFound();

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == model.ClientId && c.AgentUserId == AgentId);
        if (client == null)
        {
            ModelState.AddModelError(nameof(model.ClientId), "Choose a client.");
        }
        if (model.LineItems.Count == 0 || model.LineItems.All(l => string.IsNullOrWhiteSpace(l.Description)))
        {
            ModelState.AddModelError(string.Empty, "Add at least one line item.");
        }
        if (!ModelState.IsValid)
        {
            await LoadClientsAsync();
            model.Id = id;
            return View(model);
        }

        schedule.ClientId = model.ClientId;
        schedule.Frequency = model.Frequency;
        schedule.NextRunDate = model.NextRunDate;
        schedule.DueInDays = model.DueInDays;
        schedule.Notes = model.Notes?.Trim();
        schedule.UpdatedAt = DateTime.UtcNow;

        _db.RecurringInvoiceLineItems.RemoveRange(schedule.LineItems);
        schedule.LineItems.Clear();
        ApplyDiscount(schedule, model);
        ApplyLineItems(schedule, model.LineItems, model.DiscountMode);

        await _db.SaveChangesAsync();

        TempData["Success"] = "Recurring schedule updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Pause(int id)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var schedule = await _db.RecurringInvoiceSchedules.FirstOrDefaultAsync(s => s.Id == id && s.AgentUserId == AgentId);
        if (schedule == null) return NotFound();

        schedule.IsActive = false;
        schedule.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Schedule paused.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Resume(int id)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var schedule = await _db.RecurringInvoiceSchedules.FirstOrDefaultAsync(s => s.Id == id && s.AgentUserId == AgentId);
        if (schedule == null) return NotFound();

        schedule.IsActive = true;
        schedule.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Schedule resumed.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var gate = await RequireClientInvoicingAccessAsync();
        if (gate != null) return gate;

        var schedule = await _db.RecurringInvoiceSchedules.FirstOrDefaultAsync(s => s.Id == id && s.AgentUserId == AgentId);
        if (schedule == null) return NotFound();

        // Delete the line items EXPLICITLY, by query -- not through schedule.LineItems, which is not
        // loaded on this path (no Include above), so EF's client-side cascade never fires.
        //
        // There is no FK on RecurringInvoiceLineItems.RecurringInvoiceScheduleId (see
        // SchemaIntegrityReporter.KnownUnenforced), so nothing else removes them either. Left behind,
        // they are permanently unerasable: both AgentDataEraser and ClientDataEraser reach them
        // through the schedule that this line just deleted. Same defect shape as the custom-form
        // submission answers -- an eraser predicate anchored on a parent the agent's own UI can
        // destroy first.
        var lineItems = await _db.RecurringInvoiceLineItems
            .Where(li => li.RecurringInvoiceScheduleId == schedule.Id)
            .ToListAsync();
        _db.RecurringInvoiceLineItems.RemoveRange(lineItems);

        _db.RecurringInvoiceSchedules.Remove(schedule);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Recurring schedule deleted.";
        return RedirectToAction(nameof(Index));
    }

    private async Task LoadClientsAsync()
    {
        var clients = await _db.Clients.AsNoTracking().Where(c => c.AgentUserId == AgentId).OrderBy(c => c.LastName).ThenBy(c => c.FirstName).ToListAsync();
        ViewBag.Clients = clients;
        // 566: the shared lines editor shows what each invoice will come to, so it needs each client's
        // tax rule -- the one the job applies when it makes the invoice.
        var business = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        var rates = await _db.ProvinceTaxRates.AsNoTracking().Where(t => t.IsActive).ToListAsync();
        ViewBag.TaxByClient = clients.ToDictionary(c => c.Id, c => ClientInvoiceTaxRules.Resolve(c, business, rates));
        // 570: the editor reminds a business that charges tax and has no GST/HST number on its Profile.
        ViewBag.TaxNumberMissing = string.IsNullOrWhiteSpace(business?.TaxRegistrationNumber);
        // 568: what this business has invoiced before, offered in the editor as a description is typed.
        ViewBag.RememberedItems = await IPRO.Web.Infrastructure.RememberedInvoiceItems.ForAgentAsync(_db, AgentId);
    }

    // 566: one discount on the whole of each invoice, when that is where the editor says the discount is.
    private static void ApplyDiscount(RecurringInvoiceSchedule schedule, RecurringInvoiceEditViewModel model)
    {
        var typed = model.DiscountValue ?? 0m;
        var onInvoice = ClientInvoiceMath.CleanMode(model.DiscountMode) == ClientInvoiceMath.ModeInvoice && typed > 0 && model.DiscountType != InvoiceDiscountType.None;
        schedule.DiscountType = onInvoice ? model.DiscountType : InvoiceDiscountType.None;
        schedule.DiscountValue = !onInvoice ? 0m : model.DiscountType == InvoiceDiscountType.Percent ? Math.Min(typed, 100m) : typed;
    }

    private static void ApplyLineItems(RecurringInvoiceSchedule schedule, List<ClientInvoiceLineItemInputModel> lineItems, string? discountMode = null)
    {
        var onLines = ClientInvoiceMath.CleanMode(discountMode) == ClientInvoiceMath.ModeLines;
        var sortOrder = 0;
        foreach (var item in lineItems.Where(l => !string.IsNullOrWhiteSpace(l.Description)))
        {
            var typed = item.DiscountValue ?? 0m;
            var discounted = onLines && typed > 0 && item.DiscountType != InvoiceDiscountType.None;
            schedule.LineItems.Add(new RecurringInvoiceLineItem
            {
                Description = item.Description!.Trim(),
                Quantity = item.Quantity <= 0 ? 1 : item.Quantity,
                UnitPrice = item.UnitPrice,
                IsTaxable = item.IsTaxable,   // 563: each invoice the schedule makes keeps the line's tick
                // 566: and the line's discount, worked out on each invoice as it is made.
                DiscountType = discounted ? item.DiscountType : InvoiceDiscountType.None,
                DiscountValue = !discounted ? 0m : item.DiscountType == InvoiceDiscountType.Percent ? Math.Min(typed, 100m) : typed,
                SortOrder = sortOrder++
            });
        }
    }

    private async Task<IActionResult?> RequireClientInvoicingAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.ClientInvoicing);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
