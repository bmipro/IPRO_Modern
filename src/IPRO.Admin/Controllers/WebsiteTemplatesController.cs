using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class WebsiteTemplatesController : Controller
{
    private readonly IUnitOfWork _uow;

    public WebsiteTemplatesController(IUnitOfWork uow)
    {
        _uow = uow;
    }

    public async Task<IActionResult> Index()
    {
        var templates = await _uow.WebsiteTemplates.GetAllAsync();
        return View(templates.OrderByDescending(t => t.IsDefault).ThenBy(t => t.Name));
    }

    public async Task<IActionResult> Create()
    {
        return View("Edit", new WebsiteTemplate
        {
            TemplateKey = "new-template",
            IsActive = true,
            LayoutJson = "{}"
        });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var template = await _uow.WebsiteTemplates.GetByIdAsync(id);
        if (template == null) return NotFound();
        return View(template);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(WebsiteTemplate model)
    {
        model.TemplateKey = NormalizeTemplateKey(model.TemplateKey);
        model.Name = model.Name?.Trim() ?? string.Empty;
        model.Description = model.Description?.Trim() ?? string.Empty;
        model.BusinessType = model.BusinessType?.Trim() ?? string.Empty;
        model.PreviewImageUrl = model.PreviewImageUrl?.Trim() ?? string.Empty;
        model.LayoutJson = string.IsNullOrWhiteSpace(model.LayoutJson) ? "{}" : model.LayoutJson.Trim();

        if (string.IsNullOrWhiteSpace(model.TemplateKey))
        {
            ModelState.AddModelError(nameof(model.TemplateKey), "Template key is required.");
        }

        if (string.IsNullOrWhiteSpace(model.Name))
        {
            ModelState.AddModelError(nameof(model.Name), "Template name is required.");
        }

        var allTemplates = await _uow.WebsiteTemplates.GetAllAsync();
        if (allTemplates.Any(t => t.Id != model.Id && string.Equals(t.TemplateKey, model.TemplateKey, StringComparison.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError(nameof(model.TemplateKey), "Another template already uses this key.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        if (model.Id == 0)
        {
            model.CreatedAt = DateTime.UtcNow;
            await _uow.WebsiteTemplates.AddAsync(model);
        }
        else
        {
            var existing = await _uow.WebsiteTemplates.GetByIdAsync(model.Id);
            if (existing == null) return NotFound();

            existing.TemplateKey = model.TemplateKey;
            existing.Name = model.Name;
            existing.Description = model.Description;
            existing.BusinessType = model.BusinessType;
            existing.PreviewImageUrl = model.PreviewImageUrl;
            existing.LayoutJson = model.LayoutJson;
            existing.IsActive = model.IsActive;
            existing.IsDefault = model.IsDefault;
            _uow.WebsiteTemplates.Update(existing);
        }

        if (model.IsDefault)
        {
            await ClearOtherDefaultsAsync(model.Id, model.TemplateKey);
        }

        await _uow.SaveChangesAsync();
        TempData["Success"] = "Website template saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefault(int id)
    {
        var templates = await _uow.WebsiteTemplates.GetAllAsync();
        var selected = templates.FirstOrDefault(t => t.Id == id);
        if (selected == null) return NotFound();

        foreach (var template in templates)
        {
            template.IsDefault = template.Id == id;
            _uow.WebsiteTemplates.Update(template);
        }

        await _uow.SaveChangesAsync();
        TempData["Success"] = $"{selected.Name} is now the default website template.";
        return RedirectToAction(nameof(Index));
    }

    private async Task ClearOtherDefaultsAsync(int modelId, string templateKey)
    {
        var templates = await _uow.WebsiteTemplates.GetAllAsync();
        foreach (var template in templates.Where(t => t.Id != modelId && !string.Equals(t.TemplateKey, templateKey, StringComparison.OrdinalIgnoreCase)))
        {
            if (!template.IsDefault) continue;
            template.IsDefault = false;
            _uow.WebsiteTemplates.Update(template);
        }
    }

    private static string NormalizeTemplateKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
