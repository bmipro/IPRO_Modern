using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize(AuthenticationSchemes = "ClientPortal")]
public class ClientPortalController : Controller
{
    private readonly IPRODbContext _db;
    private int ClientId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ClientPortalController(IPRODbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var client = await _db.Clients.Include(c => c.AgentUser).AsNoTracking().FirstOrDefaultAsync(c => c.Id == ClientId);
        if (client == null) return NotFound();

        ViewBag.UnreadMessages = await _db.PortalMessages.CountAsync(m => m.ClientId == ClientId && !m.IsFromClient && !m.IsReadByClient);
        ViewBag.OpenInvoices = await _db.ClientInvoices.CountAsync(i => i.ClientId == ClientId && i.Status == ClientInvoiceStatus.Sent);
        ViewBag.DocumentCount = await _db.PortalDocuments.CountAsync(d => d.ClientId == ClientId);
        ViewBag.PendingRequests = await _db.PortalAppointmentRequests.CountAsync(r => r.ClientId == ClientId && r.Status == PortalAppointmentRequestStatus.Pending);

        return View(client);
    }

    public async Task<IActionResult> Invoices()
    {
        var invoices = await _db.ClientInvoices
            .AsNoTracking()
            .Where(i => i.ClientId == ClientId)
            .OrderByDescending(i => i.IssueDate)
            .ToListAsync();
        return View(invoices);
    }
}
