using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class SupportController : Controller
{
    private const int PageSize = 20;

    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SupportController> _logger;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public SupportController(IPRODbContext db, IEmailService email, IConfiguration configuration, ILogger<SupportController> logger)
    {
        _db = db;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IActionResult> Index(string status = "all", int page = 1)
    {
        page = Math.Max(1, page);
        status = NormalizeStatusFilter(status);

        var query = _db.SupportTickets.AsNoTracking().Where(t => t.AgentUserId == AgentId);
        query = status switch
        {
            "open" => query.Where(t => t.Status == SupportTicketStatus.Open),
            "inprogress" => query.Where(t => t.Status == SupportTicketStatus.InProgress),
            "resolved" => query.Where(t => t.Status == SupportTicketStatus.Resolved),
            "closed" => query.Where(t => t.Status == SupportTicketStatus.Closed),
            _ => query
        };

        var totalCount = await query.CountAsync();
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
        page = Math.Min(page, totalPages);

        var tickets = await query
            .OrderByDescending(t => t.LastMessageAt)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        ViewBag.Status = status;
        ViewBag.Page = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.Articles = HelpDocsService.GetArticles();

        return View(tickets);
    }

    public IActionResult Article(string slug)
    {
        var article = HelpDocsService.FindArticle(slug);
        if (article == null) return NotFound();

        ViewBag.Article = article;
        ViewBag.Html = HelpDocsService.GetArticleHtml(slug);
        return View();
    }

    public IActionResult Create() => View();

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string subject, string message)
    {
        subject = subject?.Trim() ?? string.Empty;
        message = message?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(subject))
        {
            ModelState.AddModelError("subject", "Subject is required.");
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            ModelState.AddModelError("message", "Please describe your issue.");
        }
        if (!ModelState.IsValid)
        {
            ViewBag.Subject = subject;
            ViewBag.Message = message;
            return View();
        }

        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        var authorName = agent == null ? "Agent" : $"{agent.FirstName} {agent.LastName}".Trim();

        var ticket = new SupportTicket
        {
            AgentUserId = AgentId,
            Subject = subject
        };
        ticket.Messages.Add(new SupportTicketMessage
        {
            IsFromAdmin = false,
            AuthorName = authorName,
            Body = message
        });
        _db.SupportTickets.Add(ticket);
        await _db.SaveChangesAsync();

        await NotifySupportAsync(ticket, agent, message);

        TempData["Success"] = "Your ticket was submitted. We'll respond as soon as possible.";
        return RedirectToAction(nameof(Details), new { id = ticket.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var ticket = await _db.SupportTickets
            .Include(t => t.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(t => t.Id == id && t.AgentUserId == AgentId);
        if (ticket == null) return NotFound();

        if (ticket.HasUnreadForAgent)
        {
            ticket.HasUnreadForAgent = false;
            await _db.SaveChangesAsync();
        }

        return View(ticket);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(int id, string body)
    {
        var ticket = await _db.SupportTickets.FirstOrDefaultAsync(t => t.Id == id && t.AgentUserId == AgentId);
        if (ticket == null) return NotFound();

        body = body?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            TempData["Error"] = "Enter a message before sending.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        var authorName = agent == null ? "Agent" : $"{agent.FirstName} {agent.LastName}".Trim();

        _db.SupportTicketMessages.Add(new SupportTicketMessage
        {
            SupportTicketId = ticket.Id,
            IsFromAdmin = false,
            AuthorName = authorName,
            Body = body
        });

        var now = DateTime.UtcNow;
        ticket.HasUnreadForAdmin = true;
        ticket.UpdatedAt = now;
        ticket.LastMessageAt = now;
        if (ticket.Status is SupportTicketStatus.Resolved or SupportTicketStatus.Closed)
        {
            ticket.Status = SupportTicketStatus.Open;
        }
        await _db.SaveChangesAsync();

        await NotifySupportAsync(ticket, agent, body);

        TempData["Success"] = "Your reply was sent.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkResolved(int id)
    {
        var ticket = await _db.SupportTickets.FirstOrDefaultAsync(t => t.Id == id && t.AgentUserId == AgentId);
        if (ticket == null) return NotFound();

        ticket.Status = SupportTicketStatus.Resolved;
        ticket.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Ticket marked as resolved.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task NotifySupportAsync(SupportTicket ticket, AgentUser? agent, string body)
    {
        var supportEmail = _configuration["Support:NotificationEmail"];
        if (string.IsNullOrWhiteSpace(supportEmail) || supportEmail.Contains("CHANGE_THIS", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var agentLabel = agent == null ? "An agent" : $"{agent.FirstName} {agent.LastName} ({agent.Email})";
            var html = $"""
                <p>{System.Net.WebUtility.HtmlEncode(agentLabel)} posted on support ticket #{ticket.Id}: <strong>{System.Net.WebUtility.HtmlEncode(ticket.Subject)}</strong></p>
                <p>{System.Net.WebUtility.HtmlEncode(body).Replace("\n", "<br>")}</p>
                """;
            await _email.SendDetailedAsync(supportEmail, "Support", $"[Ticket #{ticket.Id}] {ticket.Subject}", html);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send support notification email for ticket {TicketId}", ticket.Id);
        }
    }

    private static string NormalizeStatusFilter(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "open" => "open",
        "inprogress" => "inprogress",
        "resolved" => "resolved",
        "closed" => "closed",
        _ => "all"
    };
}
