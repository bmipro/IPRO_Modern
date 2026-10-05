using System.Security.Claims;
using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

// 532 (2026-10-05): the adviser's Refer a Friend page -- their link and code, sharing through their own email app
// (iPro never emails the friend), where each referral stands, and a credit note for every reward paid. Opened
// from the Profile card above Calendar Source (the owner's spot) and from the Dashboard card.
[Authorize]
public class ReferAFriendController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IConfiguration _configuration;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ReferAFriendController(IPRODbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    public async Task<IActionResult> Index()
    {
        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        if (agent == null) return NotFound();
        ViewBag.Agent = agent;
        return View(await ReferralProgram.ForAdviserAsync(_db, _configuration, agent.Id, agent.FirstName, DateTime.UtcNow));
    }

    // The credit note for a reward that was paid: the reward and its tax, refunded, on the invoice's design.
    public async Task<IActionResult> CreditNote(int id)
    {
        var referral = await _db.Referrals.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.AgentUserId == AgentId && r.Stage == ReferralStages.Paid);
        if (referral == null) return NotFound();
        ViewBag.Agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        ViewBag.Company = await BillingCompanyDetails.LoadAsync(_db, key => _configuration[key]);
        ViewBag.BackUrl = "/portal/ReferAFriend";
        return View(referral);
    }
}
