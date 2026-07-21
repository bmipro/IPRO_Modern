using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize(AuthenticationSchemes = "ClientPortal")]
public class ClientPortalPreferencesController : Controller
{
    private readonly IPRODbContext _db;
    private int ClientId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ClientPortalPreferencesController(IPRODbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ClientId);
        if (client == null) return NotFound();

        var enrollments = await _db.DripCampaignEnrollments.AsNoTracking()
            .Include(e => e.DripCampaign)
            .Where(e => e.ClientId == ClientId && e.Status == DripCampaignEnrollmentStatus.Active)
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync();

        return View(new PortalPreferencesViewModel
        {
            IsNewsletterSubscribed = client.IsNewsletterSubscribed,
            Enrollments = enrollments
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleNewsletter(bool subscribed)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == ClientId);
        if (client == null) return NotFound();

        client.IsNewsletterSubscribed = subscribed;
        client.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Preferences updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelEnrollment(int enrollmentId)
    {
        var enrollment = await _db.DripCampaignEnrollments.FirstOrDefaultAsync(e =>
            e.Id == enrollmentId && e.ClientId == ClientId && e.Status == DripCampaignEnrollmentStatus.Active);
        if (enrollment != null)
        {
            enrollment.Status = DripCampaignEnrollmentStatus.Cancelled;
            enrollment.CancelledAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Success"] = "You've been removed from that campaign.";
        }

        return RedirectToAction(nameof(Index));
    }
}
