using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize(AuthenticationSchemes = "ClientPortal")]
public class ClientPortalAppointmentsController : Controller
{
    private readonly IPRODbContext _db;
    private int ClientId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ClientPortalAppointmentsController(IPRODbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        ViewBag.UpcomingFollowUps = await _db.ClientFollowUps
            .AsNoTracking()
            .Where(f => f.ClientId == ClientId && !f.IsCompleted)
            .OrderBy(f => f.DueAt)
            .ToListAsync();

        var requests = await _db.PortalAppointmentRequests
            .AsNoTracking()
            .Where(r => r.ClientId == ClientId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return View(requests);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestAppointment(string? notes, DateTime? preferredDate)
    {
        _db.PortalAppointmentRequests.Add(new PortalAppointmentRequest
        {
            ClientId = ClientId,
            Notes = notes?.Trim(),
            PreferredDate = preferredDate
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = "Your appointment request was sent.";
        return RedirectToAction(nameof(Index));
    }
}
