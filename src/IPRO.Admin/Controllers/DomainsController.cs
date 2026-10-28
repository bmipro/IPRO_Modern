using System.Security.Claims;
using IPRO.Admin.Models;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "AdminAccess")]
public partial class DomainsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly AzureDomainAutomationOptions _azureOptions;
    private readonly IAzureDomainAutomationService _azureDomains;
    private readonly IDomainCheckService _domainCheck;
    private readonly ILogger<DomainsController> _logger;
    private readonly IAdminAuditLogService _auditLog;
    private readonly IConfiguration? _configuration;
    private readonly IMemoryCache? _cache;

    public DomainsController(
        IPRODbContext db,
        IOptions<AzureDomainAutomationOptions> azureOptions,
        IAzureDomainAutomationService azureDomains,
        IDomainCheckService domainCheck,
        ILogger<DomainsController> logger,
        IAdminAuditLogService auditLog,
        IConfiguration? configuration = null,
        IMemoryCache? cache = null)
    {
        _db = db;
        _azureOptions = azureOptions.Value;
        _azureDomains = azureDomains;
        _domainCheck = domainCheck;
        _logger = logger;
        _auditLog = auditLog;
        _configuration = configuration;
        _cache = cache;
    }

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

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
        // 595: the step each domain is on, as its adviser's guide on My Website shows it, so the owner can say "you are
        // on step 2" on the phone.
        var guides = await GuidesAsync(_domainCheck, _configuration, _cache, domains);
        foreach (var item in model) item.Guide = guides[item.Domain.Id];

        ViewBag.AzureDomainAutomation = _azureOptions;
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkBound(int id)
    {
        var domain = await _db.AgentDomains.GetRequiredAsync(id);
        // 592: "bound" means served as this adviser's site; the adviser's TXT record must have been seen first
        // (DomainOwnershipProof). The domain check would undo the mark at its next pass anyway.
        if (DomainOwnershipProof.IsNeeded(domain))
        {
            TempData["Error"] = $"{domain.DomainName} cannot be marked bound: the adviser has not proven it is theirs yet " +
                                $"(TXT record {DomainOwnershipProof.RecordName(domain)}). Use Recheck once the record is in place.";
            return RedirectToAction(nameof(Index));
        }

        if (domain.AzureBindingStatus != AgentDomainStatus.Bound && CertificateClock.FirstBindingFor(domain)) domain.BoundAt = DateTime.UtcNow;   // 595
        domain.DnsStatus = AgentDomainStatus.Bound;
        domain.AzureBindingStatus = AgentDomainStatus.Bound;
        domain.SslStatus = AgentDomainStatus.Bound;
        domain.LastError = string.Empty;
        domain.LastCheckedAt = DateTime.UtcNow;
        domain.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "DomainMarkBound", $"Domain '{domain.DomainName}' manually marked as bound");

        TempData["Success"] = $"{domain.DomainName} marked as bound.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset(int id)
    {
        var domain = await _db.AgentDomains.GetRequiredAsync(id);
        await _domainCheck.CheckAsync(domain);
        await _db.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "DomainRecheck", $"Triggered a recheck for domain '{domain.DomainName}'");

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

public partial class DomainsController
{
    // 595: each row's guide (DomainSetupGuide). Rows still being set up get a fresh look at DNS -- the newest eight, all
    // at once, within four seconds together (a domain whose name servers hang keeps the page waiting no longer), each
    // kept 15 seconds -- and the rest are described from what the domain check recorded.
    internal static readonly TimeSpan LookBudget = TimeSpan.FromSeconds(4);

    internal static async Task<Dictionary<int, DomainSetupGuide.Guide>> GuidesAsync(IDomainCheckService check, IConfiguration? configuration,
        IMemoryCache? cache, IReadOnlyList<AgentDomain> domains, int looks = 8)
    {
        var settings = DomainSetupGuide.Settings.From(configuration);
        var now = DateTime.UtcNow;
        var guides = domains.ToDictionary(d => d.Id, d => DomainSetupGuide.Describe(d, null, settings, now));
        using var budget = new CancellationTokenSource(LookBudget);
        // Domains being set up first, then live ones whose short address points here: their email may have stopped
        // (595's third review, L-2).
        var pending = domains.Where(d => guides[d.Id].Refusal == null && (!guides[d.Id].Live || ShortAddressState.PointsAtPlatform(d)))
            .OrderBy(d => guides[d.Id].Live).ThenByDescending(d => d.UpdatedAt).Take(looks)
            .Select(async domain =>
            {
                var key = $"595-admin-look:{domain.Id}:{domain.DomainName}";
                if (cache != null && cache.TryGetValue(key, out DomainSetupLook? kept)) return (domain, kept);
                DomainSetupLook? look;
                try { look = await check.LookAsync(domain, budget.Token).WaitAsync(budget.Token); }
                catch (Exception) { return (domain, (DomainSetupLook?)null); }       // too slow or failed: what is recorded
                cache?.Set(key, look, TimeSpan.FromSeconds(15));
                return (domain, look);
            })
            .ToList();
        foreach (var (domain, look) in await Task.WhenAll(pending))
        {
            guides[domain.Id] = DomainSetupGuide.Describe(domain, look, settings, now);
        }
        return guides;
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
