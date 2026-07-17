using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class NewsletterTemplatesController : Controller
{
    private readonly IUnitOfWork _uow;

    public NewsletterTemplatesController(IUnitOfWork uow)
    {
        _uow = uow;
    }

    public async Task<IActionResult> Index()
    {
        var templates = await _uow.NewsLetterTemplates.GetAllAsync();
        return View(templates.OrderBy(t => t.SortOrder).ThenBy(t => t.Name));
    }

    public IActionResult Create()
    {
        return View("Edit", new NewsLetterTemplate { IsActive = true });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var template = await _uow.NewsLetterTemplates.GetByIdAsync(id);
        if (template == null) return NotFound();
        return View(template);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(NewsLetterTemplate model)
    {
        model.Name = model.Name?.Trim() ?? string.Empty;
        model.Description = model.Description?.Trim() ?? string.Empty;
        model.Subject = model.Subject?.Trim() ?? string.Empty;
        model.HtmlBody = model.HtmlBody?.Trim() ?? string.Empty;
        model.TextBody = model.TextBody?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(model.Name))
        {
            ModelState.AddModelError(nameof(model.Name), "Template name is required.");
        }

        if (string.IsNullOrWhiteSpace(model.Subject))
        {
            ModelState.AddModelError(nameof(model.Subject), "Subject line is required.");
        }

        if (string.IsNullOrWhiteSpace(model.HtmlBody) && string.IsNullOrWhiteSpace(model.TextBody))
        {
            ModelState.AddModelError(nameof(model.HtmlBody), "Provide either an HTML body or a plain text body.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        if (model.Id == 0)
        {
            model.CreatedAt = DateTime.UtcNow;
            await _uow.NewsLetterTemplates.AddAsync(model);
        }
        else
        {
            var existing = await _uow.NewsLetterTemplates.GetByIdAsync(model.Id);
            if (existing == null) return NotFound();

            existing.Name = model.Name;
            existing.Description = model.Description;
            existing.Subject = model.Subject;
            existing.HtmlBody = model.HtmlBody;
            existing.TextBody = model.TextBody;
            existing.IsActive = model.IsActive;
            existing.SortOrder = model.SortOrder;
            _uow.NewsLetterTemplates.Update(existing);
        }

        await _uow.SaveChangesAsync();
        TempData["Success"] = "Newsletter template saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(int id)
    {
        var template = await _uow.NewsLetterTemplates.GetByIdAsync(id);
        if (template == null) return NotFound();
        template.IsActive = false;
        _uow.NewsLetterTemplates.Update(template);
        await _uow.SaveChangesAsync();
        TempData["Success"] = $"{template.Name} is no longer offered to agents.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(int id)
    {
        var template = await _uow.NewsLetterTemplates.GetByIdAsync(id);
        if (template == null) return NotFound();
        template.IsActive = true;
        _uow.NewsLetterTemplates.Update(template);
        await _uow.SaveChangesAsync();
        TempData["Success"] = $"{template.Name} is available to agents again.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var template = await _uow.NewsLetterTemplates.GetByIdAsync(id);
        if (template == null) return NotFound();
        _uow.NewsLetterTemplates.Remove(template);
        await _uow.SaveChangesAsync();
        TempData["Success"] = $"{template.Name} was deleted.";
        return RedirectToAction(nameof(Index));
    }
}
