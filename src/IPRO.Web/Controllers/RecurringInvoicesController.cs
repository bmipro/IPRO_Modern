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

        ApplyLineItems(schedule, model.LineItems);

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
            LineItems = schedule.LineItems.OrderBy(l => l.SortOrder).Select(l => new ClientInvoiceLineItemInputModel
            {
                Description = l.Description,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
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
        ApplyLineItems(schedule, model.LineItems);

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

        _db.RecurringInvoiceSchedules.Remove(schedule);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Recurring schedule deleted.";
        return RedirectToAction(nameof(Index));
    }

    private async Task LoadClientsAsync()
    {
        ViewBag.Clients = await _db.Clients.AsNoTracking().Where(c => c.AgentUserId == AgentId).OrderBy(c => c.LastName).ThenBy(c => c.FirstName).ToListAsync();
    }

    private static void ApplyLineItems(RecurringInvoiceSchedule schedule, List<ClientInvoiceLineItemInputModel> lineItems)
    {
        var sortOrder = 0;
        foreach (var item in lineItems.Where(l => !string.IsNullOrWhiteSpace(l.Description)))
        {
            schedule.LineItems.Add(new RecurringInvoiceLineItem
            {
                Description = item.Description!.Trim(),
                Quantity = item.Quantity <= 0 ? 1 : item.Quantity,
                UnitPrice = item.UnitPrice,
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
