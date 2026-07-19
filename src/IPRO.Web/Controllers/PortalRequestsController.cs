using System.Net;
using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class PortalRequestsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public PortalRequestsController(IPRODbContext db, IEmailService email)
    {
        _db = db;
        _email = email;
    }

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
    public async Task<IActionResult> Schedule(int id, DateTime scheduledAt)
    {
        var request = await _db.PortalAppointmentRequests.Include(r => r.Client).FirstOrDefaultAsync(r => r.Id == id && r.Client.AgentUserId == AgentId);
        if (request == null) return NotFound();

        if (scheduledAt == default)
        {
            TempData["Error"] = "Choose a date and time to schedule this appointment.";
            return RedirectToAction(nameof(Index));
        }

        var clientName = $"{request.Client.FirstName} {request.Client.LastName}".Trim();

        var followUp = new ClientFollowUp
        {
            ClientId = request.ClientId,
            Title = $"Appointment: {clientName}",
            Notes = request.Notes ?? string.Empty,
            DueAt = scheduledAt,
            CreatedAt = DateTime.UtcNow
        };
        await _db.ClientFollowUps.AddAsync(followUp);
        await _db.SaveChangesAsync();

        request.Status = PortalAppointmentRequestStatus.Scheduled;
        request.ScheduledAt = scheduledAt;
        request.ClientFollowUpId = followUp.Id;
        request.RespondedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(request.Client.Email))
        {
            var html = $"<p>Hi {WebUtility.HtmlEncode(request.Client.FirstName)},</p>" +
                       $"<p>Your appointment request has been scheduled for <strong>{scheduledAt:dddd, MMMM d, yyyy 'at' h:mm tt}</strong>.</p>" +
                       (string.IsNullOrWhiteSpace(request.Notes) ? "" : $"<p>Notes: {WebUtility.HtmlEncode(request.Notes)}</p>") +
                       "<p>You can review this anytime from your client portal.</p>";
            await _email.SendDetailedAsync(request.Client.Email, clientName, "Your appointment has been scheduled", html);
        }

        TempData["Success"] = "Appointment scheduled.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Decline(int id)
    {
        var request = await _db.PortalAppointmentRequests.Include(r => r.Client).FirstOrDefaultAsync(r => r.Id == id && r.Client.AgentUserId == AgentId);
        if (request == null) return NotFound();

        request.Status = PortalAppointmentRequestStatus.Declined;
        request.RespondedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var clientName = $"{request.Client.FirstName} {request.Client.LastName}".Trim();
        if (!string.IsNullOrWhiteSpace(request.Client.Email))
        {
            var html = "<p>Hi " + WebUtility.HtmlEncode(request.Client.FirstName) + ",</p>" +
                       "<p>Unfortunately your appointment request could not be scheduled at this time. Please reach out to your advisor directly or submit a new request with an alternate time.</p>";
            await _email.SendDetailedAsync(request.Client.Email, clientName, "Your appointment request was declined", html);
        }

        TempData["Success"] = "Request declined.";
        return RedirectToAction(nameof(Index));
    }
}
