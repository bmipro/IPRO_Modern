using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class PortalMessagesController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public PortalMessagesController(IPRODbContext db, IPackageEntitlementService entitlements)
    {
        _db = db;
        _entitlements = entitlements;
    }

    // Client Portal is a Platinum/Broker feature (PackageEntitlementSeeder: no, no, all, all) but was
    // enforced in exactly ONE action across the whole surface -- ClientsController.InvitePortal. This
    // controller and PortalRequestsController had no entitlement check at all, so a Silver or Gold
    // agent could run the full client-portal experience by direct navigation, and an agent who
    // downgraded from Platinum kept it working indefinitely (2026-08-14 ultra-audit). Pattern copied
    // from ClientInvoicesController.RequireClientInvoicingAccessAsync, which gates all 13 of its
    // actions -- the shape this feature should have had from the start.
    private async Task<IActionResult?> RequireClientPortalAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.ClientPortal);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }

    public async Task<IActionResult> Index()
    {
        var gate = await RequireClientPortalAccessAsync();
        if (gate != null) return gate;

        var clientsWithMessages = await _db.Clients
            .AsNoTracking()
            .Where(c => c.AgentUserId == AgentId && c.Messages.Any())
            .Select(c => new
            {
                Client = c,
                LastMessage = c.Messages.OrderByDescending(m => m.CreatedAt).First(),
                // Unread means CLIENT-authored and not yet read by the agent. This counted
                // agent-authored messages (!IsFromClient) -- which are created with
                // IsReadByAgent=true -- so the badge was permanently zero and client messages
                // never surfaced an unread indicator (review L-4).
                UnreadCount = c.Messages.Count(m => m.IsFromClient && !m.IsReadByAgent)
            })
            .OrderByDescending(x => x.LastMessage.CreatedAt)
            .ToListAsync();

        ViewBag.Rows = clientsWithMessages.Select(x => new PortalMessageInboxRow(x.Client, x.LastMessage, x.UnreadCount)).ToList();
        ViewBag.AgentTimeZone = await AgentTimeZoneHelper.ResolveForAgentAsync(_db, AgentId);
        return View();
    }

    // Both forms -- see NewsletterController.CreateFromTemplate. The message list links here at
    // /portal/PortalMessages/Thread/{id}, which an attribute route declaring only the bare path
    // cannot serve.
    [HttpGet("PortalMessages/Thread/{clientId}")]
    [HttpGet("portal/PortalMessages/Thread/{clientId}")]
    public async Task<IActionResult> Thread(int clientId)
    {
        var gate = await RequireClientPortalAccessAsync();
        if (gate != null) return gate;

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId && c.AgentUserId == AgentId);
        if (client == null) return NotFound();

        var messages = await _db.PortalMessages.Where(m => m.ClientId == clientId).OrderBy(m => m.CreatedAt).ToListAsync();
        var unread = messages.Where(m => m.IsFromClient && !m.IsReadByAgent).ToList();
        foreach (var message in unread)
        {
            message.IsReadByAgent = true;
        }
        if (unread.Count > 0)
        {
            await _db.SaveChangesAsync();
        }

        ViewBag.Client = client;
        ViewBag.AgentTimeZone = await AgentTimeZoneHelper.ResolveForAgentAsync(_db, AgentId);
        return View(messages);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(int clientId, string body)
    {
        var gate = await RequireClientPortalAccessAsync();
        if (gate != null) return gate;

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId && c.AgentUserId == AgentId);
        if (client == null) return NotFound();

        body = body?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            TempData["Error"] = "Enter a message before sending.";
            return RedirectToAction(nameof(Thread), new { clientId });
        }

        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        var authorName = agent == null ? "Advisor" : $"{agent.FirstName} {agent.LastName}".Trim();

        _db.PortalMessages.Add(new PortalMessage
        {
            ClientId = clientId,
            IsFromClient = false,
            AuthorName = authorName,
            Body = body,
            IsReadByAgent = true,
            IsReadByClient = false
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = "Message sent.";
        return RedirectToAction(nameof(Thread), new { clientId });
    }
}

public record PortalMessageInboxRow(Client Client, PortalMessage LastMessage, int UnreadCount);
