using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class PortalMessagesController : Controller
{
    private readonly IPRODbContext _db;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public PortalMessagesController(IPRODbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var clientsWithMessages = await _db.Clients
            .AsNoTracking()
            .Where(c => c.AgentUserId == AgentId && c.Messages.Any())
            .Select(c => new
            {
                Client = c,
                LastMessage = c.Messages.OrderByDescending(m => m.CreatedAt).First(),
                UnreadCount = c.Messages.Count(m => !m.IsFromClient && !m.IsReadByAgent)
            })
            .OrderByDescending(x => x.LastMessage.CreatedAt)
            .ToListAsync();

        ViewBag.Rows = clientsWithMessages.Select(x => new PortalMessageInboxRow(x.Client, x.LastMessage, x.UnreadCount)).ToList();
        return View();
    }

    public async Task<IActionResult> Thread(int clientId)
    {
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
        return View(messages);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(int clientId, string body)
    {
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
