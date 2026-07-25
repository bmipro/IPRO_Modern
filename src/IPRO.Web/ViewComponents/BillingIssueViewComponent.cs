using System.Security.Claims;
using IPRO.Billing;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Web.ViewComponents;

public class BillingIssueViewComponent : ViewComponent
{
    private readonly IBillingService _billing;

    public BillingIssueViewComponent(IBillingService billing)
    {
        _billing = billing;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        // NameIdentifier means different things depending on which cookie scheme authenticated the
        // request (agent vs "ClientPortal") - same shape as the original M-1 bug. This component is
        // only ever rendered from the agent layout today, so this is currently unreachable, but the
        // scheme check makes that an enforced invariant instead of an implicit one.
        if (UserClaimsPrincipal.Identity?.IsAuthenticated != true ||
            UserClaimsPrincipal.Identity.AuthenticationType != CookieAuthenticationDefaults.AuthenticationScheme)
        {
            return Content(string.Empty);
        }

        var idValue = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idValue, out var agentId))
        {
            return Content(string.Empty);
        }

        var issue = await _billing.GetBillingIssueAsync(agentId);
        return issue == null ? Content(string.Empty) : View(issue);
    }
}
