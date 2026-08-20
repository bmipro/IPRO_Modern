using System.Security.Claims;
using IPRO.Admin.Models;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

// SuperAdmin management for the Forms starter template library -- the agent-side equivalent of
// WebsiteStarterArticlesController. Adopting a template (Forms/AdoptTemplate in IPRO.Web) copies it
// into a real, independent WebsiteForm the agent owns; editing or deleting a master row here never
// touches an agent's already-adopted copy.
// A5-M-STARTER (2026-08-20): SuperAdmin-only, matching the template and e-card libraries --
// content written here lands on EVERY agent's public site, which is not day-to-day support work.
[Authorize(Policy = "SuperAdmin")]
public class WebsiteStarterFormsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IAdminAuditLogService _auditLog;

    public WebsiteStarterFormsController(IPRODbContext db, IAdminAuditLogService auditLog)
    {
        _db = db;
        _auditLog = auditLog;
    }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public async Task<IActionResult> Index()
    {
        var forms = await _db.WebsiteStarterForms.AsNoTracking()
            .OrderBy(f => f.BusinessType).ThenBy(f => f.SortOrder)
            .ToListAsync();
        return View(forms);
    }

    public IActionResult Create() => View("Edit", new StarterFormBuilderViewModel
    {
        BusinessType = "All",
        IsActive = true,
        Fields = new List<StarterFormFieldInput> { new() { FieldType = WebsiteFormFieldTypes.Text } }
    });

    public async Task<IActionResult> Edit(int id)
    {
        var form = await _db.WebsiteStarterForms.FirstOrDefaultAsync(f => f.Id == id);
        if (form == null) return NotFound();

        var fields = await _db.WebsiteStarterFormFields.Where(f => f.WebsiteStarterFormId == id).OrderBy(f => f.SortOrder).ToListAsync();
        var fieldIds = fields.Select(f => f.Id).ToList();
        var options = await _db.WebsiteStarterFormFieldOptions.Where(o => fieldIds.Contains(o.WebsiteStarterFormFieldId)).OrderBy(o => o.SortOrder).ToListAsync();

        var model = new StarterFormBuilderViewModel
        {
            Id = form.Id,
            BusinessType = form.BusinessType,
            Title = form.Title,
            Description = form.Description,
            SubmitButtonText = form.SubmitButtonText,
            SuccessMessage = form.SuccessMessage,
            IsActive = form.IsActive,
            SortOrder = form.SortOrder,
            Fields = fields.Select(f => new StarterFormFieldInput
            {
                FieldType = f.FieldType,
                Label = f.Label,
                Placeholder = f.Placeholder,
                HelpText = f.HelpText,
                IsRequired = f.IsRequired,
                Options = options.Where(o => o.WebsiteStarterFormFieldId == f.Id).Select(o => o.Text).ToList()
            }).ToList()
        };
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(StarterFormBuilderViewModel model)
    {
        model.BusinessType = string.IsNullOrWhiteSpace(model.BusinessType) ? "All" : model.BusinessType.Trim();
        model.Title = model.Title?.Trim() ?? string.Empty;
        model.Description = model.Description?.Trim() ?? string.Empty;
        model.SubmitButtonText = string.IsNullOrWhiteSpace(model.SubmitButtonText) ? "Submit" : model.SubmitButtonText.Trim();
        model.SuccessMessage = string.IsNullOrWhiteSpace(model.SuccessMessage) ? "Thank you. Your response was sent." : model.SuccessMessage.Trim();

        if (!ValidateBuilder(model)) return View(model);

        var isNew = model.Id == 0;
        int formId;
        if (isNew)
        {
            var form = new WebsiteStarterForm
            {
                BusinessType = model.BusinessType,
                Title = model.Title,
                Description = model.Description,
                SubmitButtonText = model.SubmitButtonText,
                SuccessMessage = model.SuccessMessage,
                IsActive = model.IsActive,
                SortOrder = model.SortOrder,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.WebsiteStarterForms.Add(form);
            await _db.SaveChangesAsync();
            formId = form.Id;
        }
        else
        {
            var existing = await _db.WebsiteStarterForms.FirstOrDefaultAsync(f => f.Id == model.Id);
            if (existing == null) return NotFound();

            existing.BusinessType = model.BusinessType;
            existing.Title = model.Title;
            existing.Description = model.Description;
            existing.SubmitButtonText = model.SubmitButtonText;
            existing.SuccessMessage = model.SuccessMessage;
            existing.IsActive = model.IsActive;
            existing.SortOrder = model.SortOrder;
            existing.UpdatedAt = DateTime.UtcNow;

            var existingFields = await _db.WebsiteStarterFormFields.Where(f => f.WebsiteStarterFormId == existing.Id).ToListAsync();
            var existingFieldIds = existingFields.Select(f => f.Id).ToList();
            var existingOptions = await _db.WebsiteStarterFormFieldOptions.Where(o => existingFieldIds.Contains(o.WebsiteStarterFormFieldId)).ToListAsync();
            _db.WebsiteStarterFormFieldOptions.RemoveRange(existingOptions);
            _db.WebsiteStarterFormFields.RemoveRange(existingFields);
            await _db.SaveChangesAsync();

            formId = existing.Id;
        }

        await SaveFieldsAsync(formId, model);

        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername,
            isNew ? "StarterFormCreate" : "StarterFormEdit",
            $"Starter form '{model.Title}' ({model.BusinessType}) {(isNew ? "created" : "updated")}");
        TempData["Success"] = "Starter form saved. New adoptions will see it; agents who already adopted a copy keep what they have.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(int id) => await SetActiveAsync(id, false);

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(int id) => await SetActiveAsync(id, true);

    private async Task<IActionResult> SetActiveAsync(int id, bool active)
    {
        var form = await _db.WebsiteStarterForms.FirstOrDefaultAsync(f => f.Id == id);
        if (form == null) return NotFound();
        form.IsActive = active;
        form.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername,
            active ? "StarterFormRestore" : "StarterFormDeactivate",
            $"Starter form '{form.Title}' {(active ? "restored" : "deactivated")}");
        TempData["Success"] = active
            ? $"{form.Title} can be adopted by agents again."
            : $"{form.Title} is no longer offered to agents. Anyone who already adopted a copy keeps it.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var form = await _db.WebsiteStarterForms.FirstOrDefaultAsync(f => f.Id == id);
        if (form == null) return NotFound();

        var fields = await _db.WebsiteStarterFormFields.Where(f => f.WebsiteStarterFormId == id).ToListAsync();
        var fieldIds = fields.Select(f => f.Id).ToList();
        var options = await _db.WebsiteStarterFormFieldOptions.Where(o => fieldIds.Contains(o.WebsiteStarterFormFieldId)).ToListAsync();

        _db.WebsiteStarterFormFieldOptions.RemoveRange(options);
        _db.WebsiteStarterFormFields.RemoveRange(fields);
        _db.WebsiteStarterForms.Remove(form);
        await _db.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "StarterFormDelete",
            $"Starter form '{form.Title}' ({form.BusinessType}) permanently deleted");
        TempData["Success"] = $"{form.Title} was deleted. Agents who already adopted a copy are unaffected.";
        return RedirectToAction(nameof(Index));
    }

    private async Task SaveFieldsAsync(int formId, StarterFormBuilderViewModel model)
    {
        var sortOrder = 0;
        foreach (var fieldInput in model.Fields)
        {
            var fieldType = WebsiteFormFieldTypes.All.Contains(fieldInput.FieldType) ? fieldInput.FieldType : WebsiteFormFieldTypes.Text;
            var label = fieldInput.Label?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(label)) continue;

            var field = new WebsiteStarterFormField
            {
                WebsiteStarterFormId = formId,
                FieldType = fieldType,
                Label = label,
                Placeholder = fieldInput.Placeholder?.Trim() ?? string.Empty,
                HelpText = fieldInput.HelpText?.Trim() ?? string.Empty,
                IsRequired = fieldType == WebsiteFormFieldTypes.Section ? false : fieldInput.IsRequired,
                SortOrder = sortOrder++
            };
            _db.WebsiteStarterFormFields.Add(field);
            await _db.SaveChangesAsync();

            if (!WebsiteFormFieldTypes.SupportsOptions(fieldType)) continue;

            var validOptions = (fieldInput.Options ?? new List<string>())
                .Select(o => o?.Trim() ?? string.Empty)
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .ToList();
            var optionOrder = 0;
            foreach (var optionText in validOptions)
            {
                _db.WebsiteStarterFormFieldOptions.Add(new WebsiteStarterFormFieldOption { WebsiteStarterFormFieldId = field.Id, Text = optionText, SortOrder = optionOrder++ });
            }
            await _db.SaveChangesAsync();
        }
    }

    private bool ValidateBuilder(StarterFormBuilderViewModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Title))
        {
            ModelState.AddModelError(nameof(model.Title), "Form title is required.");
        }

        var fields = model.Fields ?? new List<StarterFormFieldInput>();
        var nonSectionCount = 0;
        foreach (var field in fields)
        {
            var fieldType = WebsiteFormFieldTypes.All.Contains(field.FieldType) ? field.FieldType : WebsiteFormFieldTypes.Text;
            var label = field.Label?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(label)) continue;

            if (fieldType != WebsiteFormFieldTypes.Section) nonSectionCount++;

            if (WebsiteFormFieldTypes.SupportsOptions(fieldType))
            {
                var validOptionCount = (field.Options ?? new List<string>()).Count(o => !string.IsNullOrWhiteSpace(o));
                if (validOptionCount < 2)
                {
                    ModelState.AddModelError("", $"\"{label}\" needs at least 2 answer options.");
                }
            }
        }

        if (nonSectionCount == 0)
        {
            ModelState.AddModelError("", "Add at least one field (a section header alone isn't a usable form).");
        }

        return ModelState.IsValid;
    }
}
