using IPRO.Admin.Models;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "AdminAccess")]
public class DomainsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly AzureDomainAutomationOptions _azureOptions;
    private readonly IAzureDomainAutomationService _azureDomains;
    private readonly IDomainCheckService _domainCheck;
    private readonly ILogger<DomainsController> _logger;

    public DomainsController(
        IPRODbContext db,
        IOptions<AzureDomainAutomationOptions> azureOptions,
        IAzureDomainAutomationService azureDomains,
        IDomainCheckService domainCheck,
        ILogger<DomainsController> logger)
    {
        _db = db;
        _azureOptions = azureOptions.Value;
        _azureDomains = azureDomains;
        _domainCheck = domainCheck;
        _logger = logger;
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

        ViewBag.AzureDomainAutomation = _azureOptions;
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
        await _domainCheck.CheckAsync(domain);
        await _db.SaveChangesAsync();

        if (domain.DnsStatus == AgentDomainStatus.PendingDns)
        {
            TempData["Error"] = domain.LastError;
        }
        else if (domain.AzureBindingStatus == AgentDomainStatus.Bound)
        {
            TempData["Success"] = domain.SslStatus == AgentDomainStatus.Bound
                ? $"{domain.DomainName} is bound in Azure and SSL is ready."
                : $"{domain.DomainName} is bound in Azure. SSL is still pending.";
        }
        else
        {
            TempData["Error"] = domain.LastError;
        }

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
