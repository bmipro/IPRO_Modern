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
public class FormsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public FormsController(IPRODbContext db, IPackageEntitlementService entitlements)
    {
        _db = db;
        _entitlements = entitlements;
    }

    public async Task<IActionResult> Index()
    {
        var gate = await RequireFormAccessAsync();
        if (gate != null) return gate;

        var forms = await _db.WebsiteForms
            .Where(f => f.AgentUserId == AgentId)
            .OrderByDescending(f => f.UpdatedAt)
            .ToListAsync();
        return View(forms);
    }

    public async Task<IActionResult> Create()
    {
        var gate = await RequireFormAccessAsync();
        if (gate != null) return gate;

        return View(new FormBuilderViewModel
        {
            Fields = new List<FormFieldInput> { new() { FieldType = WebsiteFormFieldTypes.Text } }
        });
    }

    public async Task<IActionResult> Templates()
    {
        var gate = await RequireFormAccessAsync();
        if (gate != null) return gate;

        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        var businessType = agent?.BusinessType ?? "All";

        var templates = await _db.WebsiteStarterForms.AsNoTracking()
            .Where(f => f.IsActive && (f.BusinessType == businessType || f.BusinessType == "All"))
            .OrderBy(f => f.BusinessType == "All" ? 1 : 0).ThenBy(f => f.SortOrder)
            .ToListAsync();
        var templateIds = templates.Select(t => t.Id).ToList();
        var fields = await _db.WebsiteStarterFormFields.AsNoTracking()
            .Where(f => templateIds.Contains(f.WebsiteStarterFormId))
            .OrderBy(f => f.SortOrder)
            .ToListAsync();

        ViewBag.FieldsByForm = fields.GroupBy(f => f.WebsiteStarterFormId).ToDictionary(g => g.Key, g => g.ToList());
        return View(templates);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AdoptTemplate(int starterFormId)
    {
        var gate = await RequireFormAccessAsync();
        if (gate != null) return gate;

        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        var businessType = agent?.BusinessType ?? "All";

        // Re-checks visibility server-side rather than trusting the id came from the gallery this
        // agent was actually shown -- defends against adopting a different vertical's template by
        // guessing an id.
        var template = await _db.WebsiteStarterForms.FirstOrDefaultAsync(f => f.Id == starterFormId
            && f.IsActive && (f.BusinessType == businessType || f.BusinessType == "All"));
        if (template == null) return NotFound();

        var templateFields = await _db.WebsiteStarterFormFields.Where(f => f.WebsiteStarterFormId == template.Id).OrderBy(f => f.SortOrder).ToListAsync();
        var templateFieldIds = templateFields.Select(f => f.Id).ToList();
        var templateOptions = await _db.WebsiteStarterFormFieldOptions.Where(o => templateFieldIds.Contains(o.WebsiteStarterFormFieldId)).OrderBy(o => o.SortOrder).ToListAsync();

        var form = new WebsiteForm
        {
            AgentUserId = AgentId,
            Title = template.Title,
            Description = template.Description,
            SubmitButtonText = template.SubmitButtonText,
            SuccessMessage = template.SuccessMessage,
            IsActive = true
        };
        _db.WebsiteForms.Add(form);
        await _db.SaveChangesAsync();

        foreach (var templateField in templateFields)
        {
            var field = new WebsiteFormField
            {
                WebsiteFormId = form.Id,
                FieldType = templateField.FieldType,
                Label = templateField.Label,
                Placeholder = templateField.Placeholder,
                HelpText = templateField.HelpText,
                IsRequired = templateField.IsRequired,
                SortOrder = templateField.SortOrder
            };
            _db.WebsiteFormFields.Add(field);
            await _db.SaveChangesAsync();

            foreach (var option in templateOptions.Where(o => o.WebsiteStarterFormFieldId == templateField.Id))
            {
                _db.WebsiteFormFieldOptions.Add(new WebsiteFormFieldOption { WebsiteFormFieldId = field.Id, Text = option.Text, SortOrder = option.SortOrder });
            }
            await _db.SaveChangesAsync();
        }

        TempData["Success"] = $"Template adopted as '{form.Title}'. Use it as-is, or make changes below.";
        return RedirectToAction(nameof(Edit), new { id = form.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(FormBuilderViewModel model)
    {
        var gate = await RequireFormAccessAsync();
        if (gate != null) return gate;

        if (!ValidateBuilder(model))
        {
            return View(model);
        }

        var form = new WebsiteForm
        {
            AgentUserId = AgentId,
            Title = model.Title.Trim(),
            Description = model.Description.Trim(),
            SubmitButtonText = string.IsNullOrWhiteSpace(model.SubmitButtonText) ? "Submit" : model.SubmitButtonText.Trim(),
            SuccessMessage = string.IsNullOrWhiteSpace(model.SuccessMessage) ? "Thank you. Your response was sent." : model.SuccessMessage.Trim()
        };
        _db.WebsiteForms.Add(form);
        await _db.SaveChangesAsync();

        await SaveFieldsAsync(form.Id, model);

        TempData["Success"] = "Form saved.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Preview(int id)
    {
        var gate = await RequireFormAccessAsync();
        if (gate != null) return gate;

        var formData = await IPRO.Web.Infrastructure.PublicFormBuilder.BuildForFormAsync(_db, id, AgentId);
        if (formData == null) return NotFound();

        var website = await _db.AgentWebsites.Include(w => w.AgentUser).FirstOrDefaultAsync(w => w.AgentUserId == AgentId);
        ViewBag.Website = website;
        ViewBag.Agent = website?.AgentUser ?? await _db.AgentUsers.FirstOrDefaultAsync(a => a.Id == AgentId);
        ViewBag.IsFormPreview = true;
        return View("~/Views/PublicWebsite/StandaloneForm.cshtml", formData);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var gate = await RequireFormAccessAsync();
        if (gate != null) return gate;

        var form = await _db.WebsiteForms.FirstOrDefaultAsync(f => f.Id == id && f.AgentUserId == AgentId);
        if (form == null) return NotFound();

        var fields = await _db.WebsiteFormFields.Where(f => f.WebsiteFormId == id).OrderBy(f => f.SortOrder).ToListAsync();
        var fieldIds = fields.Select(f => f.Id).ToList();
        var options = await _db.WebsiteFormFieldOptions.Where(o => fieldIds.Contains(o.WebsiteFormFieldId)).OrderBy(o => o.SortOrder).ToListAsync();

        var model = new FormBuilderViewModel
        {
            Id = form.Id,
            Title = form.Title,
            Description = form.Description,
            SubmitButtonText = form.SubmitButtonText,
            SuccessMessage = form.SuccessMessage,
            Fields = fields.Select(f => new FormFieldInput
            {
                FieldType = f.FieldType,
                Label = f.Label,
                Placeholder = f.Placeholder,
                HelpText = f.HelpText,
                IsRequired = f.IsRequired,
                Options = options.Where(o => o.WebsiteFormFieldId == f.Id).Select(o => o.Text).ToList()
            }).ToList()
        };
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(FormBuilderViewModel model)
    {
        var gate = await RequireFormAccessAsync();
        if (gate != null) return gate;

        var form = await _db.WebsiteForms.FirstOrDefaultAsync(f => f.Id == model.Id && f.AgentUserId == AgentId);
        if (form == null) return NotFound();

        if (!ValidateBuilder(model))
        {
            return View(model);
        }

        form.Title = model.Title.Trim();
        form.Description = model.Description.Trim();
        form.SubmitButtonText = string.IsNullOrWhiteSpace(model.SubmitButtonText) ? "Submit" : model.SubmitButtonText.Trim();
        form.SuccessMessage = string.IsNullOrWhiteSpace(model.SuccessMessage) ? "Thank you. Your response was sent." : model.SuccessMessage.Trim();
        form.UpdatedAt = DateTime.UtcNow;

        var existingFields = await _db.WebsiteFormFields.Where(f => f.WebsiteFormId == form.Id).ToListAsync();
        var existingFieldIds = existingFields.Select(f => f.Id).ToList();
        var existingOptions = await _db.WebsiteFormFieldOptions.Where(o => existingFieldIds.Contains(o.WebsiteFormFieldId)).ToListAsync();
        _db.WebsiteFormFieldOptions.RemoveRange(existingOptions);
        _db.WebsiteFormFields.RemoveRange(existingFields);
        await _db.SaveChangesAsync();

        await SaveFieldsAsync(form.Id, model);

        TempData["Success"] = "Form updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var gate = await RequireFormAccessAsync();
        if (gate != null) return gate;

        var form = await _db.WebsiteForms.FirstOrDefaultAsync(f => f.Id == id && f.AgentUserId == AgentId);
        if (form == null) return NotFound();

        var fields = await _db.WebsiteFormFields.Where(f => f.WebsiteFormId == id).ToListAsync();
        var fieldIds = fields.Select(f => f.Id).ToList();
        var options = await _db.WebsiteFormFieldOptions.Where(o => fieldIds.Contains(o.WebsiteFormFieldId)).ToListAsync();

        // Website blocks pointing at this form (WebsiteFormSettings.WebsiteFormId) are left as-is -
        // PublicFormBuilder skips a block whose linked form no longer resolves, same as a deleted Poll.
        _db.WebsiteFormFieldOptions.RemoveRange(options);
        _db.WebsiteFormFields.RemoveRange(fields);
        _db.WebsiteForms.Remove(form);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Form deleted.";
        return RedirectToAction(nameof(Index));
    }

    private async Task SaveFieldsAsync(int formId, FormBuilderViewModel model)
    {
        var sortOrder = 0;
        foreach (var fieldInput in model.Fields)
        {
            var fieldType = WebsiteFormFieldTypes.All.Contains(fieldInput.FieldType) ? fieldInput.FieldType : WebsiteFormFieldTypes.Text;
            var label = fieldInput.Label?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(label)) continue;

            var field = new WebsiteFormField
            {
                WebsiteFormId = formId,
                FieldType = fieldType,
                Label = label,
                Placeholder = fieldInput.Placeholder?.Trim() ?? string.Empty,
                HelpText = fieldInput.HelpText?.Trim() ?? string.Empty,
                IsRequired = fieldType == WebsiteFormFieldTypes.Section ? false : fieldInput.IsRequired,
                SortOrder = sortOrder++
            };
            _db.WebsiteFormFields.Add(field);
            await _db.SaveChangesAsync();

            if (!WebsiteFormFieldTypes.SupportsOptions(fieldType)) continue;

            var validOptions = (fieldInput.Options ?? new List<string>())
                .Select(o => o?.Trim() ?? string.Empty)
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .ToList();
            var optionOrder = 0;
            foreach (var optionText in validOptions)
            {
                _db.WebsiteFormFieldOptions.Add(new WebsiteFormFieldOption { WebsiteFormFieldId = field.Id, Text = optionText, SortOrder = optionOrder++ });
            }
            await _db.SaveChangesAsync();
        }
    }

    private bool ValidateBuilder(FormBuilderViewModel model)
    {
        model.Title = model.Title?.Trim() ?? string.Empty;
        model.Description = model.Description?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(model.Title))
        {
            ModelState.AddModelError(nameof(model.Title), "Form title is required.");
        }

        var fields = model.Fields ?? new List<FormFieldInput>();
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

    private async Task<IActionResult?> RequireFormAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.CustomForms);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
