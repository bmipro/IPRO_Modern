using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class ELettersController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly ELetterDispatcher _dispatcher;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ELettersController(IPRODbContext db, IPackageEntitlementService entitlements, ELetterDispatcher dispatcher)
    {
        _db = db;
        _entitlements = entitlements;
        _dispatcher = dispatcher;
    }

    public async Task<IActionResult> Index()
    {
        var gate = await RequireELetterAccessAsync();
        if (gate != null) return gate;

        var letters = await _db.ELetters
            .Where(l => l.AgentUserId == AgentId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();
        ViewBag.AgentTimeZone = await GetAgentTimeZoneAsync();
        // Every template, including retired ones -- the list has to label historical letters.
        ViewBag.Templates = await _db.ELetterTemplates.AsNoTracking().ToDictionaryAsync(t => t.Key);
        return View(letters);
    }

    public async Task<IActionResult> Create(string? template)
    {
        var gate = await RequireELetterAccessAsync();
        if (gate != null) return gate;

        await LoadCreateContextAsync();
        ViewBag.SelectedTemplate = await ResolveTemplateAsync(template);
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string templateKey, string subject, string body,
        int[]? clientIds, DateTime scheduledAt, bool sendNow)
    {
        var gate = await RequireELetterAccessAsync();
        if (gate != null) return gate;

        subject = subject?.Trim() ?? string.Empty;
        body = body?.Trim() ?? string.Empty;
        var selectedIds = (clientIds ?? Array.Empty<int>()).Distinct().ToList();

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body) || selectedIds.Count == 0)
        {
            TempData["Error"] = "Enter a subject and letter body, and select at least one client.";
            await LoadCreateContextAsync();
            ViewBag.SelectedTemplate = await ResolveTemplateAsync(templateKey);
            return View();
        }

        var clients = await _db.Clients
            .Where(c => c.AgentUserId == AgentId && selectedIds.Contains(c.Id) && !string.IsNullOrWhiteSpace(c.Email))
            .ToListAsync();
        if (clients.Count == 0)
        {
            TempData["Error"] = "None of the selected clients have an email address on file.";
            await LoadCreateContextAsync();
            ViewBag.SelectedTemplate = await ResolveTemplateAsync(templateKey);
            return View();
        }

        var timeZone = await GetAgentTimeZoneAsync();
        var letter = new ELetter
        {
            AgentUserId = AgentId,
            // The template is only a starting point -- Subject and Body are already copies the
            // agent has edited. The key is kept purely so the list can label the letter.
            TemplateKey = (await ResolveTemplateAsync(templateKey))?.Key ?? string.Empty,
            Subject = subject,
            Body = body,
            Status = ELetterStatuses.Scheduled,
            ScheduledAt = sendNow ? DateTime.UtcNow : AgentTimeZoneHelper.ToUtc(scheduledAt, timeZone)
        };
        _db.ELetters.Add(letter);
        await _db.SaveChangesAsync();

        var recipients = clients.Select(c => new ELetterRecipient
        {
            ELetterId = letter.Id,
            ClientId = c.Id,
            Email = c.Email.Trim().ToLowerInvariant(),
            RecipientName = $"{c.FirstName} {c.LastName}".Trim()
        }).ToList();
        letter.TotalRecipients = recipients.Count;
        _db.ELetterRecipients.AddRange(recipients);
        await _db.SaveChangesAsync();

        if (sendNow)
        {
            await _dispatcher.DispatchAsync(letter.Id);
            TempData["Success"] = $"Letter sent to {recipients.Count} client(s).";
        }
        else
        {
            TempData["Success"] = $"Letter scheduled for {recipients.Count} client(s).";
        }

        return RedirectToAction(nameof(Index));
    }

    // Renders the letter with merge tokens resolved against a real client where one is picked,
    // so an agent sees "Dear Sarah" rather than "Dear [First Name]" before committing to a send.
    [HttpGet]
    public async Task<IActionResult> PreviewLetter(string body, int sampleClientId = 0)
    {
        var agent = await _db.AgentUsers.FirstOrDefaultAsync(a => a.Id == AgentId);
        if (agent == null) return NotFound();

        var sampleClient = sampleClientId > 0
            ? await _db.Clients.FirstOrDefaultAsync(c => c.Id == sampleClientId && c.AgentUserId == AgentId)
            : null;

        var letter = new ELetter { Body = body ?? string.Empty };
        var html = ELetterHtmlComposer.Wrap(letter, agent, sampleClient);
        return Content(html, "text/html");
    }

    private async Task LoadCreateContextAsync()
    {
        ViewBag.Templates = await ActiveTemplatesAsync();
        ViewBag.MergeFields = MergeFieldResolver.AvailableFields;
        ViewBag.Clients = await _db.Clients
            .Where(c => c.AgentUserId == AgentId && !string.IsNullOrWhiteSpace(c.Email))
            .OrderBy(c => c.LastName).ThenBy(c => c.FirstName)
            .ToListAsync();
        var timeZone = await GetAgentTimeZoneAsync();
        ViewBag.AgentTimeZone = timeZone;
        ViewBag.AgentNow = AgentTimeZoneHelper.FromUtc(DateTime.UtcNow, timeZone);
    }

    private async Task<List<ELetterTemplate>> ActiveTemplatesAsync() =>
        await _db.ELetterTemplates.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToListAsync();

    // Falls back to the first active template so the composer always opens with something in it,
    // even if the requested key was retired between the agent loading the page and posting it.
    private async Task<ELetterTemplate?> ResolveTemplateAsync(string? key)
    {
        var templates = await ActiveTemplatesAsync();
        return templates.FirstOrDefault(t => t.Key == key) ?? templates.FirstOrDefault();
    }

    private async Task<string> GetAgentTimeZoneAsync()
    {
        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        return AgentTimeZoneHelper.Normalize(agent?.TimeZone);
    }

    private async Task<IActionResult?> RequireELetterAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.PreDesignedELetters);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
