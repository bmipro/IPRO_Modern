using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class TestimonialsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public TestimonialsController(IPRODbContext db, IPackageEntitlementService entitlements)
    {
        _db = db;
        _entitlements = entitlements;
    }

    public async Task<IActionResult> Index(string status = "pending")
    {
        var access = await RequireTestimonialAccessAsync();
        if (access != null) return access;

        status = status?.Trim().ToLowerInvariant() ?? "pending";
        var query = _db.TestimonialSubmissions.Where(t => t.AgentUserId == AgentId);
        query = status switch
        {
            "approved" => query.Where(t => t.Status == TestimonialStatus.Approved),
            "rejected" => query.Where(t => t.Status == TestimonialStatus.Rejected),
            "all" => query,
            _ => query.Where(t => t.Status == TestimonialStatus.Pending)
        };

        ViewBag.Status = status;
        return View(await query.OrderByDescending(t => t.SubmittedAt).ToListAsync());
    }

    public async Task<IActionResult> Edit(int id)
    {
        var access = await RequireTestimonialAccessAsync();
        if (access != null) return access;

        var testimonial = await _db.TestimonialSubmissions.FirstOrDefaultAsync(t => t.Id == id && t.AgentUserId == AgentId);
        if (testimonial == null) return NotFound();
        return View(testimonial);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, string firstName, string lastName, string body)
    {
        var access = await RequireTestimonialAccessAsync();
        if (access != null) return access;

        var testimonial = await _db.TestimonialSubmissions.FirstOrDefaultAsync(t => t.Id == id && t.AgentUserId == AgentId);
        if (testimonial == null) return NotFound();

        firstName = firstName?.Trim() ?? string.Empty;
        lastName = lastName?.Trim() ?? string.Empty;
        body = body?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            TempData["Error"] = "Testimonial text is required.";
            return View(testimonial);
        }

        testimonial.FirstName = firstName;
        testimonial.LastName = lastName;
        testimonial.Body = body;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Testimonial updated.";
        return RedirectToAction(nameof(Index), new { status = testimonial.Status.ToString().ToLowerInvariant() });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id)
    {
        var access = await RequireTestimonialAccessAsync();
        if (access != null) return access;

        var testimonial = await _db.TestimonialSubmissions.FirstOrDefaultAsync(t => t.Id == id && t.AgentUserId == AgentId);
        if (testimonial == null) return NotFound();

        testimonial.Status = TestimonialStatus.Approved;
        testimonial.ReviewedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Testimonial approved and now visible on your site.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id)
    {
        var access = await RequireTestimonialAccessAsync();
        if (access != null) return access;

        var testimonial = await _db.TestimonialSubmissions.FirstOrDefaultAsync(t => t.Id == id && t.AgentUserId == AgentId);
        if (testimonial == null) return NotFound();

        testimonial.Status = TestimonialStatus.Rejected;
        testimonial.ReviewedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Testimonial rejected.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var access = await RequireTestimonialAccessAsync();
        if (access != null) return access;

        var testimonial = await _db.TestimonialSubmissions.FirstOrDefaultAsync(t => t.Id == id && t.AgentUserId == AgentId);
        if (testimonial == null) return NotFound();

        _db.TestimonialSubmissions.Remove(testimonial);
        await _db.SaveChangesAsync();

        TempData["Warning"] = "Testimonial deleted.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult?> RequireTestimonialAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.TestimonialManager);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
