using System.Security.Claims;
using IPRO.Billing;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
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
    private readonly IPRODbContext _db;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public DashboardController(IClientService clients, INewsLetterService newsletters, IWebsiteService websites, IBillingService billing, IPRODbContext db)
    { _clients = clients; _newsletters = newsletters; _websites = websites; _billing = billing; _db = db; }

    public async Task<IActionResult> Index()
    {
        var agentId = AgentId;
        ViewBag.ClientCount     = await _clients.GetCountAsync(agentId);
        ViewBag.NewsletterCount = (await _newsletters.GetByAgentAsync(agentId)).Count();
        ViewBag.Website         = await _websites.GetByAgentIdAsync(agentId);
        ViewBag.Subscription    = await _billing.GetActiveSubscriptionAsync(agentId);
        ViewBag.AgentName       = User.FindFirstValue("FullName");
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
        return View();
    }
}
