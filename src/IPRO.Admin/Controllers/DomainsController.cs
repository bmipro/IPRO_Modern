using IPRO.Admin.Models;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class DomainsController : Controller
{
    private readonly IPRODbContext _db;

    public DomainsController(IPRODbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        var domains = await _db.AgentDomains
            .AsNoTracking()
            .Include(d => d.AgentUser)
            .Include(d => d.AgentWebsite)
            .OrderBy(d => d.AzureBindingStatus == AgentDomainStatus.Bound)
            .ThenByDescending(d => d.UpdatedAt)
            .ToListAsync();

        var model = domains.Select(d => new AgentDomainViewModel
        {
            Domain = d,
            AgentName = string.Join(" ", new[] { d.AgentUser.FirstName, d.AgentUser.LastName }
                .Where(part => !string.IsNullOrWhiteSpace(part))).Trim(),
            AgentEmail = d.AgentUser.Email,
            TemporaryDomain = d.AgentUser.DomainName
        }).ToList();

        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkBound(int id)
    {
        var domain = await _db.AgentDomains.GetRequiredAsync(id);
        domain.DnsStatus = AgentDomainStatus.Bound;
        domain.AzureBindingStatus = AgentDomainStatus.Bound;
        domain.SslStatus = AgentDomainStatus.Bound;
        domain.LastError = string.Empty;
        domain.LastCheckedAt = DateTime.UtcNow;
        domain.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"{domain.DomainName} marked as bound.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset(int id)
    {
        var domain = await _db.AgentDomains.GetRequiredAsync(id);
        domain.DnsStatus = AgentDomainStatus.PendingDns;
        domain.AzureBindingStatus = AgentDomainStatus.BindingPending;
        domain.SslStatus = AgentDomainStatus.BindingPending;
        domain.LastError = string.Empty;
        domain.LastCheckedAt = null;
        domain.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"{domain.DomainName} queued for another domain check.";
        return RedirectToAction(nameof(Index));
    }
}

internal static class DomainDbSetExtensions
{
    public static async Task<AgentDomain> GetRequiredAsync(this DbSet<AgentDomain> domains, int id)
    {
        var domain = await domains.FirstOrDefaultAsync(d => d.Id == id);
        return domain ?? throw new InvalidOperationException("Domain request was not found.");
    }
}
