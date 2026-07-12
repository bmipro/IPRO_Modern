using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class WebsiteLeadsController : Controller
{
    private readonly IPRODbContext _db;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public WebsiteLeadsController(IPRODbContext db) => _db = db;

    public async Task<IActionResult> Index(string status = "all", string? search = null, int page = 1)
    {
        const int pageSize = 20;
        page = Math.Max(1, page);
        status = NormalizeStatusFilter(status);
        search = search?.Trim();

        var query = _db.WebsiteLeads
            .AsNoTracking()
            .Include(x => x.Client)
            .Include(x => x.WebsitePage)
            .Where(x => x.AgentUserId == AgentId);

        query = status switch
        {
            "new" => query.Where(x => x.Status == WebsiteLeadStatuses.New),
            "contacted" => query.Where(x => x.Status == WebsiteLeadStatuses.Contacted),
            "dismissed" => query.Where(x => x.Status == WebsiteLeadStatuses.Dismissed),
            "unread" => query.Where(x => !x.IsRead),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(x =>
                x.FirstName.Contains(search) ||
                x.LastName.Contains(search) ||
                x.Email.Contains(search) ||
                x.Phone.Contains(search) ||
                x.Message.Contains(search) ||
                x.SourceDomain.Contains(search));
        }

        var totalCount = await query.CountAsync();
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        ViewBag.Status = status;
        ViewBag.Search = search;
        ViewBag.Page = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalCount = totalCount;
        ViewBag.UnreadCount = await _db.WebsiteLeads.CountAsync(x => x.AgentUserId == AgentId && !x.IsRead);
        ViewBag.NewCount = await _db.WebsiteLeads.CountAsync(x => x.AgentUserId == AgentId && x.Status == WebsiteLeadStatuses.New);

        var leads = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return View(leads);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(int id, string status, string? returnUrl = null)
    {
        var lead = await _db.WebsiteLeads.FirstOrDefaultAsync(x => x.Id == id && x.AgentUserId == AgentId);
        if (lead == null) return NotFound();

        status = status?.Trim() ?? "";
        if (status is not (WebsiteLeadStatuses.New or WebsiteLeadStatuses.Contacted or WebsiteLeadStatuses.Dismissed))
        {
            return BadRequest();
        }

        lead.Status = status;
        lead.IsRead = true;
        lead.ReadAt ??= DateTime.UtcNow;
        lead.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = status == WebsiteLeadStatuses.Contacted
            ? "Lead marked as contacted."
            : status == WebsiteLeadStatuses.Dismissed
                ? "Lead dismissed."
                : "Lead returned to the new queue.";

        return LocalRedirect(SafeReturnUrl(returnUrl));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead(string? returnUrl = null)
    {
        var now = DateTime.UtcNow;
        await _db.WebsiteLeads
            .Where(x => x.AgentUserId == AgentId && !x.IsRead)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.IsRead, true)
                .SetProperty(x => x.ReadAt, now)
                .SetProperty(x => x.UpdatedAt, now));

        TempData["Success"] = "All website leads marked as read.";
        return LocalRedirect(SafeReturnUrl(returnUrl));
    }

    private static string NormalizeStatusFilter(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "new" => "new",
        "contacted" => "contacted",
        "dismissed" => "dismissed",
        "unread" => "unread",
        _ => "all"
    };

    private static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
            ? returnUrl
            : "/WebsiteLeads";
}
