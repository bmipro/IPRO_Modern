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
public class ECardsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly ECardDispatcher _dispatcher;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ECardsController(IPRODbContext db, IPackageEntitlementService entitlements, ECardDispatcher dispatcher)
    {
        _db = db;
        _entitlements = entitlements;
        _dispatcher = dispatcher;
    }

    public async Task<IActionResult> Index()
    {
        var gate = await RequireECardAccessAsync();
        if (gate != null) return gate;

        var cards = await _db.ECards
            .Where(c => c.AgentUserId == AgentId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
        var timeZone = await GetAgentTimeZoneAsync();
        ViewBag.AgentTimeZone = timeZone;
        return View(cards);
    }

    public async Task<IActionResult> Create()
    {
        var gate = await RequireECardAccessAsync();
        if (gate != null) return gate;

        await LoadCreateContextAsync();
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string occasion, string subject, string message,
        int[]? clientIds, DateTime scheduledAt, bool sendNow)
    {
        var gate = await RequireECardAccessAsync();
        if (gate != null) return gate;

        // `occasion` carries the template key -- see the note on ECard.Occasion.
        occasion = (ECardTemplateCatalog.Find(occasion) ?? ECardTemplateCatalog.Default).Key;
        subject = subject?.Trim() ?? string.Empty;
        message = message?.Trim() ?? string.Empty;
        var selectedIds = (clientIds ?? Array.Empty<int>()).Distinct().ToList();

        if (string.IsNullOrWhiteSpace(subject) || selectedIds.Count == 0)
        {
            TempData["Error"] = "Enter a subject and select at least one client.";
            await LoadCreateContextAsync();
            return View();
        }

        var clients = await _db.Clients
            .Where(c => c.AgentUserId == AgentId && selectedIds.Contains(c.Id) && !string.IsNullOrWhiteSpace(c.Email))
            .ToListAsync();
        if (clients.Count == 0)
        {
            TempData["Error"] = "None of the selected clients have an email address on file.";
            await LoadCreateContextAsync();
            return View();
        }

        var timeZone = await GetAgentTimeZoneAsync();
        var card = new ECard
        {
            AgentUserId = AgentId,
            Occasion = occasion,
            Subject = subject,
            Message = message,
            Status = ECardStatuses.Scheduled,
            ScheduledAt = sendNow ? DateTime.UtcNow : AgentTimeZoneHelper.ToUtc(scheduledAt, timeZone)
        };
        _db.ECards.Add(card);
        await _db.SaveChangesAsync();

        var recipients = clients.Select(c => new ECardRecipient
        {
            ECardId = card.Id,
            ClientId = c.Id,
            Email = c.Email.Trim().ToLowerInvariant(),
            RecipientName = $"{c.FirstName} {c.LastName}".Trim()
        }).ToList();
        card.TotalRecipients = recipients.Count;
        _db.ECardRecipients.AddRange(recipients);
        await _db.SaveChangesAsync();

        if (sendNow)
        {
            await _dispatcher.DispatchAsync(card.Id);
            TempData["Success"] = $"E-card sent to {recipients.Count} client(s).";
        }
        else
        {
            TempData["Success"] = $"E-card scheduled for {recipients.Count} client(s).";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> PreviewCard(string occasion, string subject, string message)
    {
        var agent = await _db.AgentUsers.FirstOrDefaultAsync(a => a.Id == AgentId);
        if (agent == null) return NotFound();

        var template = ECardTemplateCatalog.Find(occasion) ?? ECardTemplateCatalog.Default;
        var card = new ECard
        {
            Occasion = template.Key,
            Subject = subject ?? string.Empty,
            Message = message ?? string.Empty,
        };
        // Relative art URLs are fine here -- the preview renders inside the portal, same origin.
        var html = ECardHtmlComposer.Wrap(card, agent, string.Empty);
        return Content(html, "text/html");
    }

    private async Task LoadCreateContextAsync()
    {
        ViewBag.Templates = ECardTemplateCatalog.All;
        ViewBag.Clients = await _db.Clients
            .Where(c => c.AgentUserId == AgentId && !string.IsNullOrWhiteSpace(c.Email))
            .OrderBy(c => c.LastName).ThenBy(c => c.FirstName)
            .ToListAsync();
        var timeZone = await GetAgentTimeZoneAsync();
        ViewBag.AgentTimeZone = timeZone;
        ViewBag.AgentNow = AgentTimeZoneHelper.FromUtc(DateTime.UtcNow, timeZone);
    }

    private async Task<string> GetAgentTimeZoneAsync()
    {
        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        return AgentTimeZoneHelper.Normalize(agent?.TimeZone);
    }

    private async Task<IActionResult?> RequireECardAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.PreDesignedECard);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
