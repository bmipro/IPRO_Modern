using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class PortalRequestsController : Controller
{
    private readonly IPRODbContext _db;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public PortalRequestsController(IPRODbContext db) => _db = db;

    public async Task<IActionResult> Index(string status = "pending")
    {
        status = status?.Trim().ToLowerInvariant() ?? "pending";
        var query = _db.PortalAppointmentRequests
            .AsNoTracking()
            .Include(r => r.Client)
            .Where(r => r.Client.AgentUserId == AgentId);

        query = status switch
        {
            "scheduled" => query.Where(r => r.Status == PortalAppointmentRequestStatus.Scheduled),
            "declined" => query.Where(r => r.Status == PortalAppointmentRequestStatus.Declined),
            "all" => query,
            _ => query.Where(r => r.Status == PortalAppointmentRequestStatus.Pending)
        };

        ViewBag.Status = status;
        return View(await query.OrderByDescending(r => r.CreatedAt).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(int id, string status)
    {
        var request = await _db.PortalAppointmentRequests.Include(r => r.Client).FirstOrDefaultAsync(r => r.Id == id && r.Client.AgentUserId == AgentId);
        if (request == null) return NotFound();

        request.Status = status switch
        {
            "Scheduled" => PortalAppointmentRequestStatus.Scheduled,
            "Declined" => PortalAppointmentRequestStatus.Declined,
            _ => request.Status
        };
        request.RespondedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Request marked {request.Status}.";
        return RedirectToAction(nameof(Index));
    }
}
