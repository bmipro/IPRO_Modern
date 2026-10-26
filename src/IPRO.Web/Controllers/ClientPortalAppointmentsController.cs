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
public class ClientPortalAppointmentsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClientPortalAppointmentsController> _logger;
    private int ClientId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ClientPortalAppointmentsController(IPRODbContext db, IPackageEntitlementService entitlements, IEmailService email, IConfiguration configuration, ILogger<ClientPortalAppointmentsController> logger)
    {
        _db = db;
        _entitlements = entitlements;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        // 597 (2026-10-24): "Upcoming" is the client's own appointments -- requests the adviser scheduled
        // and has not marked done. It used to list every open follow-up on the client's record, so a
        // client could read the adviser's private to-dos and life-event reminders by their titles.
        ViewBag.UpcomingAppointments = await (
            from r in _db.PortalAppointmentRequests.AsNoTracking()
            join f in _db.ClientFollowUps.AsNoTracking() on r.ClientFollowUpId equals (int?)f.Id
            where r.ClientId == ClientId && r.Status == PortalAppointmentRequestStatus.Scheduled
                && f.ClientId == ClientId && !f.IsCompleted
            orderby f.DueAt
            select new IPRO.Web.Models.PortalUpcomingAppointment(f.DueAt, r.Notes))
            .ToListAsync();

        var requests = await _db.PortalAppointmentRequests
            .AsNoTracking()
            .Where(r => r.ClientId == ClientId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
        // 606: "Scheduled ... at" is the appointment's time now (a move in Google moves it), not the first one.
        ViewBag.AppointmentTimes = await IPRO.Web.Infrastructure.PortalAppointmentTimes.CurrentAsync(_db, requests);

        ViewBag.PortalReadOnly = await IPRO.Web.Infrastructure.ClientPortalActivity.IsReadOnlyAsync(_entitlements, User);
        return View(requests);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestAppointment(string? notes, DateTime? preferredDate)
    {
        // 598: read-only while the adviser's plan has no client portal.
        if (await IPRO.Web.Infrastructure.ClientPortalActivity.IsReadOnlyAsync(_entitlements, User))
        {
            TempData["Error"] = IPRO.Web.Infrastructure.ClientPortalActivity.ReadOnlyMessage;
            return RedirectToAction(nameof(Index));
        }

        _db.PortalAppointmentRequests.Add(new PortalAppointmentRequest
        {
            ClientId = ClientId,
            Notes = notes?.Trim(),
            PreferredDate = preferredDate
        });
        await _db.SaveChangesAsync();
        await IPRO.Web.Infrastructure.ClientPortalActivity.NotifyAdviserAsync(_db, _email, _configuration, ClientId, IPRO.Web.Infrastructure.ClientPortalActivity.Kind.AppointmentRequest, _logger);

        TempData["Success"] = "Your appointment request was sent.";
        return RedirectToAction(nameof(Index));
    }
}
