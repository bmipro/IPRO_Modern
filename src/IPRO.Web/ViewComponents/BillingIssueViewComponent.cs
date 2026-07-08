using System.Security.Claims;
using IPRO.Billing;
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
        if (UserClaimsPrincipal.Identity?.IsAuthenticated != true)
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
