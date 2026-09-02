using System.Net;
using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IPRO.Web.Controllers;

[Authorize]
public class PortalRequestsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IPackageEntitlementService _entitlements;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private readonly IConfiguration _configuration;

    public PortalRequestsController(IPRODbContext db, IEmailService email, IPackageEntitlementService entitlements, IConfiguration configuration)
    {
        _db = db;
        _email = email;
        _entitlements = entitlements;
        _configuration = configuration;
    }

    // Client Portal is Platinum/Broker only; this controller had no entitlement check at all, so a
    // Silver or Gold agent could manage portal appointment requests by direct navigation and a
    // downgraded agent kept the feature (2026-08-14 ultra-audit). See PortalMessagesController for
    // the full note; the pattern is ClientInvoicesController's.
    private async Task<IActionResult?> RequireClientPortalAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.ClientPortal);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }

    public async Task<IActionResult> Index(string status = "pending")
    {
        var gate = await RequireClientPortalAccessAsync();
        if (gate != null) return gate;

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
        ViewBag.AgentTimeZone = await AgentTimeZoneHelper.ResolveForAgentAsync(_db, AgentId);
        return View(await query.OrderByDescending(r => r.CreatedAt).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Schedule(int id, DateTime scheduledAt)
    {
        var gate = await RequireClientPortalAccessAsync();
        if (gate != null) return gate;

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
            // 460: "from your client portal" now says where that is -- the agent's own domain when
            // one is attached, otherwise the platform (ClientPortalUrls, the same rule as the invite).
            var loginUrl = IPRO.Web.Infrastructure.ClientPortalUrls.LoginUrl(
                await IPRO.Web.Infrastructure.ClientPortalUrls.GetBaseUrlAsync(_db, AgentId, _configuration));
            var html = $"<p>Hi {WebUtility.HtmlEncode(request.Client.FirstName)},</p>" +
                       $"<p>Your appointment request has been scheduled for <strong>{scheduledAt:dddd, MMMM d, yyyy 'at' h:mm tt}</strong>.</p>" +
                       (string.IsNullOrWhiteSpace(request.Notes) ? "" : $"<p>Notes: {WebUtility.HtmlEncode(request.Notes)}</p>") +
                       $"<p>You can review this anytime from your client portal: <a href=\"{loginUrl}\">{loginUrl}</a></p>";
            // 454: the appointment is scheduled either way; a failed confirmation is said out loud.
            var result = await _email.SendDetailedAsync(request.Client.Email, clientName, "Your appointment has been scheduled", html);
            if (!result.Success)
            {
                TempData["Error"] = $"Appointment scheduled, but the confirmation could not be emailed to {request.Client.Email}: {result.Message} Let {request.Client.FirstName} know another way.";
                return RedirectToAction(nameof(Index));
            }
        }

        TempData["Success"] = "Appointment scheduled.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Decline(int id)
    {
        var gate = await RequireClientPortalAccessAsync();
        if (gate != null) return gate;

        var request = await _db.PortalAppointmentRequests.Include(r => r.Client).FirstOrDefaultAsync(r => r.Id == id && r.Client.AgentUserId == AgentId);
        if (request == null) return NotFound();

        request.Status = PortalAppointmentRequestStatus.Declined;
        request.RespondedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var clientName = $"{request.Client.FirstName} {request.Client.LastName}".Trim();
        if (!string.IsNullOrWhiteSpace(request.Client.Email))
        {
            var loginUrl = IPRO.Web.Infrastructure.ClientPortalUrls.LoginUrl(
                await IPRO.Web.Infrastructure.ClientPortalUrls.GetBaseUrlAsync(_db, AgentId, _configuration));
            var html = "<p>Hi " + WebUtility.HtmlEncode(request.Client.FirstName) + ",</p>" +
                       "<p>Unfortunately your appointment request could not be scheduled at this time. Please reach out to your advisor directly or submit a new request with an alternate time.</p>" +
                       $"<p>You can submit a new request from your client portal: <a href=\"{loginUrl}\">{loginUrl}</a></p>";
            var result = await _email.SendDetailedAsync(request.Client.Email, clientName, "Your appointment request was declined", html);
            if (!result.Success)
            {
                TempData["Error"] = $"Request declined, but {request.Client.Email} could not be emailed: {result.Message} Let {request.Client.FirstName} know another way.";
                return RedirectToAction(nameof(Index));
            }
        }

        TempData["Success"] = "Request declined.";
        return RedirectToAction(nameof(Index));
    }
}
