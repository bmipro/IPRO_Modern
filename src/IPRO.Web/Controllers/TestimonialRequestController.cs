using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[AllowAnonymous]
[Route("testimonial")]
public class TestimonialRequestController : Controller
{
    private readonly IPRODbContext _db;

    public TestimonialRequestController(IPRODbContext db) => _db = db;

    [HttpGet("{token}")]
    public async Task<IActionResult> Show(string token)
    {
        var submission = await LoadAsync(token);
        if (submission == null) return NotFound();

        ViewBag.Agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == submission.AgentUserId);
        return View(submission);
    }

    [HttpPost("{token}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(string token, string body, bool consentGiven)
    {
        var submission = await LoadAsync(token);
        if (submission == null) return NotFound();

        body = body?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(submission.Body) && !string.IsNullOrWhiteSpace(body) && consentGiven)
        {
            submission.Body = body;
            submission.SubmittedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Show), new { token });
    }

    private Task<TestimonialSubmission?> LoadAsync(string token) =>
        _db.TestimonialSubmissions.FirstOrDefaultAsync(t => t.RequestToken == token);
}
