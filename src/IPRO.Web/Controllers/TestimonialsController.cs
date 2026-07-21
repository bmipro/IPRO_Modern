using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Email;
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
    private readonly IEmailService _email;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public TestimonialsController(IPRODbContext db, IPackageEntitlementService entitlements, IEmailService email)
    {
        _db = db;
        _entitlements = entitlements;
        _email = email;
    }

    public async Task<IActionResult> Index(string status = "pending")
    {
        var access = await RequireTestimonialAccessAsync();
        if (access != null) return access;

        status = status?.Trim().ToLowerInvariant() ?? "pending";
        var query = _db.TestimonialSubmissions.Where(t => t.AgentUserId == AgentId && t.Body != "");
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

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestFromClient(int clientId)
    {
        var access = await RequireTestimonialAccessAsync();
        if (access != null) return access;

        var client = await _db.Clients.Include(c => c.AgentUser).FirstOrDefaultAsync(c => c.Id == clientId && c.AgentUserId == AgentId);
        if (client == null) return NotFound();
        if (string.IsNullOrWhiteSpace(client.Email))
        {
            TempData["Error"] = "This client has no email address on file.";
            return RedirectToAction("Details", "Clients", new { id = clientId });
        }

        var submission = await _db.TestimonialSubmissions.FirstOrDefaultAsync(t =>
            t.ClientId == clientId && t.AgentUserId == AgentId && t.Status == TestimonialStatus.Pending && t.Body == "");
        if (submission == null)
        {
            submission = new TestimonialSubmission
            {
                AgentUserId = AgentId,
                ClientId = clientId,
                FirstName = client.FirstName,
                LastName = client.LastName,
                Email = client.Email,
                Body = string.Empty,
                Status = TestimonialStatus.Pending,
                SubmittedAt = DateTime.UtcNow,
                RequestToken = Guid.NewGuid().ToString("N")
            };
            _db.TestimonialSubmissions.Add(submission);
        }
        await _db.SaveChangesAsync();

        var requestUrl = Url.Action("Show", "TestimonialRequest", new { token = submission.RequestToken }, Request.Scheme);
        var companyName = client.AgentUser.CompanyName;
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#1457d9;color:white"><h1 style="margin:0;font-size:24px">{System.Net.WebUtility.HtmlEncode(companyName)}</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p>{System.Net.WebUtility.HtmlEncode(companyName)} would love to hear about your experience. Would you mind sharing a quick testimonial?</p>
                <p><a href="{requestUrl}" style="display:inline-block;padding:11px 18px;background:#1457d9;color:white;text-decoration:none;border-radius:6px">Share Your Feedback</a></p>
              </div>
            </div>
            """;
        await _email.SendDetailedAsync(client.Email, $"{client.FirstName} {client.LastName}".Trim(), $"{companyName} would love your feedback", html);

        TempData["Success"] = $"Testimonial request sent to {client.Email}.";
        return RedirectToAction("Details", "Clients", new { id = clientId });
    }

    private async Task<IActionResult?> RequireTestimonialAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.TestimonialManager);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
