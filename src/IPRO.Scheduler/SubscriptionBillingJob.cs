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
        // M19: every stage fails ALONE. An exception in one stage used to starve every stage
        // after it -- and kept starving them each hour for as long as the same failure recurred,
        // which is precisely when the later stages (dunning, reconciliation) matter most. The
        // per-item isolation inside each stage handles the common case; these guards are the
        // backstop for a stage-level failure (its opening query, for instance).
        try
        {
            var applied = await _billing.ProcessDueSubscriptionChangesAsync();
            if (applied > 0)
            {
                _logger.LogInformation("Applied {Count} due subscription change(s).", applied);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProcessDueSubscriptionChangesAsync failed; continuing with the remaining billing stages.");
        }

        try
        {
            var notified = await _billing.NotifyBillingIssuesAsync();
            if (notified > 0)
            {
                _logger.LogInformation("Sent {Count} billing issue notification(s).", notified);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NotifyBillingIssuesAsync failed; continuing with the remaining billing stages.");
        }

        // Audit #2 (A2-H2): converge any agent left with two Active subscriptions by an earlier
        // failed PayPal cancellation. Warning level so App Insights records every convergence.
        try
        {
            var reconciled = await _billing.ReconcileDuplicateActiveSubscriptionsAsync();
            if (reconciled > 0)
            {
                _logger.LogWarning("Reconciled {Count} superseded subscription row(s) whose earlier cancellation had failed.", reconciled);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReconcileDuplicateActiveSubscriptionsAsync failed; continuing with the remaining billing stages.");
        }

        // 2026-08-14 ultra-audit, the structural gap behind several findings: this system learns that
        // a subscription ended ONLY from a webhook, and nothing ever expired an Active row. A single
        // lost CANCELLED/EXPIRED delivery -- or a buyer cancelling inside PayPal's own interface --
        // left an Active row granting full access indefinitely, invisible to the duplicate check
        // above because that only fires when an agent holds TWO active rows. Asking PayPal directly
        // is the only way to know; wrapped so a PayPal outage cannot take the rest of this job down.
        try
        {
            var corrected = await _billing.ReconcileActiveSubscriptionsWithPayPalAsync();
            if (corrected > 0)
            {
                _logger.LogWarning(
                    "Corrected {Count} Active subscription row(s) that PayPal reports as ended -- their cancellation webhook was lost.",
                    corrected);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayPal subscription reconciliation failed; will retry next run.");
        }
    }
}
