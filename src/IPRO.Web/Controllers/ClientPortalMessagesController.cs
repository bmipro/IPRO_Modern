using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize(AuthenticationSchemes = "ClientPortal")]
public class ClientPortalMessagesController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClientPortalMessagesController> _logger;
    private int ClientId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ClientPortalMessagesController(IPRODbContext db, IPackageEntitlementService entitlements, IEmailService email, IConfiguration configuration, ILogger<ClientPortalMessagesController> logger)
    {
        _db = db;
        _entitlements = entitlements;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

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

        ViewBag.PortalReadOnly = await IPRO.Web.Infrastructure.ClientPortalActivity.IsReadOnlyAsync(_entitlements, User);
        return View(messages);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(string body)
    {
        // 598: read-only while the adviser's plan has no client portal.
        if (await IPRO.Web.Infrastructure.ClientPortalActivity.IsReadOnlyAsync(_entitlements, User))
        {
            TempData["Error"] = IPRO.Web.Infrastructure.ClientPortalActivity.ReadOnlyMessage;
            return RedirectToAction(nameof(Index));
        }

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
        await IPRO.Web.Infrastructure.ClientPortalActivity.NotifyAdviserAsync(_db, _email, _configuration, ClientId, IPRO.Web.Infrastructure.ClientPortalActivity.Kind.Message, _logger);

        TempData["Success"] = "Message sent.";
        return RedirectToAction(nameof(Index));
    }
}
