using System.Security.Claims;
using IPRO.Billing;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
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
        var dailyInsight = await _db.AgentDailyInsights.AsNoTracking().FirstOrDefaultAsync(i => i.AgentUserId == agentId);
        if (dailyInsight != null && dailyInsight.SuggestedActionType != AgentDailyInsightActionTypes.None)
        {
            bool stillActionable;
            if (dailyInsight.RelatedEntityId.HasValue)
            {
                stillActionable = dailyInsight.SuggestedActionType switch
                {
                    AgentDailyInsightActionTypes.OverdueFollowUp => await _db.ClientFollowUps.AnyAsync(f =>
                        f.Id == dailyInsight.RelatedEntityId && f.Client.AgentUserId == agentId && !f.IsCompleted && f.DueAt.Date < DateTime.Today),
                    AgentDailyInsightActionTypes.StaleLead => await _db.WebsiteLeads.AnyAsync(l =>
                        l.Id == dailyInsight.RelatedEntityId && l.AgentUserId == agentId && l.Status == WebsiteLeadStatuses.New),
                    AgentDailyInsightActionTypes.NoFollowUp => await _db.Clients.AnyAsync(c =>
                        c.Id == dailyInsight.RelatedEntityId && c.AgentUserId == agentId && !_db.ClientFollowUps.Any(f => f.ClientId == c.Id && !f.IsCompleted)),
                    _ => true
                };
            }
            else
            {
                // Cached row predates RelatedEntityId (or the job hasn't repopulated it yet) - fall back to
                // a general "does this condition still apply at all for this agent" check.
                var staleCutoff = DateTime.UtcNow.AddHours(-24);
                stillActionable = dailyInsight.SuggestedActionType switch
                {
                    AgentDailyInsightActionTypes.OverdueFollowUp => await _db.ClientFollowUps.AnyAsync(f =>
                        f.Client.AgentUserId == agentId && !f.IsCompleted && f.DueAt.Date < DateTime.Today),
                    AgentDailyInsightActionTypes.StaleLead => await _db.WebsiteLeads.AnyAsync(l =>
                        l.AgentUserId == agentId && l.Status == WebsiteLeadStatuses.New && l.CreatedAt < staleCutoff),
                    AgentDailyInsightActionTypes.NoFollowUp => await _db.Clients.AnyAsync(c =>
                        c.AgentUserId == agentId && !_db.ClientFollowUps.Any(f => f.ClientId == c.Id && !f.IsCompleted)),
                    _ => true
                };
            }

            if (!stillActionable)
            {
                dailyInsight.SuggestedActionType = AgentDailyInsightActionTypes.None;
                dailyInsight.SuggestedActionText = "You're all caught up — no urgent actions today.";
                dailyInsight.SuggestedActionUrl = null;
                dailyInsight.SuggestedActionReason = null;
            }
        }
        ViewBag.DailyInsight = dailyInsight;
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
        ViewBag.AgentTimeZone = await AgentTimeZoneHelper.ResolveForAgentAsync(_db, agentId);
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
