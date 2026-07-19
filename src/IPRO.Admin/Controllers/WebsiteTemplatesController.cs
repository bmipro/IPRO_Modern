using System.Security.Claims;
using IPRO.Admin.Models;
using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class WebsiteTemplatesController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IEmailService _email;
    private readonly ILogger<WebsiteTemplatesController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IAdminAuditLogService _auditLog;

    public WebsiteTemplatesController(IUnitOfWork uow, IEmailService email, ILogger<WebsiteTemplatesController> logger, IConfiguration configuration, IAdminAuditLogService auditLog)
    {
        _uow = uow;
        _email = email;
        _logger = logger;
        _configuration = configuration;
        _auditLog = auditLog;
    }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    public async Task<IActionResult> Index()
    {
        var usage = await BuildUsageAsync();
        return View(usage.OrderByDescending(t => t.Template.IsDefault).ThenBy(t => t.Template.Name));
    }

    public async Task<IActionResult> Create()
    {
        return View("Edit", new WebsiteTemplate
        {
            TemplateKey = "modern-professional-blue",
            Name = "Modern Professional - Blue",
            Description = "Clean modern website with a blue professional palette.",
            IsActive = true,
            LayoutJson = """
            {
              "renderer": "modern-professional",
              "accentColor": "#1457d9",
              "backgroundColor": "#f4f7fb",
              "fontFamily": "Arial, Helvetica, sans-serif",
              "heroStyle": "gradient"
            }
            """
        });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var template = await _uow.WebsiteTemplates.GetByIdAsync(id);
        if (template == null) return NotFound();

        ViewBag.Agents = (await _uow.AgentUsers.GetAllAsync())
            .OrderBy(a => a.LastName)
            .ThenBy(a => a.FirstName)
            .ToList();

        return View(template);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewOnAgent(int templateId, int agentUserId, bool useDefaults = false)
    {
        var secret = _configuration["AdminPreview:SharedSecret"];
        if (string.IsNullOrWhiteSpace(secret) || secret.StartsWith("CHANGE_THIS", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Admin preview is not configured yet. Set AdminPreview:SharedSecret (matching value in both apps) in Azure App Settings.";
            return RedirectToAction(nameof(Edit), new { id = templateId });
        }

        var token = AdminPreviewToken.Create(secret, agentUserId, templateId, useDefaults, TimeSpan.FromMinutes(10));
        var baseUrl = (_configuration["App:PublicWebsiteBaseUrl"] ?? "https://ipro-prod-web.azurewebsites.net").TrimEnd('/');
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "WebsiteTemplatePreviewOnAgent", $"Generated a signed preview of template id {templateId} on agent id {agentUserId}");
        return Redirect($"{baseUrl}/PublicWebsite/PreviewTemplateForAdmin?token={Uri.EscapeDataString(token)}");
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
        model.LayoutJson = WebsiteTemplateDesign.FromTemplate(model).ToLayoutJson();

        if (string.IsNullOrWhiteSpace(model.TemplateKey))
        {
            ModelState.AddModelError(nameof(model.TemplateKey), "Template key is required.");
        }

        if (string.IsNullOrWhiteSpace(model.Name))
        {
            ModelState.AddModelError(nameof(model.Name), "Template name is required.");
        }

        if (model.IsDefault && !model.IsActive)
        {
            ModelState.AddModelError(nameof(model.IsActive), "A default template must remain active.");
        }

        var allTemplates = await _uow.WebsiteTemplates.GetAllAsync();
        if (allTemplates.Any(t => t.Id != model.Id && string.Equals(t.TemplateKey, model.TemplateKey, StringComparison.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError(nameof(model.TemplateKey), "Another template already uses this key. Use a unique key such as modern-professional-blue or classic-sidebar-green.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var isNew = model.Id == 0;
        if (isNew)
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
            await ClearOtherDefaultsAsync(model.Id, model.TemplateKey, model.BusinessType);
        }

        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, isNew ? "WebsiteTemplateCreate" : "WebsiteTemplateEdit", $"Website template '{model.Name}' {(isNew ? "created" : "updated")}");
        TempData["Success"] = "Website template saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefault(int id)
    {
        var templates = await _uow.WebsiteTemplates.GetAllAsync();
        var selected = templates.FirstOrDefault(t => t.Id == id);
        if (selected == null) return NotFound();
        if (!selected.IsActive)
        {
            TempData["Error"] = "Restore this template before making it a default.";
            return RedirectToAction(nameof(Index));
        }

        var selectedBusinessType = selected.BusinessType?.Trim() ?? string.Empty;
        foreach (var template in templates.Where(t => string.Equals(t.BusinessType?.Trim() ?? string.Empty, selectedBusinessType, StringComparison.OrdinalIgnoreCase)))
        {
            template.IsDefault = template.Id == id;
            _uow.WebsiteTemplates.Update(template);
        }

        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "WebsiteTemplateSetDefault", $"Template '{selected.Name}' set as default for {(string.IsNullOrWhiteSpace(selectedBusinessType) ? "all businesses" : selectedBusinessType)}");
        TempData["Success"] = $"{selected.Name} is now the default website template for {(string.IsNullOrWhiteSpace(selectedBusinessType) ? "all businesses" : selectedBusinessType)}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Duplicate(int id)
    {
        var source = await _uow.WebsiteTemplates.GetByIdAsync(id);
        if (source == null) return NotFound();

        var templates = (await _uow.WebsiteTemplates.GetAllAsync()).ToList();
        var design = WebsiteTemplateDesign.FromTemplate(source);
        design.Version++;
        var baseKey = source.TemplateKey;
        var candidateKey = $"{baseKey}-v{design.Version}";
        var suffix = 2;
        while (templates.Any(t => string.Equals(t.TemplateKey, candidateKey, StringComparison.OrdinalIgnoreCase)))
        {
            candidateKey = $"{baseKey}-v{design.Version}-{suffix++}";
        }

        var duplicate = new WebsiteTemplate
        {
            TemplateKey = candidateKey,
            Name = $"{source.Name} v{design.Version}",
            Description = source.Description,
            BusinessType = source.BusinessType,
            PreviewImageUrl = source.PreviewImageUrl,
            LayoutJson = design.ToLayoutJson(),
            IsActive = false,
            IsDefault = false,
            CreatedAt = DateTime.UtcNow
        };
        await _uow.WebsiteTemplates.AddAsync(duplicate);
        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "WebsiteTemplateDuplicate", $"Template '{source.Name}' duplicated as inactive draft '{duplicate.Name}'");
        TempData["Success"] = $"{duplicate.Name} was created as an inactive draft. Review and activate it when ready.";
        return RedirectToAction(nameof(Edit), new { id = duplicate.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Retire(int id)
    {
        var template = await _uow.WebsiteTemplates.GetByIdAsync(id);
        if (template == null) return NotFound();
        if (template.IsDefault)
        {
            TempData["Error"] = "Choose another default for this business type before retiring this template.";
            return RedirectToAction(nameof(Index));
        }

        var usage = (await BuildUsageAsync()).First(t => t.Template.Id == id);
        template.IsActive = false;
        _uow.WebsiteTemplates.Update(template);
        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "WebsiteTemplateRetire", $"Template '{template.Name}' retired (in use by {usage.WebsiteCount} agent site(s))");

        if (usage.WebsiteCount > 0)
        {
            var sent = await NotifyAffectedAgentsAsync(template);
            TempData["Success"] = sent
                ? $"{template.Name} was retired. {usage.WebsiteCount} agent(s) still using it were emailed and will also see a migration notice; their websites remain online."
                : $"{template.Name} was retired. {usage.WebsiteCount} agent(s) still using it will see a migration notice; their websites remain online. The notification email could not be sent — check Email Setup.";
        }
        else
        {
            TempData["Success"] = $"{template.Name} was retired.";
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> NotifyAffectedAgentsAsync(WebsiteTemplate template)
    {
        var affectedAgentIds = (await _uow.AgentWebsites.GetAllAsync())
            .Where(w => w.TemplateId == template.Id)
            .Select(w => w.AgentUserId)
            .Distinct()
            .ToList();
        if (affectedAgentIds.Count == 0) return true;

        var recipients = (await _uow.AgentUsers.GetAllAsync())
            .Where(a => affectedAgentIds.Contains(a.Id) && !string.IsNullOrWhiteSpace(a.Email))
            .Select(a => new EmailRecipient(a.Email, $"{a.FirstName} {a.LastName}".Trim()))
            .ToList();
        if (recipients.Count == 0) return true;

        const string subject = "Your website template has been retired";
        var html = $"""
            <p>Hi there,</p>
            <p>The website template you are currently using, <strong>{System.Net.WebUtility.HtmlEncode(template.Name)}</strong>, has been retired by 247Advisers.</p>
            <p>Your website remains online and completely unchanged for now. Whenever you're ready, visit <strong>My Website</strong> in your agent portal to preview and switch to an active template. Your pages, content, and settings are kept when you switch.</p>
            <p>&mdash; 247Advisers</p>
            """;
        var text = $"The website template you are currently using, {template.Name}, has been retired. Your website remains online and unchanged. Visit My Website in your agent portal to preview and switch to an active template whenever you're ready.";

        try
        {
            return await _email.SendBulkAsync(recipients, subject, html, text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send template retirement notice for template {TemplateId} to {Count} agents.", template.Id, recipients.Count);
            return false;
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(int id)
    {
        var template = await _uow.WebsiteTemplates.GetByIdAsync(id);
        if (template == null) return NotFound();
        template.IsActive = true;
        _uow.WebsiteTemplates.Update(template);
        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "WebsiteTemplateRestore", $"Template '{template.Name}' restored");
        TempData["Success"] = $"{template.Name} is active again.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var template = await _uow.WebsiteTemplates.GetByIdAsync(id);
        if (template == null) return NotFound();

        var usage = (await BuildUsageAsync()).FirstOrDefault(t => t.Template.Id == id);
        if (usage == null)
        {
            TempData["Error"] = "Template usage could not be checked. Please try again.";
            return RedirectToAction(nameof(Index));
        }

        if (template.IsDefault)
        {
            TempData["Error"] = $"{template.Name} is the global default template. Choose another default before deleting it.";
            return RedirectToAction(nameof(Index));
        }

        if (usage.IsInUse)
        {
            var agentPreview = usage.AgentNames.Any()
                ? $" Agents using it: {string.Join(", ", usage.AgentNames.Take(5))}{(usage.AgentNames.Count > 5 ? ", ..." : "")}."
                : string.Empty;
            var packagePreview = usage.PackageNames.Any()
                ? $" Packages using it as default: {string.Join(", ", usage.PackageNames.Take(5))}{(usage.PackageNames.Count > 5 ? ", ..." : "")}."
                : string.Empty;

            TempData["Error"] =
                $"{template.Name} cannot be deleted yet. It is used by {usage.WebsiteCount} agent website(s) and {usage.PackageDefaultCount} package default(s)." +
                $"{agentPreview}{packagePreview} Move those agents/packages to another template first.";
            return RedirectToAction(nameof(Index));
        }

        _uow.WebsiteTemplates.Remove(template);
        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "WebsiteTemplateDelete", $"Template '{template.Name}' permanently deleted");
        TempData["Success"] = $"{template.Name} was deleted.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<List<WebsiteTemplateUsageViewModel>> BuildUsageAsync()
    {
        var templates = (await _uow.WebsiteTemplates.GetAllAsync()).ToList();
        var websites = (await _uow.AgentWebsites.GetAllAsync()).ToList();
        var agents = (await _uow.AgentUsers.GetAllAsync()).ToDictionary(a => a.Id);
        var packages = (await _uow.BillingRules.GetAllAsync()).ToList();

        return templates.Select(template =>
        {
            var templateWebsites = websites.Where(w => w.TemplateId == template.Id).ToList();
            var templatePackages = packages
                .Where(p => p.DefaultWebsiteTemplateId == template.Id)
                .ToList();

            return new WebsiteTemplateUsageViewModel
            {
                Template = template,
                WebsiteCount = templateWebsites.Count,
                PublishedWebsiteCount = templateWebsites.Count(w => w.IsPublished),
                PackageDefaultCount = templatePackages.Count,
                AgentNames = templateWebsites
                    .Select(w => agents.TryGetValue(w.AgentUserId, out var agent)
                        ? $"{agent.FirstName} {agent.LastName}".Trim()
                        : $"Agent #{w.AgentUserId}")
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name)
                    .ToList(),
                PackageNames = templatePackages
                    .Select(p => p.PackageName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name)
                    .ToList()
            };
        }).ToList();
    }

    private async Task ClearOtherDefaultsAsync(int modelId, string templateKey, string? selectedBusinessType)
    {
        var templates = await _uow.WebsiteTemplates.GetAllAsync();
        var businessType = selectedBusinessType?.Trim() ?? string.Empty;
        foreach (var template in templates.Where(t =>
                     t.Id != modelId &&
                     !string.Equals(t.TemplateKey, templateKey, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(t.BusinessType?.Trim() ?? string.Empty, businessType, StringComparison.OrdinalIgnoreCase)))
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
