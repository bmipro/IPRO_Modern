using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize(AuthenticationSchemes = "ClientPortal")]
public class ClientPortalMessagesController : Controller
{
    private readonly IPRODbContext _db;
    private int ClientId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ClientPortalMessagesController(IPRODbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var messages = await _db.PortalMessages
            .Where(m => m.ClientId == ClientId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        var unread = messages.Where(m => !m.IsFromClient && !m.IsReadByClient).ToList();
        foreach (var message in unread)
        {
            message.IsReadByClient = true;
        }
        if (unread.Count > 0)
        {
            await _db.SaveChangesAsync();
        }

        return View(messages);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(string body)
    {
        body = body?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            TempData["Error"] = "Enter a message before sending.";
            return RedirectToAction(nameof(Index));
        }

        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ClientId);
        var authorName = client == null ? "You" : $"{client.FirstName} {client.LastName}".Trim();

        _db.PortalMessages.Add(new PortalMessage
        {
            ClientId = ClientId,
            IsFromClient = true,
            AuthorName = authorName,
            Body = body,
            IsReadByAgent = false,
            IsReadByClient = true
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = "Message sent.";
        return RedirectToAction(nameof(Index));
    }
}
