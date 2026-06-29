using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize]
public class PackagesController : Controller
{
    private readonly IUnitOfWork _uow;
    public PackagesController(IUnitOfWork uow) => _uow = uow;

    public async Task<IActionResult> Index() => View(await _uow.BillingRules.GetAllAsync());
    public IActionResult Create() => View(new BillingRule());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(BillingRule model)
    {
        if (!ModelState.IsValid) return View(model);
        model.IsActive = true;
        await _uow.BillingRules.AddAsync(model);
        await _uow.SaveChangesAsync();
        TempData["Success"] = "Package created.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var rule = await _uow.BillingRules.GetByIdAsync(id);
        if (rule == null) return NotFound();
        return View(rule);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(BillingRule model)
    {
        if (!ModelState.IsValid) return View(model);
        _uow.BillingRules.Update(model);
        await _uow.SaveChangesAsync();
        TempData["Success"] = "Package updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        var rule = await _uow.BillingRules.GetByIdAsync(id);
        if (rule == null) return NotFound();
        rule.IsActive = !rule.IsActive;
        _uow.BillingRules.Update(rule);
        await _uow.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }
}
