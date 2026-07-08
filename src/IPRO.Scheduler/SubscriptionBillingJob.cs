using IPRO.Billing;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class SubscriptionBillingJob
{
    private readonly IBillingService _billing;
    private readonly ILogger<SubscriptionBillingJob> _logger;

    public SubscriptionBillingJob(IBillingService billing, ILogger<SubscriptionBillingJob> logger)
    {
        _billing = billing;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var applied = await _billing.ProcessDueSubscriptionChangesAsync();
        var notified = await _billing.NotifyBillingIssuesAsync();
        if (applied > 0)
        {
            _logger.LogInformation("Applied {Count} due subscription change(s).", applied);
        }
        if (notified > 0)
        {
            _logger.LogInformation("Sent {Count} billing issue notification(s).", notified);
        }
    }
}
