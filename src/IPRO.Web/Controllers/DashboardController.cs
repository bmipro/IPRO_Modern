using System.Security.Claims;
using IPRO.Billing;
using IPRO.Business.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Web.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly IClientService _clients;
    private readonly INewsLetterService _newsletters;
    private readonly IWebsiteService _websites;
    private readonly IBillingService _billing;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public DashboardController(IClientService clients, INewsLetterService newsletters, IWebsiteService websites, IBillingService billing)
    { _clients = clients; _newsletters = newsletters; _websites = websites; _billing = billing; }

    public async Task<IActionResult> Index()
    {
        var agentId = AgentId;
        ViewBag.ClientCount     = await _clients.GetCountAsync(agentId);
        ViewBag.NewsletterCount = (await _newsletters.GetByAgentAsync(agentId)).Count();
        ViewBag.Website         = await _websites.GetByAgentIdAsync(agentId);
        ViewBag.Subscription    = await _billing.GetActiveSubscriptionAsync(agentId);
        ViewBag.AgentName       = User.FindFirstValue("FullName");
        return View();
    }
}
