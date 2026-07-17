using System.Security.Claims;
using System.Text;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class WebsiteLeadsController : Controller
{
    private const int PageSize = 20;

    private readonly IPRODbContext _db;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public WebsiteLeadsController(IPRODbContext db) => _db = db;

    public async Task<IActionResult> Index(string status = "all", string? search = null, DateTime? fromDate = null, DateTime? toDate = null, string sort = "newest", int page = 1)
    {
        page = Math.Max(1, page);
        status = NormalizeStatusFilter(status);
        sort = NormalizeSort(sort);
        search = search?.Trim();

        var query = BuildFilteredQuery(status, search, fromDate, toDate);

        var totalCount = await query.CountAsync();
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
        page = Math.Min(page, totalPages);

        ViewBag.Status = status;
        ViewBag.Search = search;
        ViewBag.FromDate = fromDate?.ToString("yyyy-MM-dd");
        ViewBag.ToDate = toDate?.ToString("yyyy-MM-dd");
        ViewBag.Sort = sort;
        ViewBag.Page = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalCount = totalCount;
        ViewBag.UnreadCount = await _db.WebsiteLeads.CountAsync(x => x.AgentUserId == AgentId && !x.IsRead);
        ViewBag.NewCount = await _db.WebsiteLeads.CountAsync(x => x.AgentUserId == AgentId && x.Status == WebsiteLeadStatuses.New);

        var leads = await ApplySort(query, sort)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        return View(leads);
    }

    public async Task<IActionResult> Export(string status = "all", string? search = null, DateTime? fromDate = null, DateTime? toDate = null, string sort = "newest")
    {
        status = NormalizeStatusFilter(status);
        sort = NormalizeSort(sort);
        search = search?.Trim();

        var leads = await ApplySort(BuildFilteredQuery(status, search, fromDate, toDate), sort).ToListAsync();

        var csv = new StringBuilder();
        csv.AppendLine("Date,First Name,Last Name,Email,Phone,Status,Type,Source Domain,Page,Message");
        foreach (var lead in leads)
        {
            csv.AppendLine(string.Join(",",
                CsvEscape(lead.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
                CsvEscape(lead.FirstName),
                CsvEscape(lead.LastName),
                CsvEscape(lead.Email),
                CsvEscape(lead.Phone),
                CsvEscape(lead.Status),
                CsvEscape(lead.SubmissionType == WebsiteLeadTypes.Newsletter ? "Newsletter signup" : "Contact request"),
                CsvEscape(lead.SourceDomain),
                CsvEscape(lead.WebsitePage?.Title ?? string.Empty),
                CsvEscape(lead.Message)));
        }

        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"website-leads-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
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
    public async Task<IActionResult> BulkSetStatus(int[] ids, string status, string? returnUrl = null)
    {
        status = status?.Trim() ?? "";
        if (status is not (WebsiteLeadStatuses.Contacted or WebsiteLeadStatuses.Dismissed))
        {
            return BadRequest();
        }

        if (ids == null || ids.Length == 0)
        {
            TempData["Error"] = "Select at least one lead first.";
            return LocalRedirect(SafeReturnUrl(returnUrl));
        }

        var now = DateTime.UtcNow;
        var count = await _db.WebsiteLeads
            .Where(x => x.AgentUserId == AgentId && ids.Contains(x.Id))
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.IsRead, true)
                .SetProperty(x => x.ReadAt, x => x.ReadAt ?? now)
                .SetProperty(x => x.UpdatedAt, now));

        TempData["Success"] = status == WebsiteLeadStatuses.Contacted
            ? $"{count} lead(s) marked as contacted."
            : $"{count} lead(s) dismissed.";

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

    private IQueryable<WebsiteLead> BuildFilteredQuery(string status, string? search, DateTime? fromDate, DateTime? toDate)
    {
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

        if (fromDate.HasValue)
        {
            query = query.Where(x => x.CreatedAt >= fromDate.Value.Date);
        }

        if (toDate.HasValue)
        {
            query = query.Where(x => x.CreatedAt < toDate.Value.Date.AddDays(1));
        }

        return query;
    }

    private static IOrderedQueryable<WebsiteLead> ApplySort(IQueryable<WebsiteLead> query, string sort) => sort switch
    {
        "oldest" => query.OrderBy(x => x.CreatedAt),
        "status" => query.OrderBy(x => x.Status).ThenByDescending(x => x.CreatedAt),
        _ => query.OrderByDescending(x => x.CreatedAt)
    };

    private static string NormalizeStatusFilter(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "new" => "new",
        "contacted" => "contacted",
        "dismissed" => "dismissed",
        "unread" => "unread",
        _ => "all"
    };

    private static string NormalizeSort(string? sort) => sort?.Trim().ToLowerInvariant() switch
    {
        "oldest" => "oldest",
        "status" => "status",
        _ => "newest"
    };

    private static string CsvEscape(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
            ? returnUrl
            : "/WebsiteLeads";
}
