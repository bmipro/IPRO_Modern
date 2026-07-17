using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "AdminAccess")]
public class SupportTicketsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly ILogger<SupportTicketsController> _logger;

    public SupportTicketsController(IPRODbContext db, IEmailService email, ILogger<SupportTicketsController> logger)
    {
        _db = db;
        _email = email;
        _logger = logger;
    }

    private string CurrentAdminName => User.FindFirst("FullName")?.Value ?? User.Identity?.Name ?? "Support";

    public async Task<IActionResult> Index(string status = "all", string? search = null, int page = 1)
    {
        const int pageSize = 25;
        page = Math.Max(1, page);
        search = search?.Trim();
        status = NormalizeStatusFilter(status);

        var query = _db.SupportTickets.AsNoTracking().Include(t => t.AgentUser).AsQueryable();

        query = status switch
        {
            "open" => query.Where(t => t.Status == SupportTicketStatus.Open),
            "inprogress" => query.Where(t => t.Status == SupportTicketStatus.InProgress),
            "resolved" => query.Where(t => t.Status == SupportTicketStatus.Resolved),
            "closed" => query.Where(t => t.Status == SupportTicketStatus.Closed),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(t =>
                t.Subject.Contains(search) ||
                t.AgentUser.FirstName.Contains(search) ||
                t.AgentUser.LastName.Contains(search) ||
                t.AgentUser.Email.Contains(search));
        }

        var totalCount = await query.CountAsync();
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        var tickets = await query
            .OrderByDescending(t => t.LastMessageAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        ViewBag.Status = status;
        ViewBag.Search = search;
        ViewBag.Page = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalCount = totalCount;
        ViewBag.UnreadCount = await _db.SupportTickets.CountAsync(t => t.HasUnreadForAdmin);

        return View(tickets);
    }

    public async Task<IActionResult> Details(int id)
    {
        var ticket = await _db.SupportTickets
            .Include(t => t.AgentUser)
            .Include(t => t.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(t => t.Id == id);
        if (ticket == null) return NotFound();

        if (ticket.HasUnreadForAdmin)
        {
            ticket.HasUnreadForAdmin = false;
            await _db.SaveChangesAsync();
        }

        return View(ticket);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(int id, string body)
    {
        var ticket = await _db.SupportTickets.Include(t => t.AgentUser).FirstOrDefaultAsync(t => t.Id == id);
        if (ticket == null) return NotFound();

        body = body?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            TempData["Error"] = "Enter a message before sending.";
            return RedirectToAction(nameof(Details), new { id });
        }

        _db.SupportTicketMessages.Add(new SupportTicketMessage
        {
            SupportTicketId = ticket.Id,
            IsFromAdmin = true,
            AuthorName = CurrentAdminName,
            Body = body
        });

        var now = DateTime.UtcNow;
        ticket.HasUnreadForAgent = true;
        ticket.UpdatedAt = now;
        ticket.LastMessageAt = now;
        if (ticket.Status is SupportTicketStatus.Resolved or SupportTicketStatus.Closed)
        {
            ticket.Status = SupportTicketStatus.Open;
        }
        else if (ticket.Status == SupportTicketStatus.Open)
        {
            ticket.Status = SupportTicketStatus.InProgress;
        }

        await _db.SaveChangesAsync();
        await NotifyAgentAsync(ticket, body);

        TempData["Success"] = "Reply sent.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(int id, string status)
    {
        var ticket = await _db.SupportTickets.FirstOrDefaultAsync(t => t.Id == id);
        if (ticket == null) return NotFound();

        if (!Enum.TryParse<SupportTicketStatus>(status, out var parsed))
        {
            return BadRequest();
        }

        ticket.Status = parsed;
        ticket.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Ticket status set to {parsed}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task NotifyAgentAsync(SupportTicket ticket, string body)
    {
        if (string.IsNullOrWhiteSpace(ticket.AgentUser?.Email)) return;

        try
        {
            var html = $"""
                <p>Support replied to your ticket: <strong>{System.Net.WebUtility.HtmlEncode(ticket.Subject)}</strong></p>
                <p>{System.Net.WebUtility.HtmlEncode(body).Replace("\n", "<br>")}</p>
                """;
            await _email.SendDetailedAsync(ticket.AgentUser.Email, $"{ticket.AgentUser.FirstName} {ticket.AgentUser.LastName}".Trim(), $"[Ticket #{ticket.Id}] {ticket.Subject}", html);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify agent {AgentUserId} of reply on ticket {TicketId}", ticket.AgentUserId, ticket.Id);
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
