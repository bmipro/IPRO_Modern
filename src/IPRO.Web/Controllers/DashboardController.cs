using System.Security.Claims;
using IPRO.Billing;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly IClientService _clients;
    private readonly INewsLetterService _newsletters;
    private readonly IWebsiteService _websites;
    private readonly IBillingService _billing;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IPRODbContext _db;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public DashboardController(IClientService clients, INewsLetterService newsletters, IWebsiteService websites, IBillingService billing, IPackageEntitlementService entitlements, IPRODbContext db)
    { _clients = clients; _newsletters = newsletters; _websites = websites; _billing = billing; _entitlements = entitlements; _db = db; }

    public async Task<IActionResult> Index()
    {
        var agentId = AgentId;
        ViewBag.ClientCount     = await _clients.GetCountAsync(agentId);
        ViewBag.NewsletterCount = (await _newsletters.GetByAgentAsync(agentId)).Count();
        ViewBag.Website         = await _websites.GetByAgentIdAsync(agentId);
        ViewBag.Subscription    = await _billing.GetActiveSubscriptionAsync(agentId);
        ViewBag.AgentName       = User.FindFirstValue("FullName");
        ViewBag.FeatureAccess = await LoadFeatureAccessAsync(agentId);
        ViewBag.DailyInsight = await _db.AgentDailyInsights.AsNoTracking().FirstOrDefaultAsync(i => i.AgentUserId == agentId);
        ViewBag.OverdueFollowUpCount = await _db.ClientFollowUps
            .CountAsync(f => f.Client.AgentUserId == agentId && !f.IsCompleted && f.DueAt.Date < DateTime.Today);
        ViewBag.TodayFollowUpCount = await _db.ClientFollowUps
            .CountAsync(f => f.Client.AgentUserId == agentId && !f.IsCompleted && f.DueAt.Date == DateTime.Today);
        ViewBag.UpcomingFollowUpCount = await _db.ClientFollowUps
            .CountAsync(f => f.Client.AgentUserId == agentId && !f.IsCompleted && f.DueAt.Date > DateTime.Today);
        ViewBag.FollowUps = await _db.ClientFollowUps
            .Include(f => f.Client)
            .Where(f => f.Client.AgentUserId == agentId && !f.IsCompleted)
            .OrderBy(f => f.DueAt)
            .ThenBy(f => f.Client.LastName)
            .Take(8)
            .ToListAsync();
        ViewBag.UnreadWebsiteLeadCount = await _db.WebsiteLeads
            .CountAsync(x => x.AgentUserId == agentId && !x.IsRead);
        ViewBag.NewWebsiteLeadCount = await _db.WebsiteLeads
            .CountAsync(x => x.AgentUserId == agentId && x.Status == WebsiteLeadStatuses.New);
        ViewBag.WebsiteLeads = await _db.WebsiteLeads
            .AsNoTracking()
            .Where(x => x.AgentUserId == agentId && x.Status == WebsiteLeadStatuses.New)
            .OrderByDescending(x => x.CreatedAt)
            .Take(5)
            .ToListAsync();
        return View();
    }

    private async Task<Dictionary<string, PackageFeatureAccess>> LoadFeatureAccessAsync(int agentId)
    {
        var featureCodes = new[]
        {
            PackageFeatureCodes.CalendarScheduler,
            PackageFeatureCodes.Newsletters,
            PackageFeatureCodes.InstantWebsite,
            PackageFeatureCodes.AiDailyAssistant
        };

        var access = new Dictionary<string, PackageFeatureAccess>();
        foreach (var featureCode in featureCodes)
        {
            access[featureCode] = await _entitlements.GetAccessAsync(agentId, featureCode);
        }

        return access;
    }
}
