using IPRO.Admin.Models;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class DomainsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly AzureDomainAutomationOptions _azureOptions;
    private readonly IAzureDomainAutomationService _azureDomains;
    private readonly ILogger<DomainsController> _logger;

    public DomainsController(
        IPRODbContext db,
        IOptions<AzureDomainAutomationOptions> azureOptions,
        IAzureDomainAutomationService azureDomains,
        ILogger<DomainsController> logger)
    {
        _db = db;
        _azureOptions = azureOptions.Value;
        _azureDomains = azureDomains;
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
        domain.LastCheckedAt = DateTime.UtcNow;
        domain.UpdatedAt = DateTime.UtcNow;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain.DomainName);
            if (addresses.Length == 0)
            {
                domain.DnsStatus = AgentDomainStatus.PendingDns;
                domain.AzureBindingStatus = AgentDomainStatus.BindingPending;
                domain.SslStatus = AgentDomainStatus.BindingPending;
                domain.LastError = "DNS has not resolved yet.";
                TempData["Error"] = $"{domain.DomainName} DNS has not resolved yet.";
            }
            else
            {
                domain.DnsStatus = AgentDomainStatus.DnsReady;
                domain.LastError = string.Empty;

                var result = await _azureDomains.EnsureDomainAsync(domain.DomainName);
                if (result.Success)
                {
                    domain.DnsStatus = AgentDomainStatus.Bound;
                    domain.AzureBindingStatus = AgentDomainStatus.Bound;
                    domain.SslStatus = result.SslBound ? AgentDomainStatus.Bound : AgentDomainStatus.BindingPending;
                    domain.LastError = result.SslBound ? string.Empty : result.Message;
                    TempData["Success"] = result.SslBound
                        ? $"{domain.DomainName} is bound in Azure and SSL is ready."
                        : $"{domain.DomainName} is bound in Azure. SSL is still pending.";
                }
                else
                {
                    domain.AzureBindingStatus = _azureDomains.IsConfigured ? AgentDomainStatus.Failed : AgentDomainStatus.BindingPending;
                    domain.SslStatus = AgentDomainStatus.BindingPending;
                    domain.LastError = result.Message;
                    TempData["Error"] = result.Message;
                }
            }
        }
        catch (Exception ex)
        {
            domain.DnsStatus = AgentDomainStatus.PendingDns;
            domain.AzureBindingStatus = AgentDomainStatus.BindingPending;
            domain.SslStatus = AgentDomainStatus.BindingPending;
            domain.LastError = "DNS has not resolved yet. Confirm the CNAME points to " + domain.DnsTarget + ".";
            TempData["Error"] = domain.LastError;
            _logger.LogInformation(ex, "Manual domain check failed for {Domain}", domain.DomainName);
        }

        await _db.SaveChangesAsync();
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
