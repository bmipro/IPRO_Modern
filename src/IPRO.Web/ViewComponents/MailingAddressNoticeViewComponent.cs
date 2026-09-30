using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.ViewComponents;

// 533: every marketing email now closes with the adviser's business and mailing address (Canada's
// anti-spam law asks for one). The address comes from the Profile, and its street line is optional
// there, so each marketing send page shows this note while it is missing. Nothing is blocked: the
// footer still carries the city, province and postal code.
public class MailingAddressNoticeViewComponent : ViewComponent
{
    private readonly IPRODbContext _db;

    public MailingAddressNoticeViewComponent(IPRODbContext db)
    {
        _db = db;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        // Agent pages only -- the same scheme check as BillingIssueViewComponent.
        if (UserClaimsPrincipal.Identity?.IsAuthenticated != true ||
            UserClaimsPrincipal.Identity.AuthenticationType != CookieAuthenticationDefaults.AuthenticationScheme)
        {
            return Content(string.Empty);
        }

        if (!int.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var agentId))
        {
            return Content(string.Empty);
        }

        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId);
        return agent == null || AdviserSender.HasStreetAddress(agent) ? Content(string.Empty) : View();
    }
}
