using System.Security.Claims;
using System.Text.RegularExpressions;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

// SuperAdmin management for the starter letters agents pick from.
//
// Unlike card designs, a template here is only ever a seed: ELetter copies Subject and Body at
// create time, so editing one changes what the *next* agent starts from and never rewrites a
// letter already written or sent. That also means a template can safely be retired -- nothing
// downstream depends on it beyond a label in the agent's list.
[Authorize(Policy = "SuperAdmin")]
public class ELetterTemplatesController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IAdminAuditLogService _auditLog;

    public ELetterTemplatesController(IPRODbContext db, IAdminAuditLogService auditLog)
    {
        _db = db;
        _auditLog = auditLog;
    }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public async Task<IActionResult> Index()
    {
        var templates = await _db.ELetterTemplates.AsNoTracking()
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToListAsync();
        return View(templates);
    }

    public IActionResult Create()
    {
        ViewBag.MergeFields = MergeFieldResolver.AvailableFields;
        return View("Edit", new ELetterTemplate { IsActive = true, SortOrder = 100 });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var template = await _db.ELetterTemplates.FirstOrDefaultAsync(t => t.Id == id);
        if (template == null) return NotFound();
        ViewBag.MergeFields = MergeFieldResolver.AvailableFields;
        return View(template);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ELetterTemplate model)
    {
        model.Name = model.Name?.Trim() ?? string.Empty;
        model.Description = model.Description?.Trim() ?? string.Empty;
        model.Subject = model.Subject?.Trim() ?? string.Empty;
        model.Body = model.Body?.Trim() ?? string.Empty;
        model.Key = NormaliseKey(model.Key, model.Name);

        if (string.IsNullOrWhiteSpace(model.Name))
            ModelState.AddModelError(nameof(model.Name), "Template name is required.");
        if (string.IsNullOrWhiteSpace(model.Subject))
            ModelState.AddModelError(nameof(model.Subject), "Subject line is required.");
        if (string.IsNullOrWhiteSpace(model.Body))
            ModelState.AddModelError(nameof(model.Body), "Letter body is required.");
        if (string.IsNullOrWhiteSpace(model.Key))
            ModelState.AddModelError(nameof(model.Key), "Key is required.");

        if (await _db.ELetterTemplates.AnyAsync(t => t.Key == model.Key && t.Id != model.Id))
            ModelState.AddModelError(nameof(model.Key), "Another template already uses that key.");

        // A token the resolver doesn't know would reach the client as literal "[Advisor Mobile]".
        var unknown = UnknownMergeTokens($"{model.Subject}\n{model.Body}");
        if (unknown.Count > 0)
            ModelState.AddModelError(nameof(model.Body),
                $"These look like merge fields but aren't recognised, and would be sent to clients as-is: {string.Join(", ", unknown)}");

        if (!ModelState.IsValid)
        {
            ViewBag.MergeFields = MergeFieldResolver.AvailableFields;
            return View(model);
        }

        var isNew = model.Id == 0;
        if (isNew)
        {
            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;
            _db.ELetterTemplates.Add(model);
        }
        else
        {
            var existing = await _db.ELetterTemplates.FirstOrDefaultAsync(t => t.Id == model.Id);
            if (existing == null) return NotFound();

            existing.Key = model.Key;
            existing.Name = model.Name;
            existing.Description = model.Description;
            existing.Subject = model.Subject;
            existing.Body = model.Body;
            existing.IsActive = model.IsActive;
            existing.SortOrder = model.SortOrder;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername,
            isNew ? "ELetterTemplateCreate" : "ELetterTemplateEdit",
            $"E-letter template '{model.Name}' ({model.Key}) {(isNew ? "created" : "updated")}");
        TempData["Success"] = "E-letter template saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(int id) => await SetActiveAsync(id, false);

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(int id) => await SetActiveAsync(id, true);

    private async Task<IActionResult> SetActiveAsync(int id, bool active)
    {
        var template = await _db.ELetterTemplates.FirstOrDefaultAsync(t => t.Id == id);
        if (template == null) return NotFound();

        template.IsActive = active;
        template.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername,
            active ? "ELetterTemplateRestore" : "ELetterTemplateDeactivate",
            $"E-letter template '{template.Name}' {(active ? "restored" : "deactivated")}");
        TempData["Success"] = active
            ? $"{template.Name} is available to agents again."
            : $"{template.Name} is no longer offered to agents.";
        return RedirectToAction(nameof(Index));
    }

    private static List<string> UnknownMergeTokens(string text)
    {
        var known = MergeFieldResolver.AvailableFields
            .Select(f => f.Token.Trim('[', ']').Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Regex.Matches(text ?? string.Empty, @"\[\s*([A-Za-z]+(?:\s+[A-Za-z]+)*)\s*\]")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(name => !known.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => $"[{name}]")
            .ToList();
    }

    private static string NormaliseKey(string? key, string name)
    {
        var source = string.IsNullOrWhiteSpace(key) ? name : key;
        var slug = Regex.Replace(source.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 80 ? slug[..80] : slug;
    }
}
