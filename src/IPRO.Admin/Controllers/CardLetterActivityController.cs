using IPRO.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

// Read-only view of e-card and e-letter sends across every agent, so SuperAdmin can see whether
// the features are being used and whether sends are failing.
//
// Deliberately aggregate-only: it reports how many recipients a send had, never who they were.
// Recipient rows are an agent's own client list, and there's no admin reason to enumerate them
// here when the counts answer the actual question.
[Authorize(Policy = "SuperAdmin")]
public class CardLetterActivityController : Controller
{
    private const int MaxRows = 200;

    private readonly IPRODbContext _db;

    public CardLetterActivityController(IPRODbContext db)
    {
        _db = db;
    }

    public record ActivityRow(
        string Kind,
        string AgentName,
        string Label,
        string Design,
        string Status,
        int TotalRecipients,
        int TotalSent,
        DateTime? SentAt,
        DateTime ScheduledAt,
        DateTime CreatedAt)
    {
        public int Failed => Math.Max(0, TotalRecipients - TotalSent);
        public bool HasFailures => SentAt.HasValue && Failed > 0;
    }

    public async Task<IActionResult> Index()
    {
        var designNames = await _db.ECardDesigns.AsNoTracking()
            .ToDictionaryAsync(d => d.Key, d => $"{d.Occasion} — {d.Name}");
        var templateNames = await _db.ELetterTemplates.AsNoTracking()
            .ToDictionaryAsync(t => t.Key, t => t.Name);

        var cards = await _db.ECards.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt).Take(MaxRows)
            .Select(c => new
            {
                c.Subject, c.Occasion, c.Status, c.TotalRecipients, c.TotalSent,
                c.SentAt, c.ScheduledAt, c.CreatedAt,
                AgentName = c.AgentUser.FirstName + " " + c.AgentUser.LastName
            })
            .ToListAsync();

        var letters = await _db.ELetters.AsNoTracking()
            .OrderByDescending(l => l.CreatedAt).Take(MaxRows)
            .Select(l => new
            {
                l.Subject, l.TemplateKey, l.Status, l.TotalRecipients, l.TotalSent,
                l.SentAt, l.ScheduledAt, l.CreatedAt,
                AgentName = l.AgentUser.FirstName + " " + l.AgentUser.LastName
            })
            .ToListAsync();

        var rows = cards
            .Select(c => new ActivityRow("E-Card", c.AgentName.Trim(), c.Subject,
                designNames.TryGetValue(c.Occasion ?? string.Empty, out var d) ? d : c.Occasion ?? "—",
                c.Status, c.TotalRecipients, c.TotalSent, c.SentAt, c.ScheduledAt, c.CreatedAt))
            .Concat(letters.Select(l => new ActivityRow("E-Letter", l.AgentName.Trim(), l.Subject,
                templateNames.TryGetValue(l.TemplateKey ?? string.Empty, out var t) ? t : "Blank letter",
                l.Status, l.TotalRecipients, l.TotalSent, l.SentAt, l.ScheduledAt, l.CreatedAt)))
            .OrderByDescending(r => r.CreatedAt)
            .Take(MaxRows)
            .ToList();

        ViewBag.TotalCardsSent = cards.Sum(c => c.TotalSent);
        ViewBag.TotalLettersSent = letters.Sum(l => l.TotalSent);
        ViewBag.FailureCount = rows.Count(r => r.HasFailures);
        return View(rows);
    }
}
