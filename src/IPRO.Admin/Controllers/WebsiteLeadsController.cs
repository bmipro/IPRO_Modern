using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class WebsiteLeadsController : Controller
{
    private readonly IPRODbContext _db;

    public WebsiteLeadsController(IPRODbContext db) => _db = db;

    public async Task<IActionResult> Index(string tab = "leads", string? search = null, string? reason = null, bool failedOnly = false, int page = 1)
    {
        const int pageSize = 25;
        page = Math.Max(1, page);
        tab = tab == "attempts" ? "attempts" : "leads";
        search = search?.Trim();

        ViewBag.Tab = tab;
        ViewBag.Search = search;
        ViewBag.Reason = reason;
        ViewBag.FailedOnly = failedOnly;
        ViewBag.Page = page;
        ViewBag.TotalLeads = await _db.WebsiteLeads.CountAsync();
        ViewBag.TotalAttempts = await _db.WebsiteSpamAttempts.CountAsync();
        ViewBag.FailedNotificationCount = await _db.WebsiteLeads.CountAsync(l => !l.NotificationSent);

        if (tab == "attempts")
        {
            var attemptsQuery = _db.WebsiteSpamAttempts
                .AsNoTracking()
                .Include(a => a.AgentUser)
                .Include(a => a.AgentWebsite)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(reason))
            {
                attemptsQuery = attemptsQuery.Where(a => a.Reason == reason);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                attemptsQuery = attemptsQuery.Where(a =>
                    a.SourceDomain.Contains(search) ||
                    a.IpAddress.Contains(search));
            }

            var totalCount = await attemptsQuery.CountAsync();
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
            page = Math.Min(page, totalPages);
            ViewBag.TotalPages = totalPages;

            var attempts = await attemptsQuery
                .OrderByDescending(a => a.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return View("Index", new WebsiteLeadsAdminViewModel { Attempts = attempts });
        }
        else
        {
            var leadsQuery = _db.WebsiteLeads
                .AsNoTracking()
                .Include(l => l.AgentUser)
                .Include(l => l.Client)
                .AsQueryable();

            if (failedOnly)
            {
                leadsQuery = leadsQuery.Where(l => !l.NotificationSent);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                leadsQuery = leadsQuery.Where(l =>
                    l.FirstName.Contains(search) ||
                    l.LastName.Contains(search) ||
                    l.Email.Contains(search) ||
                    l.SourceDomain.Contains(search) ||
                    l.AgentUser.FirstName.Contains(search) ||
                    l.AgentUser.LastName.Contains(search) ||
                    l.AgentUser.Email.Contains(search));
            }

            var totalCount = await leadsQuery.CountAsync();
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
            page = Math.Min(page, totalPages);
            ViewBag.TotalPages = totalPages;

            var leads = await leadsQuery
                .OrderByDescending(l => l.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return View("Index", new WebsiteLeadsAdminViewModel { Leads = leads });
        }
    }
}

public class WebsiteLeadsAdminViewModel
{
    public List<WebsiteLead> Leads { get; set; } = new();
    public List<WebsiteSpamAttempt> Attempts { get; set; } = new();
}
