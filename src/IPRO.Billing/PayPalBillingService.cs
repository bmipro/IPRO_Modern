using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPRO.Billing;

public class PayPalBillingService : IBillingService
{
    private readonly IUnitOfWork _uow;
    private readonly IPRODbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly PayPalSettings _settings;
    private readonly ILogger<PayPalBillingService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PayPalBillingService(IUnitOfWork uow, IPRODbContext db, IHttpClientFactory httpClientFactory, IEmailService email, IOptions<PayPalSettings> settings, IConfiguration configuration, ILogger<PayPalBillingService> logger)
    {
        _uow = uow;
        _db = db;
        _httpClientFactory = httpClientFactory;
        _email = email;
        _configuration = configuration;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IPRO.Entities.Billing?> GetActiveSubscriptionAsync(int userId)
    {
        await ApplyDuePendingChangesAsync(userId);

        return await _uow.Billings.FirstOrDefaultAsync(b =>
            b.AgentUserId == userId && b.Status == BillingStatus.Active);
    }

    public async Task<SubscriptionChange?> GetPendingChangeAsync(int userId)
    {
        await ApplyDuePendingChangesAsync(userId);

        return await _uow.SubscriptionChanges.FirstOrDefaultAsync(c =>
            c.AgentUserId == userId && c.Status == SubscriptionChangeStatus.Pending);
    }

    public async Task<BillingIssue?> GetBillingIssueAsync(int userId)
    {
        var activeSubscription = await GetActiveSubscriptionAsync(userId);
        var invoices = await GetInvoicesAsync(userId);
        var failedInvoice = invoices.FirstOrDefault(i =>
            !i.IsPaid &&
            i.Billing != null &&
            (i.Billing?.Status == BillingStatus.Failed || IsPayPalFailedInvoice(i)) &&
            IsActionableBillingIssue(i.Billing!, activeSubscription));
        if (failedInvoice != null)
        {
            return await BuildBillingIssueAsync(failedInvoice, "Payment failed", "Your last payment could not be completed. Please update or retry your payment to keep your IPRO services active.");
        }

        var pendingInvoice = invoices.FirstOrDefault(i =>
            !i.IsPaid &&
            i.Billing?.Status == BillingStatus.Pending &&
            i.IssuedAt <= DateTime.UtcNow.AddHours(-24) &&
            IsActionableBillingIssue(i.Billing, activeSubscription));
        if (pendingInvoice != null)
        {
            return await BuildBillingIssueAsync(pendingInvoice, "Payment pending", "You have a payment that was started but not completed. Please continue payment or cancel the checkout.");
        }

        return null;
    }

    public async Task<BillingChangeResult> CreateSubscriptionAsync(int userId, int billingRuleId, BillingPeriod period, string returnUrl, string cancelUrl)
    {
        var requestedPackage = await _uow.BillingRules.FirstOrDefaultAsync(p => p.Id == billingRuleId && p.IsActive);
        if (requestedPackage == null)
        {
            return BillingChangeResult.Failed("We could not activate that subscription. Please choose an active package.");
        }

        // Trial packages are only ever entered via an invitation code at registration (see
        // TrialInviteCode) - never through the normal subscribe/upgrade/downgrade flow, or this
        // would be a way to get a genuinely active, never-expiring Billing row for a free package.
        if (requestedPackage.IsTrialPackage)
        {
            return BillingChangeResult.Failed("That package is invitation-only. Please choose one of our regular packages.");
        }

        // The period arrives straight from a POST body and was never validated (2026-08-14
        // ultra-audit). Quarterly is the sharp edge: the Super Admin package form hard-forces
        // QuarterlyPrice to 0 while GetPayPalPlanId(Quarterly) returns the MONTHLY plan id, so
        // posting period=Quarterly produced a $0 Billing.Amount, a $0 invoice (hence no tax
        // gross-up, so PayPal billed the net price forever) and a NextBillingDate three months out
        // on a monthly plan -- and the zeroed Amount then poisoned every later proration. Annually
        // on a package with no annual price has the same shape. Validate at the boundary: a period
        // is only offerable if it has both a real price and a real plan.
        if (!IsPeriodOfferable(requestedPackage, period))
        {
            _logger.LogWarning(
                "Rejected subscribe for agent {AgentId}: package {PackageId} has no price/plan for {Period}.",
                userId, requestedPackage.Id, period);
            return BillingChangeResult.Failed("That billing period is not available for this package. Please choose Monthly or Annually.");
        }

        var activeSubscription = await GetActiveSubscriptionAsync(userId);
        if (activeSubscription == null)
        {
            await CancelPendingChangesAsync(userId);

            var agent = await _uow.AgentUsers.GetByIdAsync(userId);
            var promo = await ValidatePromotionCodeAsync(agent?.PromotionCode, requestedPackage.Id, userId);

            decimal? overrideAmount = null;
            string? overridePlanId = null;

            // The per-package setup-fee waiver (Super Admin -> Packages -> Edit) is applied first,
            // so what PayPal charges is what the pricing page advertised. A promotion code then
            // discounts whatever remains after the waiver -- it can never resurrect a waived fee.
            var baseSetupFee = requestedPackage.EffectiveSetupFee(DateTime.UtcNow);
            decimal? overrideSetupFee = baseSetupFee != requestedPackage.SetupFee ? baseSetupFee : null;

            if (promo != null)
            {
                if (promo.SetupFeeDiscountType != PromoDiscountType.None)
                {
                    overrideSetupFee = ComputeDiscountedAmount(baseSetupFee, promo.SetupFeeDiscountType, promo.SetupFeeDiscountValue);
                }

                if (promo.RecurringDiscountType != PromoDiscountType.None)
                {
                    overrideAmount = ComputeDiscountedAmount(GetAmount(requestedPackage, period), promo.RecurringDiscountType, promo.RecurringDiscountValue);

                    var effectiveSetupFee = overrideSetupFee ?? baseSetupFee;
                    var isFullyComped = promo.RecurringDurationCycles == null && overrideAmount <= 0 && effectiveSetupFee <= 0;

                    if (isFullyComped)
                    {
                        // No PayPal plan needed at all - BeginPaidChangeAsync will activate directly.
                        overridePlanId = string.Empty;
                    }
                    else
                    {
                        try
                        {
                            overridePlanId = await GetOrCreatePromoPlanIdAsync(promo, requestedPackage, period);
                        }
                        catch (InvalidOperationException)
                        {
                            return BillingChangeResult.Failed("This promotion code's pricing can't be set up with PayPal right now (a permanent 100%-or-more discount isn't supported unless the setup fee is also fully discounted). Please contact support.");
                        }
                    }
                }
            }

            var effectiveAmount = overrideAmount ?? GetAmount(requestedPackage, period);
            return await BeginPaidChangeAsync(
                userId,
                null,
                requestedPackage,
                period,
                SubscriptionChangeType.Subscribe,
                DateTime.UtcNow,
                0,
                effectiveAmount,
                effectiveAmount,
                returnUrl,
                cancelUrl,
                includeSetupFee: true,
                overrideAmount: overrideAmount,
                overridePlanId: overridePlanId,
                overrideSetupFee: overrideSetupFee,
                promotionCodeId: promo?.Id);
        }

        if (activeSubscription.BillingRuleId == requestedPackage.Id)
        {
            await CancelPendingChangesAsync(userId);
            return new BillingChangeResult { Success = true, Message = "You are already on that package." };
        }

        var currentPackage = await _uow.BillingRules.GetByIdAsync(activeSubscription.BillingRuleId);
        if (currentPackage == null)
        {
            return BillingChangeResult.Failed("Your current package could not be found.");
        }

        if (IsUpgrade(currentPackage, requestedPackage))
        {
            await CancelPendingChangesAsync(userId);
            var now = DateTime.UtcNow;
            var effectiveEnd = activeSubscription.NextBillingDate ?? GetNextBillingDate(now, activeSubscription.Period);
            // Measure from the start of the CURRENT cycle, not from Billing.StartDate -- see
            // GetCurrentCycleStart for why the latter silently undercharges every upgrade that
            // follows a renewal.
            var cycleStart = GetCurrentCycleStart(effectiveEnd, activeSubscription.Period);
            var remainingFraction = CalculateRemainingFraction(now, cycleStart, effectiveEnd);
            var credit = Math.Round(GetAmount(currentPackage, activeSubscription.Period) * remainingFraction, 2);
            var charge = Math.Round(GetAmount(requestedPackage, period) * remainingFraction, 2);
            var amountDue = Math.Max(0, charge - credit);

            // Both branches now go through BeginPaidChangeAsync so that a PayPal subscription is
            // always created for the new package. The zero-due branch used to call
            // ApplyUpgradeWithoutPaymentAsync, which cancelled the paid subscription and activated
            // the new package locally with no PayPal subscription behind it -- upgrading on the last
            // day of a cycle (remainingFraction ~ 0, so amountDue = 0) therefore granted the top
            // package permanently, for nothing. There is nothing to "apply without payment": a
            // recurring charge always needs the customer's approval at PayPal, even when the
            // immediate amount is zero.
            return await BeginPaidChangeAsync(userId, currentPackage, requestedPackage, period,
                SubscriptionChangeType.Upgrade, now, credit, charge, amountDue,
                returnUrl, cancelUrl, activeSubscription.Id, effectiveEnd);
        }

        await ScheduleDowngradeAsync(userId, activeSubscription, currentPackage, requestedPackage, period);
        return new BillingChangeResult
        {
            Success = true,
            Message = $"Your downgrade to {requestedPackage.PackageName} is scheduled for {(activeSubscription.NextBillingDate ?? GetNextBillingDate(DateTime.UtcNow, activeSubscription.Period)):MMMM d, yyyy}."
        };
    }

    public async Task<BillingChangeResult> CapturePaymentAsync(int userId, string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return BillingChangeResult.Failed("Missing PayPal payment id.");
        }

        var subscriptionInvoice = await _uow.Invoices.FirstOrDefaultAsync(i =>
            i.AgentUserId == userId && i.PayPalTransactionId == orderId && !i.IsPaid);
        var subscriptionBilling = subscriptionInvoice == null
            ? await _uow.Billings.FirstOrDefaultAsync(b => b.AgentUserId == userId && b.PayPalSubscriptionId == orderId && b.Status == BillingStatus.Pending)
            : await _uow.Billings.GetByIdAsync(subscriptionInvoice.BillingId);
        if (subscriptionBilling != null &&
            subscriptionBilling.AgentUserId == userId &&
            !string.IsNullOrWhiteSpace(subscriptionBilling.PayPalSubscriptionId) &&
            subscriptionBilling.PayPalSubscriptionId == orderId)
        {
            var status = await GetPayPalSubscriptionStatusAsync(orderId);
            if (!IsPayPalSubscriptionApproved(status))
            {
                return BillingChangeResult.Failed("PayPal has not activated that subscription yet. Please complete the PayPal approval.");
            }

            return await ActivateSubscriptionBillingAsync(userId, subscriptionBilling, subscriptionInvoice, "PayPal subscription approved.");
        }

        var invoice = await _uow.Invoices.FirstOrDefaultAsync(i =>
            i.AgentUserId == userId && i.PayPalTransactionId == orderId && !i.IsPaid);
        if (invoice == null)
        {
            return BillingChangeResult.Failed("We could not find a pending invoice for that PayPal payment.");
        }

        var captured = false;
        try
        {
            captured = await CapturePayPalOrderAsync(orderId);
        }
        catch
        {
            await MarkPaymentFailedAsync(userId, invoice.BillingId);
            return BillingChangeResult.Failed("PayPal could not confirm that payment. The checkout was closed, so please choose a package again.");
        }

        if (!captured)
        {
            await MarkPaymentFailedAsync(userId, invoice.BillingId);
            return BillingChangeResult.Failed("PayPal did not confirm the payment. Please try again.");
        }

        invoice.IsPaid = true;
        _uow.Invoices.Update(invoice);

        var change = await _uow.SubscriptionChanges.FirstOrDefaultAsync(c =>
            c.BillingId == invoice.BillingId && c.AgentUserId == userId && c.Status == SubscriptionChangeStatus.Pending);
        var billing = await _uow.Billings.GetByIdAsync(invoice.BillingId);
        if (change == null || billing == null)
        {
            await _uow.SaveChangesAsync();
            await SendPaidInvoiceEmailAsync(invoice.Id);
            return new BillingChangeResult { Success = true, Message = "Payment captured." };
        }

        var activeSubscriptions = await _uow.Billings.FindAsync(b =>
            b.AgentUserId == userId && b.Status == BillingStatus.Active && b.Id != billing.Id);
        foreach (var subscription in activeSubscriptions)
        {
            // The upgrade payment is already captured, so the new subscription activates either
            // way -- but the old row is only marked Cancelled when PayPal actually stopped it
            // (empty PayPalSubscriptionId counts: nothing external to stop). Marking it Cancelled
            // after a failed cancel is the lie the independent review flagged (H-1): the portal
            // says cancelled while PayPal keeps charging. Leaving the row Active keeps the truth
            // visible and lets a retry succeed later.
            if (!await CancelPayPalSubscriptionAsync(subscription.PayPalSubscriptionId, "Replaced by an upgraded IPRO subscription."))
            {
                _logger.LogError(
                    "Billing {BillingId} (PayPal {SubscriptionId}) could not be cancelled while upgrading agent {AgentUserId}; leaving it Active so the failure is visible and retryable.",
                    subscription.Id, subscription.PayPalSubscriptionId, userId);
                continue;
            }

            subscription.Status = BillingStatus.Cancelled;
            subscription.CancelledAt = DateTime.UtcNow;
            _uow.Billings.Update(subscription);
        }

        billing.Status = BillingStatus.Active;
        await SyncAgentPackageAsync(userId, billing.BillingRuleId);
        if (change.ChangeType == SubscriptionChangeType.Upgrade && change.EffectiveDate < DateTime.UtcNow)
        {
            billing.StartDate = DateTime.UtcNow;
        }
        _uow.Billings.Update(billing);

        change.Status = SubscriptionChangeStatus.Applied;
        change.AppliedAt = DateTime.UtcNow;
        _uow.SubscriptionChanges.Update(change);

        await _uow.SaveChangesAsync();
        await SendPaidInvoiceEmailAsync(invoice.Id);
        return new BillingChangeResult
        {
            Success = true,
            Message = "Payment confirmed and your package is active."
        };
    }

    // paymentConfirmed: has money actually settled? Only PAYMENT.SALE.COMPLETED (or a genuinely $0
    // comped subscription) can say yes. Approval and BILLING.SUBSCRIPTION.ACTIVATED cannot: APPROVED
    // is PayPal's post-consent, PRE-payment state, and our plans carry
    // setup_fee_failure_action = "CONTINUE", which activates a subscription even when the setup-fee
    // charge was DECLINED. Stamping IsPaid there produced a paid invoice and a "your payment has been
    // received" receipt for money that never arrived, permanently mis-stating the ledger and the tax
    // remitted on it (2026-08-14 ultra-audit). Access is still granted immediately -- only the
    // financial record waits for the money. An unpaid invoice under an ACTIVE billing row raises no
    // dunning banner (GetBillingIssueAsync keys on Failed, or Pending older than 24h), so the
    // customer sees nothing odd while the sale webhook lands.
    private async Task<BillingChangeResult> ActivateSubscriptionBillingAsync(int userId, IPRO.Entities.Billing billing, IPRO.Entities.Invoice? invoice, string message, bool paymentConfirmed = false)
    {
        var now = DateTime.UtcNow;
        if (invoice == null)
        {
            invoice = (await _uow.Invoices.FindAsync(i => i.BillingId == billing.Id && !i.IsPaid))
                .OrderByDescending(i => i.IssuedAt)
                .FirstOrDefault();
        }

        if (invoice != null && paymentConfirmed)
        {
            invoice.IsPaid = true;
            _uow.Invoices.Update(invoice);
        }

        var activeSubscriptions = await _uow.Billings.FindAsync(b =>
            b.AgentUserId == userId && b.Status == BillingStatus.Active && b.Id != billing.Id);
        foreach (var subscription in activeSubscriptions)
        {
            // Same contract as the upgrade path (review H-1): only a confirmed PayPal stop may
            // mark the local row Cancelled. On failure the row stays Active -- visible, retryable.
            if (!await CancelPayPalSubscriptionAsync(subscription.PayPalSubscriptionId, "Replaced by a new IPRO subscription."))
            {
                _logger.LogError(
                    "Billing {BillingId} (PayPal {SubscriptionId}) could not be cancelled while activating a new subscription for agent {AgentUserId}; leaving it Active so the failure is visible and retryable.",
                    subscription.Id, subscription.PayPalSubscriptionId, userId);
                continue;
            }

            subscription.Status = BillingStatus.Cancelled;
            subscription.CancelledAt = now;
            _uow.Billings.Update(subscription);
        }

        billing.Status = BillingStatus.Active;
        billing.StartDate = now;
        billing.NextBillingDate = GetNextBillingDate(now, billing.Period);
        _uow.Billings.Update(billing);
        await SyncAgentPackageAsync(userId, billing.BillingRuleId);

        var change = await _uow.SubscriptionChanges.FirstOrDefaultAsync(c =>
            c.BillingId == billing.Id && c.AgentUserId == userId && c.Status == SubscriptionChangeStatus.Pending);
        if (change != null)
        {
            change.Status = SubscriptionChangeStatus.Applied;
            change.AppliedAt = now;
            _uow.SubscriptionChanges.Update(change);

            if (change.PromotionCodeId.HasValue)
            {
                await RecordPromoRedemptionAsync(change.PromotionCodeId.Value, userId, billing, now);
            }
        }

        await _uow.SaveChangesAsync();
        if (invoice != null && invoice.IsPaid)
        {
            await SendPaidInvoiceEmailAsync(invoice.Id);
        }

        return new BillingChangeResult
        {
            Success = true,
            Message = message
        };
    }

    public async Task<BillingChangeResult> ResumePaymentAsync(int userId, int invoiceId, string returnUrl, string cancelUrl)
    {
        if (!HasPayPalSettings())
        {
            return BillingChangeResult.Failed("PayPal is not configured yet. Please add PayPal ClientId and ClientSecret in Azure app settings.");
        }

        var invoice = await _uow.Invoices.FirstOrDefaultAsync(i =>
            i.Id == invoiceId && i.AgentUserId == userId && !i.IsPaid);
        if (invoice == null)
        {
            return BillingChangeResult.Failed("We could not find that unpaid invoice.");
        }

        var billing = await _uow.Billings.GetByIdAsync(invoice.BillingId);
        if (billing == null || billing.AgentUserId != userId || billing.Status != BillingStatus.Pending)
        {
            return BillingChangeResult.Failed("That invoice is not connected to a pending package payment anymore.");
        }

        var package = await _uow.BillingRules.GetByIdAsync(billing.BillingRuleId);
        if (package == null)
        {
            return BillingChangeResult.Failed("The package for this invoice could not be found.");
        }

        // Start a real SUBSCRIPTION, not a one-time order.
        //
        // This used to call CreatePayPalOrderAsync, which is a single payment. The agent paid one
        // month, the invoice was marked paid, the package activated -- and no subscription ever
        // existed, so nothing charged them again. Because ProcessDueSubscriptionChangesAsync only
        // applies downgrades and sends notices (all recurring money comes from PayPal's own engine),
        // that agent held the package indefinitely for a single month's payment. Same defect class
        // as C-2, reached through a different door: a Billing row with no PayPalSubscriptionId is
        // not a subscription, however paid it looks.
        //
        // The button says "Resume payment", which is a promise about recurring billing, so it has to
        // produce one. The cost is that the agent approves at PayPal rather than paying in one
        // click -- unavoidable, since PayPal requires approval to establish a billing agreement.
        var period = billing.Period;
        var billingRuleId = billing.BillingRuleId;

        // Void the stale attempt first. CreateSubscriptionAsync issues its own Billing row and
        // invoice, so leaving these behind would give the agent two pending rows for one package and
        // an orphaned invoice that dunning would keep chasing.
        //
        // Review L-1 flagged this ordering (a create failure after the void leaves nothing to
        // resume). Kept deliberately: the reverse order is worse for money -- create-then-void
        // means a void failure leaves TWO live approval links, and an agent completing the stale
        // one later ends up with two subscriptions. A failed create here costs one extra click
        // (Subscribe again from Billing); a stale approvable link can cost a double charge.
        if (!await CancelPendingPaymentAsync(userId, invoice.Id))
        {
            return BillingChangeResult.Failed(
                "We could not clear the previous payment attempt. Please refresh and try again, or contact support.");
        }

        // Delegate rather than reimplement: this is the same path a first-time subscribe takes, so
        // promotion codes, trial-package refusal, setup fees and plan creation all behave identically
        // instead of drifting from a second copy of the logic.
        return await CreateSubscriptionAsync(userId, billingRuleId, period, returnUrl, cancelUrl);
    }

    public async Task<bool> CancelPendingPaymentAsync(int userId, int invoiceId)
    {
        var invoice = await _uow.Invoices.FirstOrDefaultAsync(i =>
            i.Id == invoiceId && i.AgentUserId == userId && !i.IsPaid);
        if (invoice == null)
        {
            return false;
        }

        var billing = await _uow.Billings.GetByIdAsync(invoice.BillingId);
        if (billing == null || billing.AgentUserId != userId)
        {
            return false;
        }

        if (billing.Status == BillingStatus.Cancelled)
        {
            return true;
        }

        if (billing.Status != BillingStatus.Pending && billing.Status != BillingStatus.Failed)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        billing.Status = BillingStatus.Cancelled;
        billing.CancelledAt = now;
        _uow.Billings.Update(billing);

        var pendingChange = await _uow.SubscriptionChanges.FirstOrDefaultAsync(c =>
            c.BillingId == billing.Id && c.AgentUserId == userId && c.Status == SubscriptionChangeStatus.Pending);
        if (pendingChange != null)
        {
            pendingChange.Status = SubscriptionChangeStatus.Cancelled;
            pendingChange.CancelledAt = now;
            _uow.SubscriptionChanges.Update(pendingChange);
        }

        await _uow.SaveChangesAsync();
        return true;
    }

    public async Task<bool> CancelPendingPaymentByOrderAsync(int userId, string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return false;
        }

        var invoice = await _uow.Invoices.FirstOrDefaultAsync(i =>
            i.AgentUserId == userId && i.PayPalTransactionId == orderId && !i.IsPaid);

        return invoice != null && await CancelPendingPaymentAsync(userId, invoice.Id);
    }

    public async Task<bool> CancelSubscriptionAsync(int userId)
    {
        var subscription = await GetActiveSubscriptionAsync(userId);
        if (subscription == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(subscription.PayPalSubscriptionId))
        {
            // Bail out BEFORE touching the local row. Marking it Cancelled while PayPal is still
            // billing is the worst of both worlds: the agent is told they are cancelled, the portal
            // agrees, and the charges continue with nothing in our data pointing at them. Leaving
            // the row Active keeps the truth visible and lets a retry succeed later.
            if (!await CancelPayPalSubscriptionAsync(
                    subscription.PayPalSubscriptionId,
                    "Agent cancelled subscription from IPRO billing."))
            {
                return false;
            }
        }

        await CancelPendingChangesAsync(userId);
        subscription.Status = BillingStatus.Cancelled;
        subscription.CancelledAt = DateTime.UtcNow;
        _uow.Billings.Update(subscription);
        await _uow.SaveChangesAsync();
        return true;
    }

    public async Task<int> ProcessDueSubscriptionChangesAsync()
    {
        var now = DateTime.UtcNow;
        var dueChanges = await _uow.SubscriptionChanges.FindAsync(c =>
            c.Status == SubscriptionChangeStatus.Pending &&
            c.ChangeType == SubscriptionChangeType.Downgrade &&
            c.EffectiveDate <= now);

        var applied = 0;
        foreach (var agentId in dueChanges.Select(c => c.AgentUserId).Distinct())
        {
            applied += await ApplyDuePendingChangesAsync(agentId);
        }

        return applied;
    }

    public async Task<int> NotifyBillingIssuesAsync()
    {
        var problemBillings = await _uow.Billings.FindAsync(b =>
            b.Status == BillingStatus.Failed ||
            (b.Status == BillingStatus.Pending && b.CreatedAt <= DateTime.UtcNow.AddHours(-24)));

        var sent = 0;
        foreach (var billing in problemBillings.OrderBy(b => b.CreatedAt))
        {
            var activeSubscription = await GetActiveSubscriptionAsync(billing.AgentUserId);
            if (!IsActionableBillingIssue(billing, activeSubscription))
            {
                continue;
            }

            var invoice = (await _uow.Invoices.FindAsync(i =>
                    i.BillingId == billing.Id && !i.IsPaid))
                .OrderByDescending(i => i.IssuedAt)
                .FirstOrDefault();
            if (invoice == null)
            {
                continue;
            }

            if (await SendBillingIssueEmailAsync(billing, invoice))
            {
                sent++;
            }
        }

        var failedSubscriptionInvoices = (await _uow.Invoices.FindAsync(i =>
                !i.IsPaid && i.PayPalTransactionId.StartsWith("PAYPAL_FAILED:")))
            .OrderBy(i => i.IssuedAt)
            .ToList();
        foreach (var invoice in failedSubscriptionInvoices)
        {
            var billing = await _uow.Billings.GetByIdAsync(invoice.BillingId);
            if (billing == null || billing.Status != BillingStatus.Active)
            {
                continue;
            }

            if (await SendBillingIssueEmailAsync(billing, invoice))
            {
                sent++;
            }
        }

        return sent;
    }

    private async Task<bool> SendBillingIssueEmailAsync(IPRO.Entities.Billing billing, IPRO.Entities.Invoice invoice)
    {
        var alreadyLogged = await _uow.OperateLogs.FirstOrDefaultAsync(l =>
            l.AgentUserId == billing.AgentUserId &&
            l.Module == "Billing" &&
            l.Action == "BillingIssueEmail" &&
            l.Description == $"Billing:{billing.Id}:Invoice:{invoice.Id}");
        if (alreadyLogged != null)
        {
            return false;
        }

        var agent = await _uow.AgentUsers.GetByIdAsync(billing.AgentUserId);
        if (agent == null || string.IsNullOrWhiteSpace(agent.Email))
        {
            return false;
        }

        var package = await _uow.BillingRules.GetByIdAsync(billing.BillingRuleId);
        var fullName = $"{agent.FirstName} {agent.LastName}".Trim();
        var amount = $"{invoice.Total:N2} {invoice.Currency}";
        var packageName = package?.PackageName ?? "your IPRO package";
        var isFailedPayment = billing.Status == BillingStatus.Failed || IsPayPalFailedInvoice(invoice);
        var subject = isFailedPayment
            ? "Action required: IPRO payment failed"
            : "Reminder: IPRO payment pending";
        var html = BuildBillingIssueEmailHtml(fullName, packageName, amount, isFailedPayment ? BillingStatus.Failed : billing.Status);
        var text = $"Hello {fullName},\n\nWe need your attention on the payment for {packageName}. Amount: {amount}. Please sign in to your IPRO Agent Portal and go to Billing to correct the issue.\n\nIPRO Management";

        if (!await _email.SendAsync(agent.Email, fullName, subject, html, text))
        {
            return false;
        }

        await _uow.OperateLogs.AddAsync(new OperateLog
        {
            AgentUserId = billing.AgentUserId,
            Module = "Billing",
            Action = "BillingIssueEmail",
            Description = $"Billing:{billing.Id}:Invoice:{invoice.Id}",
            CreatedAt = DateTime.UtcNow
        });
        await _uow.SaveChangesAsync();
        return true;
    }

    public async Task<bool> HandleWebhookAsync(string eventType, string payload, PayPalWebhookHeaders headers, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        using var document = JsonDocument.Parse(payload);
        if (!await VerifyWebhookSignatureAsync(document.RootElement, headers))
        {
            return false;
        }

        var resource = document.RootElement.TryGetProperty("resource", out var resourceElement)
            ? resourceElement
            : document.RootElement;
        var subscriptionId = GetWebhookSubscriptionId(resource);
        var transactionId = GetWebhookString(resource, "id");

        return eventType switch
        {
            "BILLING.SUBSCRIPTION.ACTIVATED" => await HandleSubscriptionActivatedWebhookAsync(subscriptionId),
            "BILLING.SUBSCRIPTION.CANCELLED" => await HandleSubscriptionCancelledWebhookAsync(subscriptionId, BillingStatus.Cancelled),
            "BILLING.SUBSCRIPTION.SUSPENDED" => await HandleSubscriptionCancelledWebhookAsync(subscriptionId, BillingStatus.Failed),
            "BILLING.SUBSCRIPTION.EXPIRED" => await HandleSubscriptionCancelledWebhookAsync(subscriptionId, BillingStatus.Expired),
            "BILLING.SUBSCRIPTION.PAYMENT.FAILED" => await HandleSubscriptionPaymentFailedWebhookAsync(subscriptionId, transactionId),
            "PAYMENT.SALE.COMPLETED" => await HandleSubscriptionPaymentCompletedWebhookAsync(subscriptionId, transactionId, amount),
            _ => true
        };
    }

    public async Task<PayPalPlanSyncResult> SyncPayPalPlansAsync(int billingRuleId)
    {
        if (!HasPayPalSettings())
        {
            return PayPalPlanSyncResult.Failed("PayPal is not configured yet. Add PayPal ClientId and ClientSecret in Azure app settings.");
        }

        var package = await _uow.BillingRules.GetByIdAsync(billingRuleId);
        if (package == null)
        {
            return PayPalPlanSyncResult.Failed("Package could not be found.");
        }

        if (package.MonthlyPrice <= 0 && package.AnnualPrice <= 0)
        {
            return PayPalPlanSyncResult.Failed("PayPal plans were not created because this package has no monthly or annual recurring price.");
        }

        try
        {
            var productId = await CreatePayPalProductAsync(package);
            var monthlyPlanId = package.MonthlyPrice > 0
                ? await CreatePayPalPlanAsync(productId, package, BillingPeriod.Monthly)
                : string.Empty;
            var annualPlanId = package.AnnualPrice > 0
                ? await CreatePayPalPlanAsync(productId, package, BillingPeriod.Annually)
                : string.Empty;

            package.PayPalMonthlyPlanId = monthlyPlanId;
            package.PayPalAnnualPlanId = annualPlanId;
            // Snapshot the price each plan is frozen at (422b) -- the Packages screen compares
            // these against the editable prices and warns on divergence.
            package.PayPalMonthlyPlanPrice = string.IsNullOrEmpty(monthlyPlanId) ? null : package.MonthlyPrice;
            package.PayPalAnnualPlanPrice = string.IsNullOrEmpty(annualPlanId) ? null : package.AnnualPrice;
            _uow.BillingRules.Update(package);
            await _uow.SaveChangesAsync();

            return new PayPalPlanSyncResult
            {
                Success = true,
                ProductId = productId,
                MonthlyPlanId = monthlyPlanId,
                AnnualPlanId = annualPlanId,
                Message = "PayPal product and plans were created. Future subscribers will use the new plan IDs."
            };
        }
        catch (Exception ex)
        {
            return PayPalPlanSyncResult.Failed(ex.Message);
        }
    }

    // QA-only: creates a real PayPal Plan billed every 1 day instead of every month, so a manual
    // buyer-pass test can observe the actual unattended renewal path (PayPal's own clock firing a
    // charge, our webhook receiving it) inside a few days instead of waiting out a real monthly
    // cycle. Hard-refuses outside sandbox so this can never create a real-money daily-billing plan,
    // even by mistake later -- there is no live-mode path through this method at all.
    public async Task<PayPalPlanSyncResult> SyncDailyTestPlanAsync(int billingRuleId)
    {
        if (!_settings.IsSandbox)
        {
            return PayPalPlanSyncResult.Failed("Refused: daily test plans can only be created while PayPal__IsSandbox is true.");
        }

        if (!HasPayPalSettings())
        {
            return PayPalPlanSyncResult.Failed("PayPal is not configured yet. Add PayPal ClientId and ClientSecret in Azure app settings.");
        }

        var package = await _uow.BillingRules.GetByIdAsync(billingRuleId);
        if (package == null)
        {
            return PayPalPlanSyncResult.Failed("Package could not be found.");
        }

        if (package.MonthlyPrice <= 0)
        {
            return PayPalPlanSyncResult.Failed("A daily test plan was not created because this package has no monthly price to bill.");
        }

        try
        {
            var productId = await CreatePayPalProductAsync(package);
            var dailyPlanId = await CreatePayPalPlanAsync(productId, package, BillingPeriod.Monthly, intervalUnitOverride: "DAY");

            package.PayPalMonthlyPlanId = dailyPlanId;
            package.PayPalMonthlyPlanPrice = package.MonthlyPrice;
            _uow.BillingRules.Update(package);
            await _uow.SaveChangesAsync();

            return new PayPalPlanSyncResult
            {
                Success = true,
                ProductId = productId,
                MonthlyPlanId = dailyPlanId,
                Message = "PayPal sandbox product and a DAY-frequency plan were created."
            };
        }
        catch (Exception ex)
        {
            return PayPalPlanSyncResult.Failed(ex.Message);
        }
    }

    public async Task<BillingChangeResult> EmailPaidInvoiceAsync(int invoiceId, bool force = false)
    {
        var result = await SendPaidInvoiceEmailAsync(invoiceId, force);
        return result.Success
            ? new BillingChangeResult { Success = true, Message = "Invoice email sent." }
            : BillingChangeResult.Failed(result.Message);
    }

    private async Task<bool> HandleSubscriptionActivatedWebhookAsync(string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return false;
        }

        var billing = await _uow.Billings.FirstOrDefaultAsync(b => b.PayPalSubscriptionId == subscriptionId);
        if (billing == null)
        {
            return true;
        }

        if (billing.Status == BillingStatus.Active)
        {
            return true;
        }

        var invoice = (await _uow.Invoices.FindAsync(i => i.BillingId == billing.Id && !i.IsPaid))
            .OrderByDescending(i => i.IssuedAt)
            .FirstOrDefault();
        await ActivateSubscriptionBillingAsync(billing.AgentUserId, billing, invoice, "PayPal subscription activated.");
        return true;
    }

    private async Task<bool> HandleSubscriptionCancelledWebhookAsync(string subscriptionId, BillingStatus status)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return false;
        }

        var billing = await _uow.Billings.FirstOrDefaultAsync(b => b.PayPalSubscriptionId == subscriptionId);
        if (billing == null)
        {
            return true;
        }

        billing.Status = status;
        billing.CancelledAt = DateTime.UtcNow;
        _uow.Billings.Update(billing);
        await _uow.SaveChangesAsync();
        return true;
    }

    internal async Task<bool> HandleSubscriptionPaymentFailedWebhookAsync(string subscriptionId, string transactionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return false;
        }

        var billing = await _uow.Billings.FirstOrDefaultAsync(b => b.PayPalSubscriptionId == subscriptionId);
        if (billing == null)
        {
            return true;
        }

        // ONE open failure marker per billing, ever (audit item 422d). This used to mint a fresh
        // numbered unpaid invoice for EVERY failure delivery -- and PayPal retries a failing payment
        // on its own schedule and redelivers the webhook on top, so one bad card produced a pile of
        // phantom invoices consuming real invoice numbers, cluttering Revenue as unpaid rows, and
        // competing to be "oldest unpaid" when a success finally arrived. The marker invoice itself
        // is load-bearing (NotifyBillingIssuesAsync keys the dunning email on it), so the first
        // failure still creates it; subsequent failures append their transaction ids to the same
        // marker for the audit trail. When the retry eventually succeeds, the completed handler
        // settles this marker and the ledger shows one invoice: failed attempts, then paid.
        var invoices = await _uow.Invoices.FindAsync(i => i.BillingId == billing.Id);

        // Replay of a failure event we already recorded: acknowledge, change nothing.
        if (!string.IsNullOrWhiteSpace(transactionId) &&
            invoices.Any(i => (i.PayPalTransactionId ?? string.Empty).Contains(transactionId)))
        {
            return true;
        }

        var openMarker = invoices
            .Where(i => !i.IsPaid && (i.PayPalTransactionId ?? string.Empty).Contains("PAYPAL_FAILED:"))
            .OrderByDescending(i => i.IssuedAt)
            .FirstOrDefault();
        if (openMarker != null)
        {
            openMarker.PayPalTransactionId = $"{openMarker.PayPalTransactionId}, PAYPAL_FAILED:{transactionId}";
            _uow.Invoices.Update(openMarker);
            await _uow.SaveChangesAsync();
            return true;
        }

        var package = await _uow.BillingRules.GetByIdAsync(billing.BillingRuleId);
        var invoice = package == null
            ? await CreateInvoiceAsync(billing.Id, billing.AgentUserId, billing.Amount, false)
            : await CreateInvoiceAsync(billing.Id, billing.AgentUserId, package, billing.Period, billing.Amount, 0, false);
        invoice.PayPalTransactionId = $"PAYPAL_FAILED:{transactionId}";
        _uow.Invoices.Update(invoice);
        await _uow.SaveChangesAsync();
        return true;
    }

    internal async Task<bool> HandleSubscriptionPaymentCompletedWebhookAsync(string subscriptionId, string transactionId, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return false;
        }

        var billing = await _uow.Billings.FirstOrDefaultAsync(b => b.PayPalSubscriptionId == subscriptionId);
        if (billing == null)
        {
            return true;
        }

        // REPLAY GUARD, before any window logic (audit item 422c). PayPal redelivers events -- on
        // retry schedules that reach DAYS, and again when webhooks are resent by hand (2026-08-10,
        // three resends). The absorb window below only recognises a duplicate for 6 hours, so a
        // replay arriving later minted a second PAID invoice for the same charge. A transaction id
        // is globally unique at PayPal: if ANY invoice on this billing already records it (alone, in
        // a comma-joined list, or as a PAYPAL_FAILED marker), this delivery has been processed --
        // acknowledge and stop. Time plays no part, so a replay is idempotent forever.
        if (!string.IsNullOrWhiteSpace(transactionId))
        {
            var alreadyRecorded = (await _uow.Invoices.FindAsync(i => i.BillingId == billing.Id))
                .Any(i => (i.PayPalTransactionId ?? string.Empty).Contains(transactionId));
            if (alreadyRecorded)
            {
                return true;
            }
        }

        // Settle the unpaid invoice whose TOTAL matches what PayPal actually charged, and only fall
        // back to oldest-first when nothing matches (audit item 422d, second half). Oldest-first
        // alone could mark a failed-payment marker or a pending upgrade invoice as paid by an
        // unrelated charge, misstating which bill the money settled.
        var unpaidInvoices = (await _uow.Invoices.FindAsync(i => i.BillingId == billing.Id && !i.IsPaid))
            .OrderBy(i => i.IssuedAt)
            .ToList();
        var pendingInvoice = amount > 0
            ? unpaidInvoices.FirstOrDefault(i => Math.Abs(i.Total - amount) <= 0.02m) ?? unpaidInvoices.FirstOrDefault()
            : unpaidInvoices.FirstOrDefault();
        if (pendingInvoice != null)
        {
            pendingInvoice.IsPaid = true;
            // A failed-payment marker being settled by the successful retry keeps its failure ids
            // for the audit trail; the settling transaction is appended, not overwritten.
            pendingInvoice.PayPalTransactionId = string.IsNullOrWhiteSpace(pendingInvoice.PayPalTransactionId)
                ? transactionId
                : $"{pendingInvoice.PayPalTransactionId}, {transactionId}";
            _uow.Invoices.Update(pendingInvoice);
        }
        else
        {
            // The activation bundle produces TWO PayPal sales (setup fee, then the first cycle
            // minutes later) but signup writes ONE invoice covering both. Whichever sale arrives
            // first marks that invoice paid; without this check the second one looked like a new
            // billing cycle and got a duplicate invoice invented for it -- observed on the first
            // organic webhook run (2026-08-10, I-RYCAW2SJMH73): the $172.47 setup sale became a
            // spurious "$150.01 monthly recurring subscription" invoice on top of the $218.45
            // signup invoice that already covered it. Double paper, not double charge.
            //
            // Rule: a sale that lands within the activation window (an invoice for this billing was
            // settled in the last 6 hours) for NO MORE than that invoice's total is part of the
            // bundle already invoiced -- record its transaction id against the invoice and stop. The
            // window is hours, not a day, so tomorrow's genuine daily cycle (24h away) can never
            // match; monthly cycles are further still.
            //
            // "No more than", not "less than": an UPGRADE's invoice covers exactly one charge (the
            // prorated difference), so the sale EQUALS the invoice total. The original strict < was
            // written for the signup bundle where each sale is a fraction of the invoice, and let
            // the first upgrade through this path mint a duplicate (IPRO-2026-000011, 2026-08-11:
            // two $20.30 invoice emails for one $20.30 charge -- owner-reported).
            var recentlySettled = (await _uow.Invoices.FindAsync(i => i.BillingId == billing.Id && i.IsPaid))
                .Where(i => i.IssuedAt > DateTime.UtcNow.AddHours(-6) && amount <= i.Total)
                .OrderByDescending(i => i.IssuedAt)
                .FirstOrDefault();
            if (recentlySettled != null)
            {
                if (!string.IsNullOrWhiteSpace(transactionId) &&
                    !(recentlySettled.PayPalTransactionId ?? string.Empty).Contains(transactionId))
                {
                    // Keep every settling transaction on the invoice so the audit trail is honest.
                    recentlySettled.PayPalTransactionId = string.IsNullOrWhiteSpace(recentlySettled.PayPalTransactionId)
                        ? transactionId
                        : $"{recentlySettled.PayPalTransactionId}, {transactionId}";
                    _uow.Invoices.Update(recentlySettled);
                    await _uow.SaveChangesAsync();
                }
                return true;
            }

            var package = await _uow.BillingRules.GetByIdAsync(billing.BillingRuleId);

            // PayPal reports the GROSS amount it charged -- the plan price is built tax-inclusive,
            // so the HST is already inside it. This used to be passed to CreateInvoiceAsync as the
            // NET line amount, which added tax a second time: the first webhook-created invoice
            // (IPRO-2026-000012, 2026-08-10) showed $45.20 + $5.88 HST = $51.08 for a charge that
            // was actually $45.20. Found by the owner reading the invoice.
            //
            // So: back the net out with the agent's own rate and let the normal invoice path
            // recompute the tax, which guarantees the invoice total equals what PayPal charged.
            // billing.Amount (the fallback when the event carries no amount) is already stored net
            // and must NOT be de-taxed.
            decimal recurringAmount;
            if (amount > 0)
            {
                var taxProbe = await CalculateTaxAsync(billing.AgentUserId, amount);
                recurringAmount = taxProbe.Rate > 0
                    ? Math.Round(amount / (1 + taxProbe.Rate), 2)
                    : amount;

                // Dividing a rounded gross can land a cent away from the advertised price (PayPal
                // billed Quebec's 14.975% as 14.98% and the de-tax printed $150.01 against an
                // advertised $150 -- owner-reported, 2026-08-10). billing.Amount IS the advertised
                // net the agent signed up at, so when the de-tax lands within pennies of it, the
                // stored net is the truth and the invoice must say it exactly.
                if (billing.Amount > 0 && Math.Abs(recurringAmount - billing.Amount) <= 0.02m)
                {
                    recurringAmount = billing.Amount;
                }
            }
            else
            {
                recurringAmount = billing.Amount;
            }

            var invoice = package == null
                ? await CreateInvoiceAsync(billing.Id, billing.AgentUserId, recurringAmount, true)
                : await CreateInvoiceAsync(billing.Id, billing.AgentUserId, package, billing.Period, recurringAmount, 0, true);
            invoice.PayPalTransactionId = transactionId;
            _uow.Invoices.Update(invoice);
            pendingInvoice = invoice;
        }

        // NEVER resurrect a subscription the agent (or we) already ended. This branch had no status
        // guard, so a late webhook retry could flip a Cancelled/Expired/Failed row back to Active
        // with a fresh NextBillingDate -- e.g. the renewal sale fires, our verification call blips
        // and returns Unauthorized, PayPal queues a retry, the agent cancels the next day, and the
        // retry lands afterwards and reinstates full access permanently, because nothing else ever
        // expires an Active row (2026-08-14 ultra-audit). A sale arriving for an ended subscription
        // is a real anomaly: record it against the ledger, but do not re-grant access.
        if (billing.Status is BillingStatus.Cancelled or BillingStatus.Expired)
        {
            _logger.LogError(
                "PAYMENT.SALE.COMPLETED {TransactionId} arrived for agent {AgentId}'s {Status} subscription " +
                "{SubscriptionId}. Recording the invoice but NOT reactivating -- verify at PayPal whether " +
                "this subscription is genuinely still billing.",
                transactionId, billing.AgentUserId, billing.Status, billing.PayPalSubscriptionId);
        }
        else
        {
            if (billing.Status != BillingStatus.Active)
            {
                billing.Status = BillingStatus.Active;
                billing.StartDate = DateTime.UtcNow;
            }
            await SyncAgentPackageAsync(billing.AgentUserId, billing.BillingRuleId);
            billing.NextBillingDate = GetNextBillingDate(DateTime.UtcNow, billing.Period);
        }

        _uow.Billings.Update(billing);
        await _uow.SaveChangesAsync();
        if (pendingInvoice != null)
        {
            await SendPaidInvoiceEmailAsync(pendingInvoice.Id);
        }

        return true;
    }

    public async Task<List<IPRO.Entities.Invoice>> GetInvoicesAsync(int userId)
    {
        var invoices = await _uow.Invoices.FindAsync(i => i.AgentUserId == userId);
        var invoiceList = invoices.OrderByDescending(i => i.IssuedAt).ToList();
        foreach (var invoice in invoiceList)
        {
            var billing = await _uow.Billings.GetByIdAsync(invoice.BillingId);
            if (billing != null)
            {
                invoice.Billing = billing;
            }

            invoice.LineItems = (await _uow.InvoiceLineItems.FindAsync(i => i.InvoiceId == invoice.Id))
                .OrderBy(i => i.SortOrder)
                .ToList();
        }

        return invoiceList;
    }

    public async Task<IPRO.Entities.Invoice> GenerateInvoiceAsync(int userId, decimal amount, string description)
    {
        var activeSubscription = await GetActiveSubscriptionAsync(userId);
        if (activeSubscription == null)
        {
            throw new InvalidOperationException("Cannot generate an invoice without an active subscription.");
        }

        var package = await _uow.BillingRules.GetByIdAsync(activeSubscription.BillingRuleId);
        if (package == null)
        {
            return await CreateInvoiceAsync(activeSubscription.Id, userId, amount, false);
        }

        return await CreateInvoiceAsync(activeSubscription.Id, userId, package, activeSubscription.Period, amount, 0, false);
    }

    public async Task<List<BillingRule>> GetPackagesAsync()
    {
        // Trial packages are invitation-only (see TrialInviteCode) - never offered as a normal
        // subscribe/upgrade/downgrade choice, or an agent could just "subscribe" to the free
        // trial package directly and get a genuinely active (never-expiring) Billing row for it.
        // Hidden test packages (QA daily-billing sandbox plans) are reachable only by a direct
        // billingRuleId POST -- never rendered here, or a real agent's upgrade picker could offer
        // them.
        var packages = await _uow.BillingRules.FindAsync(p => p.IsActive && !p.IsTrialPackage && !p.IsHiddenTestPackage);
        return packages.OrderBy(p => p.MonthlyPrice <= 0 ? decimal.MaxValue : p.MonthlyPrice).ToList();
    }

    // Keeps AgentUser.PackageId in step with whatever package the agent is actually on.
    //
    // Nothing in the billing flow used to write this field -- it was set once at registration and then
    // frozen, so an agent who upgraded still read as their signup package forever. Reported 2026-08-06:
    // My Profile showed "IPro Silver" for an agent who had upgraded to Gold and then Platinum the same
    // day.
    //
    // Entitlements were never wrong, because ResolveBillingRuleIdAsync consults the active Billing row
    // first. But that same method falls back to PackageId when there is NO active billing, so a stale
    // value is a real hazard, not just a cosmetic one: a lapsed-then-restored Platinum agent would have
    // fallen back to Silver features. Writing it here makes the fallback truthful.
    private async Task SyncAgentPackageAsync(int userId, int billingRuleId)
    {
        if (billingRuleId <= 0) return;

        var agent = await _uow.AgentUsers.GetByIdAsync(userId);
        if (agent == null || agent.PackageId == billingRuleId) return;

        agent.PackageId = billingRuleId;
        _uow.AgentUsers.Update(agent);
    }

    private async Task<BillingChangeResult> BeginPaidChangeAsync(
        int userId,
        BillingRule? currentPackage,
        BillingRule requestedPackage,
        BillingPeriod period,
        SubscriptionChangeType changeType,
        DateTime effectiveDate,
        decimal credit,
        decimal charge,
        decimal amountDue,
        string returnUrl,
        string cancelUrl,
        int? currentBillingId = null,
        DateTime? nextBillingDate = null,
        bool includeSetupFee = false,
        decimal? overrideAmount = null,
        string? overridePlanId = null,
        decimal? overrideSetupFee = null,
        int? promotionCodeId = null)
    {
        if (!HasPayPalSettings())
        {
            return BillingChangeResult.Failed("PayPal is not configured yet. Please add PayPal ClientId and ClientSecret in Azure app settings.");
        }

        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = userId,
            BillingRuleId = requestedPackage.Id,
            Amount = overrideAmount ?? GetAmount(requestedPackage, period),
            Currency = "CAD",
            Status = BillingStatus.Pending,
            Period = period,
            StartDate = effectiveDate,
            NextBillingDate = nextBillingDate ?? GetNextBillingDate(effectiveDate, period),
            PayPalPlanId = overridePlanId ?? GetPayPalPlanId(requestedPackage, period),
            CreatedAt = DateTime.UtcNow
        };

        await _uow.Billings.AddAsync(billing);
        await _uow.SaveChangesAsync();

        // Fallback honours the package waiver too, so a future call site that forgets to pass
        // overrideSetupFee still cannot charge a fee the pricing page said was waived.
        var setupFee = includeSetupFee ? (overrideSetupFee ?? requestedPackage.EffectiveSetupFee(DateTime.UtcNow)) : 0;
        var invoice = await CreateInvoiceAsync(billing.Id, userId, requestedPackage, period, amountDue, setupFee, false);

        await _uow.SubscriptionChanges.AddAsync(new SubscriptionChange
        {
            AgentUserId = userId,
            CurrentBillingRuleId = currentPackage?.Id,
            RequestedBillingRuleId = requestedPackage.Id,
            BillingId = billing.Id,
            PromotionCodeId = promotionCodeId,
            ChangeType = changeType,
            Status = SubscriptionChangeStatus.Pending,
            Period = period,
            EffectiveDate = effectiveDate,
            ProratedCredit = credit,
            ProratedCharge = charge,
            AmountDue = invoice.Total
        });
        await _uow.SaveChangesAsync();

        // Fully comped by a PERMANENT promo code (recurring price and setup fee both $0 forever) -
        // PayPal's Subscriptions API cannot represent a free-forever recurring plan, so there is
        // nothing to check out; activate directly via the same path a real payment confirmation takes.
        //
        // The gate is the EMPTY PLAN ID, not the amounts. CreateSubscriptionAsync sets overridePlanId
        // to string.Empty only when isFullyComped (which additionally requires RecurringDurationCycles
        // == null); a time-limited "first 3 months free" promo gets a REAL plan that charges full price
        // from cycle 4. Keying on `billing.Amount <= 0 && setupFee <= 0` matched that case too and
        // activated it permanently for $0 with no PayPal subscription behind it -- nothing in this
        // system bills anyone, so the agent kept the package free forever (2026-08-14 ultra-audit).
        if (changeType == SubscriptionChangeType.Subscribe
            && promotionCodeId.HasValue
            && string.IsNullOrWhiteSpace(billing.PayPalPlanId)
            && billing.Amount <= 0
            && setupFee <= 0)
        {
            // paymentConfirmed: true is correct here and only here -- the total is genuinely $0, so
            // there is no money in flight and the invoice is settled the moment it is issued.
            return await ActivateSubscriptionBillingAsync(userId, billing, invoice, "Your promotion code covers this package at no cost - your account is active now.", paymentConfirmed: true);
        }

        // UPGRADES MUST CREATE A REAL SUBSCRIPTION, NOT A ONE-OFF ORDER (2026-08-05 audit, Critical)
        //
        // This gate used to read `changeType == Subscribe`, so an upgrade fell through to the
        // CreatePayPalOrderAsync path below: the agent paid the prorated difference once, the old
        // subscription was cancelled on capture, and the new Billing row went Active with an EMPTY
        // PayPalSubscriptionId. Nothing in this system bills anyone -- SubscriptionBillingJob only
        // applies due downgrades and sends notices, and all recurring money arrives through PayPal's
        // own engine via PAYMENT.SALE.COMPLETED. So no subscription meant no further revenue, for the
        // life of the account. Timed near the end of a cycle the prorated difference rounds to zero,
        // which made the top tier free forever.
        //
        // An upgrade now starts a genuine subscription on the new plan, with the prorated difference
        // charged as its setup fee -- one up-front charge plus correct recurring billing thereafter.
        // The old subscription is still cancelled only AFTER the new one activates (CapturePaymentAsync),
        // so an abandoned approval leaves the agent on what they already had.
        var startsSubscription = changeType == SubscriptionChangeType.Subscribe
                              || changeType == SubscriptionChangeType.Upgrade;

        if (startsSubscription && !string.IsNullOrWhiteSpace(billing.PayPalPlanId))
        {
            // For an upgrade the prorated amount is the up-front charge; the plan carries the
            // recurring price from the next cycle on.
            var subscriptionSetupFee = changeType == SubscriptionChangeType.Upgrade
                ? setupFee + Math.Max(0, amountDue)
                : setupFee;

            // Upgrades defer the first recurring charge to the date the agent has already paid up to.
            // A new subscription passes null and bills immediately, which is correct for a signup.
            var subscriptionStart = changeType == SubscriptionChangeType.Upgrade ? nextBillingDate : null;

            PayPalSubscriptionResult subscription;
            try
            {
                subscription = await CreatePayPalSubscriptionAsync(invoice, requestedPackage, period, subscriptionSetupFee, returnUrl, cancelUrl, billing.PayPalPlanId, subscriptionStart);
            }
            catch (Exception ex)
            {
                await MarkPendingBillingFailedAsync(billing);
                return BillingChangeResult.Failed($"PayPal subscription could not be started: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(subscription.ApprovalUrl))
            {
                await MarkPendingBillingFailedAsync(billing);
                return BillingChangeResult.Failed("PayPal did not return a subscription approval link.");
            }

            billing.PayPalSubscriptionId = subscription.SubscriptionId;
            _uow.Billings.Update(billing);
            invoice.PayPalTransactionId = subscription.SubscriptionId;
            _uow.Invoices.Update(invoice);
            await _uow.SaveChangesAsync();

            return new BillingChangeResult
            {
                Success = true,
                RequiresPayment = true,
                ApprovalUrl = subscription.ApprovalUrl,
                InvoiceId = invoice.Id,
                AmountDue = invoice.Total,
                Message = "Please approve the recurring PayPal subscription to activate this package."
            };
        }

        // Reaching here with startsSubscription means the plan id was empty and the change was NOT a
        // permanent comp -- refuse rather than fall through to the one-time order below. That
        // fall-through is the same defect the 2026-08-05 audit closed for upgrades, still reachable
        // through two doors (2026-08-14 ultra-audit): a package whose PayPal plans were never synced
        // (they start empty and syncing is a manual Super Admin button), and Annually on a package
        // with no annual price (SyncPayPalPlansAsync deliberately stores an empty annual plan id).
        // Both produced a single capture, an Active billing row with no PayPalSubscriptionId, and a
        // package held indefinitely for one payment.
        if (startsSubscription)
        {
            await MarkPendingBillingFailedAsync(billing);
            _logger.LogError(
                "Refusing subscription for agent {AgentId} on package {PackageId} ({Period}): no PayPal plan id. " +
                "Sync the package's PayPal plans in Super Admin -> Packages before anyone can subscribe.",
                userId, requestedPackage.Id, period);
            return BillingChangeResult.Failed(
                "This package is not ready for checkout yet - its PayPal plan has not been set up. " +
                "Please contact support; no payment has been taken.");
        }

        PayPalOrderResult order;
        try
        {
            order = await CreatePayPalOrderAsync(invoice, requestedPackage.PackageName, returnUrl, cancelUrl);
        }
        catch (Exception ex)
        {
            billing.Status = BillingStatus.Failed;
            _uow.Billings.Update(billing);

            var pendingChange = await _uow.SubscriptionChanges.FirstOrDefaultAsync(c =>
                c.BillingId == billing.Id && c.Status == SubscriptionChangeStatus.Pending);
            if (pendingChange != null)
            {
                pendingChange.Status = SubscriptionChangeStatus.Cancelled;
                pendingChange.CancelledAt = DateTime.UtcNow;
                _uow.SubscriptionChanges.Update(pendingChange);
            }

            await _uow.SaveChangesAsync();
            return BillingChangeResult.Failed($"PayPal checkout could not be started: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(order.ApprovalUrl))
        {
            billing.Status = BillingStatus.Failed;
            _uow.Billings.Update(billing);
            await _uow.SaveChangesAsync();
            return BillingChangeResult.Failed("PayPal did not return an approval link.");
        }

        invoice.PayPalTransactionId = order.OrderId;
        _uow.Invoices.Update(invoice);
        await _uow.SaveChangesAsync();

        return new BillingChangeResult
        {
            Success = true,
            RequiresPayment = true,
            ApprovalUrl = order.ApprovalUrl,
            InvoiceId = invoice.Id,
            AmountDue = invoice.Total,
            Message = "Please complete payment in PayPal to activate this package change."
        };
    }

    // ApplyUpgradeWithoutPaymentAsync was DELETED on 2026-08-05.
    //
    // It handled the "prorated difference rounds to zero" upgrade by cancelling the agent's paid
    // PayPal subscription and creating a local Billing row marked Active with no PayPalSubscriptionId
    // behind it. Since nothing in this codebase charges anyone -- recurring money only ever arrives
    // through PayPal's own engine -- that permanently ended billing for the account, and an upgrade
    // timed near the end of a cycle handed over the top package for nothing.
    //
    // Deliberately not kept as dead code: it is a working implementation of the exact defect, one
    // call site away from returning. Zero-due upgrades now go through BeginPaidChangeAsync like every
    // other upgrade, which starts a real subscription that the agent approves at PayPal.

    private async Task ScheduleDowngradeAsync(int userId, IPRO.Entities.Billing currentSubscription, BillingRule currentPackage, BillingRule requestedPackage, BillingPeriod period)
    {
        await CancelPendingChangesAsync(userId);

        var effectiveDate = currentSubscription.NextBillingDate ?? GetNextBillingDate(DateTime.UtcNow, currentSubscription.Period);
        await _uow.SubscriptionChanges.AddAsync(new SubscriptionChange
        {
            AgentUserId = userId,
            CurrentBillingRuleId = currentPackage.Id,
            RequestedBillingRuleId = requestedPackage.Id,
            BillingId = currentSubscription.Id,
            ChangeType = SubscriptionChangeType.Downgrade,
            Status = SubscriptionChangeStatus.Pending,
            Period = period,
            EffectiveDate = effectiveDate,
            ProratedCredit = 0,
            ProratedCharge = 0,
            AmountDue = 0
        });

        await _uow.SaveChangesAsync();
    }

    private async Task CancelPendingChangesAsync(int userId)
    {
        var pendingChanges = await _uow.SubscriptionChanges.FindAsync(c =>
            c.AgentUserId == userId && c.Status == SubscriptionChangeStatus.Pending);

        foreach (var change in pendingChanges)
        {
            change.Status = SubscriptionChangeStatus.Cancelled;
            change.CancelledAt = DateTime.UtcNow;
            _uow.SubscriptionChanges.Update(change);
        }

        var pendingBillings = await _uow.Billings.FindAsync(b =>
            b.AgentUserId == userId && b.Status == BillingStatus.Pending);
        foreach (var billing in pendingBillings)
        {
            billing.Status = BillingStatus.Cancelled;
            billing.CancelledAt = DateTime.UtcNow;
            _uow.Billings.Update(billing);
        }

        await _uow.SaveChangesAsync();
    }

    private async Task<int> ApplyDuePendingChangesAsync(int userId)
    {
        var now = DateTime.UtcNow;
        var dueChanges = await _uow.SubscriptionChanges.FindAsync(c =>
            c.AgentUserId == userId &&
            c.Status == SubscriptionChangeStatus.Pending &&
            c.ChangeType == SubscriptionChangeType.Downgrade &&
            c.EffectiveDate <= now);

        var applied = 0;
        foreach (var change in dueChanges.OrderBy(c => c.EffectiveDate))
        {
            var requestedPackage = await _uow.BillingRules.GetByIdAsync(change.RequestedBillingRuleId);
            if (requestedPackage == null)
            {
                change.Status = SubscriptionChangeStatus.Cancelled;
                change.CancelledAt = now;
                _uow.SubscriptionChanges.Update(change);
                continue;
            }

            var activeSubscriptions = await _uow.Billings.FindAsync(b =>
                b.AgentUserId == userId && b.Status == BillingStatus.Active);
            var allCancelled = true;
            foreach (var subscription in activeSubscriptions)
            {
                // Review H-1: only a confirmed PayPal stop may mark the row Cancelled. Unlike the
                // upgrade paths, nothing has been charged yet here, so a failure can simply leave
                // the change Pending -- this job runs hourly and retries it naturally.
                if (!await CancelPayPalSubscriptionAsync(subscription.PayPalSubscriptionId, "Replaced by a scheduled IPRO package downgrade."))
                {
                    _logger.LogError(
                        "Billing {BillingId} (PayPal {SubscriptionId}) could not be cancelled for agent {AgentUserId}'s scheduled downgrade; leaving the change Pending to retry next run.",
                        subscription.Id, subscription.PayPalSubscriptionId, userId);
                    allCancelled = false;
                    continue;
                }

                subscription.Status = BillingStatus.Cancelled;
                subscription.CancelledAt = now;
                _uow.Billings.Update(subscription);
            }

            if (!allCancelled)
            {
                // Persist the rows that did cancel, keep the change Pending, retry next run.
                await _uow.SaveChangesAsync();
                continue;
            }

            // The old (higher-priced) subscription is genuinely cancelled above. The new,
            // downgraded package is deliberately NOT auto-activated here: PayPal has no way to
            // create a new subscription without the buyer re-approving on PayPal's own page, so
            // marking a Billing row Active with no real PayPal linkage would grant free permanent
            // access to the lower tier - this is the exact bug this fix closes (H-7, security
            // audit 2026-07-24). Instead the agent is left with no active subscription - the
            // existing entitlement gate (IsAccessGatedAsync) naturally redirects every request to
            // /Billing, same as any other lapsed-billing agent - and prompted by email to finish
            // subscribing to the new package there, which goes through the exact same PayPal
            // approval flow (CreateSubscriptionAsync) a brand-new signup already uses.
            change.Status = SubscriptionChangeStatus.Applied;
            change.AppliedAt = now;
            _uow.SubscriptionChanges.Update(change);
            await _uow.SaveChangesAsync();

            await SendDowngradeReadyToCompleteEmailAsync(userId, requestedPackage);
            applied++;
        }

        return applied;
    }

    private async Task SendDowngradeReadyToCompleteEmailAsync(int userId, BillingRule requestedPackage)
    {
        var agent = await _uow.AgentUsers.GetByIdAsync(userId);
        if (agent == null || string.IsNullOrWhiteSpace(agent.Email)) return;

        var fullName = $"{agent.FirstName} {agent.LastName}".Trim();
        var billingUrl = BuildBillingPageUrl();
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#193f82;color:white"><h1 style="margin:0;font-size:24px">IPRO Advisers</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p>Hi {System.Net.WebUtility.HtmlEncode(fullName)},</p>
                <p>Your scheduled downgrade to <strong>{System.Net.WebUtility.HtmlEncode(requestedPackage.PackageName)}</strong> is now in effect, and your previous subscription has been cancelled.</p>
                <p>One step left: PayPal requires you to re-approve a new subscription any time the plan changes, so please visit Billing to finish subscribing to {System.Net.WebUtility.HtmlEncode(requestedPackage.PackageName)} at its lower price. Until then, your account will be limited to the Billing page.</p>
                <p><a href="{billingUrl}" style="display:inline-block;padding:11px 18px;background:#193f82;color:white;text-decoration:none;border-radius:6px">Complete My Subscription</a></p>
              </div>
            </div>
            """;
        await _email.SendDetailedAsync(agent.Email, fullName, "Action needed: complete your IPRO Advisers plan change", html);
    }

    private string BuildBillingPageUrl() => $"{IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration)}/Billing";

    private async Task MarkPaymentFailedAsync(int userId, int billingId)
    {
        var billing = await _uow.Billings.GetByIdAsync(billingId);
        if (billing == null || billing.AgentUserId != userId || billing.Status != BillingStatus.Pending)
        {
            return;
        }

        var now = DateTime.UtcNow;
        billing.Status = BillingStatus.Failed;
        billing.CancelledAt = now;
        _uow.Billings.Update(billing);

        var pendingChange = await _uow.SubscriptionChanges.FirstOrDefaultAsync(c =>
            c.BillingId == billing.Id && c.AgentUserId == userId && c.Status == SubscriptionChangeStatus.Pending);
        if (pendingChange != null)
        {
            pendingChange.Status = SubscriptionChangeStatus.Cancelled;
            pendingChange.CancelledAt = now;
            _uow.SubscriptionChanges.Update(pendingChange);
        }

        await _uow.SaveChangesAsync();
    }

    private async Task MarkPendingBillingFailedAsync(IPRO.Entities.Billing billing)
    {
        billing.Status = BillingStatus.Failed;
        billing.CancelledAt = DateTime.UtcNow;
        _uow.Billings.Update(billing);

        var pendingChange = await _uow.SubscriptionChanges.FirstOrDefaultAsync(c =>
            c.BillingId == billing.Id && c.Status == SubscriptionChangeStatus.Pending);
        if (pendingChange != null)
        {
            pendingChange.Status = SubscriptionChangeStatus.Cancelled;
            pendingChange.CancelledAt = DateTime.UtcNow;
            _uow.SubscriptionChanges.Update(pendingChange);
        }

        await _uow.SaveChangesAsync();
    }

    private async Task<IPRO.Entities.Invoice> CreateInvoiceAsync(int billingId, int userId, decimal amount, bool isPaid)
    {
        var billing = await _uow.Billings.GetByIdAsync(billingId);
        var package = billing == null ? null : await _uow.BillingRules.GetByIdAsync(billing.BillingRuleId);
        if (billing == null || package == null)
        {
            return await CreateInvoiceWithLinesAsync(billingId, userId, amount, 0, 0, string.Empty, isPaid, new[]
            {
                new InvoiceLineDraft("IPRO billing charge", amount)
            });
        }

        return await CreateInvoiceAsync(billingId, userId, package, billing.Period, amount, 0, isPaid);
    }

    private async Task<IPRO.Entities.Invoice> CreateInvoiceAsync(int billingId, int userId, BillingRule package, BillingPeriod period, decimal recurringAmount, decimal setupFee, bool isPaid)
    {
        var lineItems = new List<InvoiceLineDraft>();
        if (recurringAmount > 0)
        {
            lineItems.Add(new InvoiceLineDraft($"{package.PackageName} {FormatPeriod(period)} recurring subscription", recurringAmount));
        }

        if (setupFee > 0)
        {
            lineItems.Add(new InvoiceLineDraft($"{package.PackageName} one-time setup fee", setupFee));
        }

        if (lineItems.Count == 0)
        {
            lineItems.Add(new InvoiceLineDraft($"{package.PackageName} subscription adjustment", 0));
        }

        var subtotal = lineItems.Sum(i => i.Amount);
        var tax = await CalculateTaxAsync(userId, subtotal);
        return await CreateInvoiceWithLinesAsync(billingId, userId, subtotal, tax.Amount, tax.Rate, tax.Region, isPaid, lineItems);
    }

    private async Task<IPRO.Entities.Invoice> CreateInvoiceWithLinesAsync(int billingId, int userId, decimal subtotal, decimal taxAmount, decimal taxRate, string taxRegion, bool isPaid, IEnumerable<InvoiceLineDraft> lines)
    {
        var issuedAt = DateTime.UtcNow;

        // The bill-to is frozen onto the invoice at issue time: invoices are retained after the agent
        // is deleted, so they must render without an AgentUsers row.
        var agent = await _uow.AgentUsers.GetByIdAsync(userId);
        var billToName = agent == null ? string.Empty : $"{agent.FirstName} {agent.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(billToName)) billToName = agent?.UserName ?? string.Empty;
        var provincePostal = agent == null ? string.Empty : $"{agent.Province} {agent.PostalCode}".Trim();
        var addressLines = new[] { agent?.CompanyAddress, agent?.City, provincePostal, agent?.Country }
            .Where(line => !string.IsNullOrWhiteSpace(line));

        var invoice = new IPRO.Entities.Invoice
        {
            BillingId = billingId,
            AgentUserId = userId,
            SubTotal = subtotal,
            TaxAmount = taxAmount,
            TaxRate = taxRate,
            TaxRegion = taxRegion,
            Total = subtotal + taxAmount,
            Currency = "CAD",
            IssuedAt = issuedAt,
            IsPaid = isPaid,
            BillToName = billToName,
            BillToCompany = agent?.CompanyName ?? string.Empty,
            BillToEmail = agent?.Email ?? string.Empty,
            BillToAddress = string.Join("\n", addressLines!)
        };

        await _uow.Invoices.AddAsync(invoice);

        // Number generation is MAX(existing)+1 read from committed rows, so two concurrent writers
        // (a PayPal webhook racing SubscriptionBillingJob, or the app scaled past one instance) can
        // both compute the same number. The unique index on InvoiceNumber then makes the LOSER throw
        // -- and this code runs AFTER PayPal has captured the money, so the old behaviour didn't
        // produce a duplicate number, it produced a PAID CHARGE WITH NO INVOICE ROW: the same end
        // state as the 2026-08-14 invoice loss by a different route (auditor 5, F12; observed live
        // on 2026-08-10 when two webhook resends raced). The index stays the arbiter; the loser now
        // re-reads and takes the next number instead of losing the invoice.
        for (var attempt = 1; ; attempt++)
        {
            invoice.InvoiceNumber = await GenerateInvoiceNumberAsync(issuedAt);
            try
            {
                await _uow.SaveChangesAsync();
                break;
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
                when (attempt <= 5 && IsDuplicateInvoiceNumber(ex))
            {
                // Another writer committed this number between our MAX() read and the insert. The
                // entity is still tracked as Added; loop to take a fresh number and save again.
            }
        }

        var sortOrder = 10;
        foreach (var line in lines)
        {
            await _uow.InvoiceLineItems.AddAsync(new InvoiceLineItem
            {
                InvoiceId = invoice.Id,
                Description = line.Description,
                Amount = line.Amount,
                SortOrder = sortOrder
            });
            sortOrder += 10;
        }

        if (taxAmount > 0)
        {
            await _uow.InvoiceLineItems.AddAsync(new InvoiceLineItem
            {
                InvoiceId = invoice.Id,
                Description = $"{taxRegion} tax ({taxRate:P3})",
                Amount = taxAmount,
                SortOrder = sortOrder
            });
        }

        await _uow.SaveChangesAsync();
        return invoice;
    }

    // True when a DbUpdateException is specifically the InvoiceNumber unique index rejecting a
    // duplicate -- the one failure the retry loop above may safely absorb. Anything else re-throws.
    private static bool IsDuplicateInvoiceNumber(Microsoft.EntityFrameworkCore.DbUpdateException ex) =>
        ex.InnerException is MySqlConnector.MySqlException mysql
        && mysql.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyEntry
        && mysql.Message.Contains("InvoiceNumber", StringComparison.OrdinalIgnoreCase);

    private async Task<string> GenerateInvoiceNumberAsync(DateTime issuedAt)
    {
        var prefix = $"IPRO-{issuedAt:yyyy}-";
        var existingInvoices = await _uow.Invoices.FindAsync(i => i.InvoiceNumber.StartsWith(prefix));
        var nextNumber = existingInvoices
            .Select(i => int.TryParse(i.InvoiceNumber[prefix.Length..], out var number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        string invoiceNumber;
        do
        {
            invoiceNumber = $"{prefix}{nextNumber:000000}";
            nextNumber++;
        }
        while (await _uow.Invoices.FirstOrDefaultAsync(i => i.InvoiceNumber == invoiceNumber) != null);

        return invoiceNumber;
    }

    private async Task<TaxCalculation> CalculateTaxAsync(int userId, decimal taxableAmount)
    {
        if (taxableAmount <= 0)
        {
            return new TaxCalculation(0, 0, "No tax");
        }

        var agent = await _uow.AgentUsers.GetByIdAsync(userId);
        if (agent == null)
        {
            return new TaxCalculation(0, 0, "No tax");
        }

        var country = (agent.Country ?? string.Empty).Trim();
        if (country.Equals("US", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("USA", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("United States", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("United States of America", StringComparison.OrdinalIgnoreCase))
        {
            return new TaxCalculation(0, 0, "US");
        }

        if (!country.Equals("Canada", StringComparison.OrdinalIgnoreCase) &&
            !country.Equals("CA", StringComparison.OrdinalIgnoreCase))
        {
            return new TaxCalculation(0, 0, country.Length == 0 ? "No tax" : country);
        }

        var province = NormalizeProvince(agent.Province);
        var taxRate = await _uow.ProvinceTaxRates.FirstOrDefaultAsync(t => t.ProvinceCode == province && t.IsActive);
        if (taxRate == null)
        {
            return new TaxCalculation(0, 0, string.IsNullOrWhiteSpace(province) ? "Canada" : province);
        }

        var amount = Math.Round(taxableAmount * taxRate.Rate, 2, MidpointRounding.AwayFromZero);
        return new TaxCalculation(taxRate.Rate, amount, $"{taxRate.ProvinceCode} {taxRate.TaxLabel}".Trim());
    }

    private static string FormatPeriod(BillingPeriod period) => period switch
    {
        BillingPeriod.Annually => "annual",
        BillingPeriod.Quarterly => "quarterly",
        _ => "monthly"
    };

    private static string NormalizeProvince(string? province)
    {
        var value = (province ?? string.Empty).Trim().ToUpperInvariant();
        return ProvinceAliases.TryGetValue(value, out var alias) ? alias : value;
    }

    private static readonly Dictionary<string, string> ProvinceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ALBERTA"] = "AB",
        ["BRITISH COLUMBIA"] = "BC",
        ["MANITOBA"] = "MB",
        ["NEW BRUNSWICK"] = "NB",
        ["NEWFOUNDLAND"] = "NL",
        ["NEWFOUNDLAND AND LABRADOR"] = "NL",
        ["NORTHWEST TERRITORIES"] = "NT",
        ["NOVA SCOTIA"] = "NS",
        ["NUNAVUT"] = "NU",
        ["ONTARIO"] = "ON",
        ["PRINCE EDWARD ISLAND"] = "PE",
        ["QUEBEC"] = "QC",
        ["QUÉBEC"] = "QC",
        ["SASKATCHEWAN"] = "SK",
        ["YUKON"] = "YT"
    };

    private async Task<BillingIssue> BuildBillingIssueAsync(IPRO.Entities.Invoice invoice, string status, string message)
    {
        var package = await _uow.BillingRules.GetByIdAsync(invoice.Billing.BillingRuleId);
        return new BillingIssue
        {
            BillingId = invoice.BillingId,
            InvoiceId = invoice.Id,
            PackageName = package?.PackageName ?? "IPRO package",
            Status = status,
            AmountDue = invoice.Total,
            Currency = invoice.Currency,
            Message = message
        };
    }

    private static bool IsActionableBillingIssue(IPRO.Entities.Billing issueBilling, IPRO.Entities.Billing? activeSubscription)
    {
        if (activeSubscription == null)
        {
            return true;
        }

        return activeSubscription.Id == issueBilling.Id ||
            activeSubscription.BillingRuleId != issueBilling.BillingRuleId;
    }

    private static string BuildBillingIssueEmailHtml(string fullName, string packageName, string amount, BillingStatus status)
    {
        var heading = status == BillingStatus.Failed ? "Payment Needs Attention" : "Payment Still Pending";
        return $"""
        <div style="font-family:Arial,sans-serif;background:#f4f7fb;padding:24px;">
          <div style="max-width:620px;margin:0 auto;background:#ffffff;border-radius:14px;overflow:hidden;border:1px solid #dbe5f2;">
            <div style="background:#0f3f8f;color:#ffffff;padding:24px 28px;">
              <h1 style="margin:0;font-size:24px;">{heading}</h1>
              <p style="margin:8px 0 0;color:#dbeafe;">IPRO Advisers billing notice</p>
            </div>
            <div style="padding:28px;color:#1f2937;">
              <p>Hello {fullName},</p>
              <p>We need your attention on the payment for <strong>{packageName}</strong>.</p>
              <div style="background:#fff7ed;border:1px solid #fed7aa;border-radius:10px;padding:16px;margin:20px 0;">
                <strong>Amount:</strong> {amount}<br/>
                <strong>Status:</strong> {heading}
              </div>
              <p>Please sign in to your IPRO Agent Portal and open <strong>Billing</strong> to update or retry your payment.</p>
              <p style="margin-top:28px;">IPRO Management</p>
            </div>
          </div>
        </div>
        """;
    }

    private async Task<PaidInvoiceEmailResult> SendPaidInvoiceEmailAsync(int invoiceId, bool force = false)
    {
        var alreadySent = await _uow.OperateLogs.ExistsAsync(l =>
            l.Module == "Billing" &&
            l.Action == "InvoiceEmail" &&
            l.Description == $"Invoice:{invoiceId}");
        if (alreadySent && !force)
        {
            return PaidInvoiceEmailResult.Sent();
        }

        var invoice = await _uow.Invoices.GetByIdAsync(invoiceId);
        if (invoice == null || !invoice.IsPaid)
        {
            return PaidInvoiceEmailResult.Failed("Invoice email could not be sent because the invoice is missing or unpaid.");
        }

        var agent = await _uow.AgentUsers.GetByIdAsync(invoice.AgentUserId);
        if (agent == null || string.IsNullOrWhiteSpace(agent.Email))
        {
            return PaidInvoiceEmailResult.Failed("Invoice email could not be sent because the agent has no email address.");
        }

        var billing = await _uow.Billings.GetByIdAsync(invoice.BillingId);
        var package = billing == null ? null : await _uow.BillingRules.GetByIdAsync(billing.BillingRuleId);
        var lineItems = (await _uow.InvoiceLineItems.FindAsync(i => i.InvoiceId == invoice.Id))
            .OrderBy(i => i.SortOrder)
            .ToList();

        var fullName = $"{agent.FirstName} {agent.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(fullName))
        {
            fullName = agent.UserName;
        }

        var packageName = package?.PackageName ?? "IPRO package";
        var html = BuildPaidInvoiceEmailHtml(invoice, lineItems, agent, fullName, packageName);
        var text = BuildPaidInvoiceEmailText(invoice, lineItems, fullName, packageName);
        var sendResult = await _email.SendDetailedAsync(agent.Email, fullName, $"IPRO invoice {invoice.InvoiceNumber}", html, text);
        if (!sendResult.Success)
        {
            await _uow.OperateLogs.AddAsync(new OperateLog
            {
                AgentUserId = agent.Id,
                Module = "Billing",
                Action = "InvoiceEmailFailed",
                Description = $"Invoice:{invoiceId}:Email:{agent.Email}:Reason:{sendResult.Message}",
                CreatedAt = DateTime.UtcNow
            });
            await _uow.SaveChangesAsync();
            return PaidInvoiceEmailResult.Failed(sendResult.Message);
        }

        await _uow.OperateLogs.AddAsync(new OperateLog
        {
            AgentUserId = agent.Id,
            Module = "Billing",
            Action = "InvoiceEmail",
            Description = $"Invoice:{invoiceId}",
            CreatedAt = DateTime.UtcNow
        });
        await _uow.SaveChangesAsync();
        return PaidInvoiceEmailResult.Sent();
    }

    private string BuildPaidInvoiceEmailHtml(IPRO.Entities.Invoice invoice, IEnumerable<InvoiceLineItem> lineItems, AgentUser agent, string fullName, string packageName)
    {
        var billingUrl = GetPortalBillingUrl();
        var invoiceUrl = GetPortalInvoiceUrl(invoice.Id);
        var companyName = _configuration["BillingCompany:Name"] ?? "IPRO Advisers";
        var companyEmail = _configuration["BillingCompany:Email"] ?? "billing@iproadvisers.com";
        var companyWebsite = _configuration["BillingCompany:Website"] ?? "www.iProAdvisers.com";
        var taxNumber = _configuration["BillingCompany:TaxRegistrationNumber"] ?? string.Empty;
        var itemList = lineItems.ToList();
        var rows = itemList.Any()
            ? string.Join("", itemList.Select(item => $"""
                <tr>
                  <td style="padding:12px 0;border-bottom:1px solid #e5edf7;">{WebUtility.HtmlEncode(item.Description)}</td>
                  <td style="padding:12px 0;border-bottom:1px solid #e5edf7;text-align:right;">${item.Amount:N2} {invoice.Currency}</td>
                </tr>
                """))
            : $"""
                <tr>
                  <td style="padding:12px 0;border-bottom:1px solid #e5edf7;">{WebUtility.HtmlEncode(packageName)} billing charge</td>
                  <td style="padding:12px 0;border-bottom:1px solid #e5edf7;text-align:right;">${invoice.SubTotal:N2} {invoice.Currency}</td>
                </tr>
                """;

        var address = BuildEmailBillToBlock(agent);
        var billingButton = string.IsNullOrWhiteSpace(billingUrl)
            ? ""
            : $"""<p style="margin:26px 0;"><a href="{billingUrl}" style="display:inline-block;background:#1457d9;color:#ffffff;text-decoration:none;padding:12px 18px;border-radius:9px;font-weight:bold;">View Billing</a></p>""";
        var invoiceButton = string.IsNullOrWhiteSpace(invoiceUrl)
            ? ""
            : $"""<a href="{invoiceUrl}" style="display:inline-block;background:#1457d9;color:#ffffff;text-decoration:none;padding:12px 18px;border-radius:9px;font-weight:bold;margin-right:10px;">View / Print Invoice</a>""";

        return $"""
        <div style="font-family:Arial,sans-serif;background:#f4f7fb;padding:24px;">
          <div style="max-width:680px;margin:0 auto;background:#ffffff;border-radius:14px;overflow:hidden;border:1px solid #dbe5f2;">
            <div style="background:#102a5c;color:#ffffff;padding:26px 30px;">
              <h1 style="margin:0;font-size:24px;">{WebUtility.HtmlEncode(companyName)}</h1>
              <p style="margin:8px 0 0;color:#dbeafe;">Invoice paid</p>
              <p style="margin:8px 0 0;color:#dbeafe;font-size:13px;">{WebUtility.HtmlEncode(companyWebsite)} &nbsp; | &nbsp; {WebUtility.HtmlEncode(companyEmail)}</p>
              {(string.IsNullOrWhiteSpace(taxNumber) ? "" : $"<p style=\"margin:6px 0 0;color:#dbeafe;font-size:12px;\">Tax registration: {WebUtility.HtmlEncode(taxNumber)}</p>")}
            </div>
            <div style="padding:30px;color:#1f2937;">
              <p>Hello {WebUtility.HtmlEncode(fullName)},</p>
              <p>Your payment for <strong>{WebUtility.HtmlEncode(packageName)}</strong> has been received.</p>
              <div style="display:table;width:100%;border-spacing:0 0;margin:20px 0;">
                <div style="display:table-cell;width:50%;background:#f8fafc;border:1px solid #dbe5f2;border-radius:12px;padding:16px;vertical-align:top;">
                  <div style="color:#64748b;font-size:12px;font-weight:bold;text-transform:uppercase;letter-spacing:.08em;margin-bottom:8px;">Bill To</div>
                  <div style="font-weight:bold;">{WebUtility.HtmlEncode(fullName)}</div>
                  {address}
                </div>
                <div style="display:table-cell;width:16px;"></div>
                <div style="display:table-cell;width:50%;background:#f8fafc;border:1px solid #dbe5f2;border-radius:12px;padding:16px;vertical-align:top;">
                  <div style="color:#64748b;font-size:12px;font-weight:bold;text-transform:uppercase;letter-spacing:.08em;margin-bottom:8px;">Invoice Details</div>
                  <div><strong>Invoice #:</strong> {WebUtility.HtmlEncode(invoice.InvoiceNumber)}</div>
                  <div><strong>Date:</strong> {IPRO.DataAccess.AgentLocalTime.FromUtc(invoice.IssuedAt, agent.TimeZone):MMMM d, yyyy}</div>
                  <div><strong>Status:</strong> Paid</div>
                  {(string.IsNullOrWhiteSpace(invoice.PayPalTransactionId) ? "" : $"<div><strong>PayPal transaction:</strong> {WebUtility.HtmlEncode(invoice.PayPalTransactionId)}</div>")}
                </div>
              </div>
              <table style="width:100%;border-collapse:collapse;margin-top:10px;">
                <thead>
                  <tr>
                    <th style="text-align:left;color:#64748b;font-size:12px;text-transform:uppercase;letter-spacing:.08em;border-bottom:2px solid #dbe5f2;padding-bottom:10px;">Description</th>
                    <th style="text-align:right;color:#64748b;font-size:12px;text-transform:uppercase;letter-spacing:.08em;border-bottom:2px solid #dbe5f2;padding-bottom:10px;">Amount</th>
                  </tr>
                </thead>
                <tbody>{rows}</tbody>
              </table>
              <!-- A table, not flex divs: Gmail strips display:flex, which crammed the label and
                   amount together ("Subtotal$40.00" -- owner-reported 2026-08-11). -->
              <table style="width:100%;max-width:320px;margin-left:auto;margin-top:20px;border-collapse:collapse;">
                <tr>
                  <td style="border-bottom:1px solid #e5edf7;padding:8px 0;">Subtotal</td>
                  <td style="border-bottom:1px solid #e5edf7;padding:8px 0;text-align:right;"><strong>${invoice.SubTotal:N2} {invoice.Currency}</strong></td>
                </tr>
                <tr>
                  <td style="border-bottom:1px solid #e5edf7;padding:8px 0;">Tax {WebUtility.HtmlEncode(invoice.TaxRegion)}</td>
                  <td style="border-bottom:1px solid #e5edf7;padding:8px 0;text-align:right;"><strong>${invoice.TaxAmount:N2} {invoice.Currency}</strong></td>
                </tr>
                <tr>
                  <td style="padding:12px 0;color:#1457d9;font-size:20px;"><strong>Total</strong></td>
                  <td style="padding:12px 0;color:#1457d9;font-size:20px;text-align:right;"><strong>${invoice.Total:N2} {invoice.Currency}</strong></td>
                </tr>
              </table>
              <p style="margin:26px 0;">{invoiceButton}</p>
              {billingButton}
              <p style="margin-top:26px;">Thank you for your business. Please keep this invoice for your records.</p>
              <p style="margin-top:16px;">IPRO Management</p>
            </div>
          </div>
        </div>
        """;
    }

    private string BuildPaidInvoiceEmailText(IPRO.Entities.Invoice invoice, IEnumerable<InvoiceLineItem> lineItems, string fullName, string packageName)
    {
        var itemLines = lineItems.Any()
            ? string.Join("\n", lineItems.Select(i => $"- {i.Description}: ${i.Amount:N2} {invoice.Currency}"))
            : $"- {packageName} billing charge: ${invoice.SubTotal:N2} {invoice.Currency}";

        return $"""
        Hello {fullName},

        Thank you for your payment. Invoice {invoice.InvoiceNumber} has been paid.

        Items:
        {itemLines}

        Subtotal: ${invoice.SubTotal:N2} {invoice.Currency}
        Tax {invoice.TaxRegion}: ${invoice.TaxAmount:N2} {invoice.Currency}
        Total: ${invoice.Total:N2} {invoice.Currency}

        You can view your invoice from the Billing page in your IPRO Agent Portal.

        IPRO Management
        """;
    }

    private static string BuildEmailBillToBlock(AgentUser agent)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.CompanyName)) lines.Add(agent.CompanyName);
        if (!string.IsNullOrWhiteSpace(agent.Email)) lines.Add(agent.Email);
        if (!string.IsNullOrWhiteSpace(agent.CompanyAddress)) lines.Add(agent.CompanyAddress);
        if (!string.IsNullOrWhiteSpace(agent.City)) lines.Add(agent.City);

        var provincePostal = $"{agent.Province} {agent.PostalCode}".Trim();
        if (!string.IsNullOrWhiteSpace(provincePostal)) lines.Add(provincePostal);
        if (!string.IsNullOrWhiteSpace(agent.Country)) lines.Add(agent.Country);

        return lines.Count == 0
            ? string.Empty
            : string.Join("", lines.Select(line => $"<div style=\"color:#64748b;font-size:13px;\">{WebUtility.HtmlEncode(line)}</div>"));
    }

    private string GetPortalBillingUrl()
    {
        var source = string.IsNullOrWhiteSpace(_settings.ReturnUrl) ? _settings.CancelUrl : _settings.ReturnUrl;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}/Billing";
    }

    private string GetPortalInvoiceUrl(int invoiceId)
    {
        var source = string.IsNullOrWhiteSpace(_settings.ReturnUrl) ? _settings.CancelUrl : _settings.ReturnUrl;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}/Billing/Invoice/{invoiceId}";
    }

    private async Task<PayPalSubscriptionResult> CreatePayPalSubscriptionAsync(IPRO.Entities.Invoice invoice, BillingRule package, BillingPeriod period, decimal setupFee, string returnUrl, string cancelUrl, string? planIdOverride = null, DateTime? startTimeUtc = null)
    {
        var planId = planIdOverride ?? GetPayPalPlanId(package, period);
        if (string.IsNullOrWhiteSpace(planId))
        {
            throw new InvalidOperationException("This package does not have a PayPal plan ID for the selected billing period.");
        }

        var accessToken = await GetPayPalAccessTokenAsync();
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // WE DO THE TAX MATH; PAYPAL ONLY GETS FINISHED, TAX-INCLUSIVE PRICES (2026-08-10)
        //
        // Two hard-won facts shape this block:
        //
        // 1. Taxes are a SIBLING of payment_preferences, not a child (2026-08-06). Nested inside,
        //    PayPal returns 201 with plan_overridden:true and silently discards the override, so the
        //    tax was displayed on invoices but never collected.
        //
        // 2. PayPal cannot bill a 3-decimal tax percentage (2026-08-10). It ACCEPTS "14.975" and even
        //    echoes it back while the subscription is APPROVAL_PENDING, but on approval it bills at
        //    14.98% and persists that. Quebec's GST+QST is 14.975%, so an exclusive percentage always
        //    overcharges: the $150 setup fee arrived as $172.47 while the invoice said $150 + $22.46
        //    = $172.46 -- and backing the net out of $172.47 printed $150.01 against an advertised
        //    price of $150. The owner flagged both.
        //
        // So instead of asking PayPal to add tax, every amount is grossed up here with the exact
        // provincial rate -- the same net + Math.Round(net * rate) arithmetic CalculateTaxAsync uses,
        // so a PayPal charge always equals the matching invoice line to the penny -- and taxes are
        // declared inclusive, which PayPal treats as informational and never recomputes. The plan's
        // stored prices stay NET (plans are shared across provinces); the gross is applied per
        // subscription via the billing_cycles override, verified accepted by the live sandbox.
        var paymentPreferences = new Dictionary<string, object>();
        object? taxes = null;
        object[]? cycleOverrides = null;
        if (invoice.TaxRate > 0)
        {
            taxes = new
            {
                percentage = (invoice.TaxRate * 100).ToString("0.###", CultureInfo.InvariantCulture),
                inclusive = true
            };
            cycleOverrides = await BuildTaxInclusiveCycleOverridesAsync(client, planId, invoice.TaxRate);
        }

        if (setupFee > 0)
        {
            var setupFeeCharged = invoice.TaxRate > 0 ? AddTax(setupFee, invoice.TaxRate) : setupFee;
            paymentPreferences["setup_fee"] = new
            {
                currency_code = invoice.Currency,
                value = setupFeeCharged.ToString("0.00", CultureInfo.InvariantCulture)
            };
        }

        var payload = new Dictionary<string, object?>
        {
            ["plan_id"] = planId,
            ["custom_id"] = invoice.Id.ToString(),
            ["application_context"] = new
            {
                brand_name = "IPRO Advisers",
                user_action = "SUBSCRIBE_NOW",
                return_url = returnUrl,
                cancel_url = cancelUrl
            }
        };

        // WHEN THE RECURRING CHARGE SHOULD BEGIN (2026-08-06, found by a live sandbox upgrade)
        //
        // Omitting start_time makes PayPal default to "now", which bills the FIRST FULL CYCLE
        // immediately -- on top of the setup fee. For a brand-new subscription that is correct and is
        // what we want. For an UPGRADE it is a straight overcharge: the agent has already paid for the
        // current period on the package they are leaving.
        //
        // Observed on a real Silver -> Gold upgrade: $40 (Silver, already paid) + $19.99 (correct
        // prorated difference) + $60 (a whole extra Gold cycle that should not have been taken until
        // the next billing date). Passing the existing NextBillingDate here means PayPal charges only
        // the prorated setup fee up front and starts the regular cycle when the paid-for period ends,
        // which is exactly what the Billing page already tells the agent will happen.
        //
        // PayPal rejects a start_time that is not in the future, so anything already due falls back to
        // the default: better an immediate start than a failed upgrade.
        if (startTimeUtc.HasValue && startTimeUtc.Value > DateTime.UtcNow.AddMinutes(5))
        {
            payload["start_time"] = startTimeUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        if (paymentPreferences.Count > 0 || taxes != null || cycleOverrides is { Length: > 0 })
        {
            var planOverride = new Dictionary<string, object>();
            if (paymentPreferences.Count > 0) planOverride["payment_preferences"] = paymentPreferences;
            if (taxes != null) planOverride["taxes"] = taxes;
            if (cycleOverrides is { Length: > 0 }) planOverride["billing_cycles"] = cycleOverrides;
            payload["plan"] = planOverride;
        }

        using var response = await client.PostAsync(
            $"{_settings.BaseUrl}/v1/billing/subscriptions",
            new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"));

        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PayPal subscription creation failed: {json}");
        }

        using var document = JsonDocument.Parse(json);
        var subscriptionId = document.RootElement.GetProperty("id").GetString() ?? string.Empty;
        var approvalUrl = document.RootElement.GetProperty("links").EnumerateArray()
            .FirstOrDefault(link => link.TryGetProperty("rel", out var rel) && rel.GetString() == "approve")
            .GetProperty("href").GetString() ?? string.Empty;

        return new PayPalSubscriptionResult(subscriptionId, approvalUrl);
    }

    // Gross a net price up the same way CalculateTaxAsync builds an invoice (net + rounded tax),
    // NOT Math.Round(net * (1 + rate)) -- for a 2-decimal net the two are equivalent, but writing it
    // this way makes "the PayPal charge equals the invoice total" true by construction, not by proof.
    private static decimal AddTax(decimal net, decimal rate) =>
        net + Math.Round(net * rate, 2, MidpointRounding.AwayFromZero);

    // Plans store NET prices and are shared by every province, so the tax-inclusive gross has to be
    // applied per subscription. PayPal's subscription create accepts a billing_cycles override keyed
    // by sequence (verified against the live sandbox 2026-08-10); reading the plan back first keeps
    // this correct for multi-cycle promo plans and the QA daily plans without special-casing them.
    private async Task<object[]> BuildTaxInclusiveCycleOverridesAsync(HttpClient client, string planId, decimal taxRate)
    {
        using var response = await client.GetAsync($"{_settings.BaseUrl}/v1/billing/plans/{planId}");
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PayPal plan lookup failed: {json}");
        }

        using var document = JsonDocument.Parse(json);
        var overrides = new List<object>();
        foreach (var cycle in document.RootElement.GetProperty("billing_cycles").EnumerateArray())
        {
            if (!cycle.TryGetProperty("pricing_scheme", out var scheme) ||
                !scheme.TryGetProperty("fixed_price", out var price))
            {
                continue;
            }

            var net = decimal.Parse(price.GetProperty("value").GetString() ?? "0", CultureInfo.InvariantCulture);
            overrides.Add(new
            {
                sequence = cycle.GetProperty("sequence").GetInt32(),
                pricing_scheme = new
                {
                    fixed_price = new
                    {
                        currency_code = price.TryGetProperty("currency_code", out var currency)
                            ? currency.GetString() ?? "CAD"
                            : "CAD",
                        value = AddTax(net, taxRate).ToString("0.00", CultureInfo.InvariantCulture)
                    }
                }
            });
        }

        return overrides.ToArray();
    }

    private async Task<string> CreatePayPalProductAsync(BillingRule package)
    {
        var accessToken = await GetPayPalAccessTokenAsync();
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add("Prefer", "return=representation");

        var description = string.IsNullOrWhiteSpace(package.Description)
            ? $"{package.PackageName} subscription package"
            : package.Description;
        var payload = new
        {
            name = $"IPRO Advisers - {package.PackageName}",
            description = description.Length > 256 ? description[..256] : description,
            type = "SERVICE"
        };

        using var response = await client.PostAsync(
            $"{_settings.BaseUrl}/v1/catalogs/products",
            new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"));

        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PayPal product creation failed: {json}");
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("id").GetString() ?? string.Empty;
    }

    private async Task<string> CreatePayPalPlanAsync(string productId, BillingRule package, BillingPeriod period, string? intervalUnitOverride = null)
    {
        var amount = GetAmount(package, period);
        if (amount <= 0)
        {
            return string.Empty;
        }

        var accessToken = await GetPayPalAccessTokenAsync();
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add("Prefer", "return=representation");

        // intervalUnitOverride exists only for SyncDailyTestPlanAsync's QA plans -- our own
        // bookkeeping (Billing.Period, GetNextBillingDate) still treats these as Monthly, since
        // PayPal's engine is what actually drives the real cadence here, not this field.
        var intervalUnit = intervalUnitOverride ?? (period == BillingPeriod.Annually ? "YEAR" : "MONTH");
        var periodName = intervalUnitOverride == "DAY" ? "Daily" : (period == BillingPeriod.Annually ? "Annual" : "Monthly");
        var payload = new
        {
            product_id = productId,
            name = $"{package.PackageName} {periodName}",
            description = $"{package.PackageName} {periodName.ToLowerInvariant()} recurring subscription",
            status = "ACTIVE",
            billing_cycles = new[]
            {
                new
                {
                    frequency = new
                    {
                        interval_unit = intervalUnit,
                        interval_count = 1
                    },
                    tenure_type = "REGULAR",
                    sequence = 1,
                    total_cycles = 0,
                    pricing_scheme = new
                    {
                        fixed_price = new
                        {
                            value = amount.ToString("0.00"),
                            currency_code = "CAD"
                        }
                    }
                }
            },
            payment_preferences = new
            {
                auto_bill_outstanding = true,
                setup_fee_failure_action = "CONTINUE",
                payment_failure_threshold = 3
            }
        };

        using var response = await client.PostAsync(
            $"{_settings.BaseUrl}/v1/billing/plans",
            new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"));

        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PayPal {periodName.ToLowerInvariant()} plan creation failed: {json}");
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("id").GetString() ?? string.Empty;
    }

    public async Task<PromotionCode?> ValidatePromotionCodeAsync(string? code, int billingRuleId, int? agentId = null)
    {
        code = code?.Trim();
        if (string.IsNullOrWhiteSpace(code)) return null;

        var promo = await _uow.PromotionCodes.FirstOrDefaultAsync(p => p.Code.ToLower() == code.ToLower());
        if (promo == null || !promo.IsActive) return null;
        if (promo.ExpiresAt.HasValue && promo.ExpiresAt.Value < DateTime.UtcNow) return null;
        if (promo.MaxRedemptions.HasValue && promo.RedemptionCount >= promo.MaxRedemptions.Value) return null;
        // A package restriction binds whenever it is set, whatever the code discounts. Enforcing it
        // only for recurring discounts meant a "100% off setup fee -- Silver only" code also waived
        // Platinum's $400 setup fee for anyone who typed it, and recorded the redemption as
        // legitimate (2026-08-14 ultra-audit).
        if (promo.RestrictedBillingRuleId.HasValue && promo.RestrictedBillingRuleId != billingRuleId) return null;

        // Per-agent redemption uniqueness: only checkable when there's an existing agent to check
        // against (an existing agent choosing to (re)subscribe) -- registration-time validation
        // calls this with no agentId, since the account doesn't exist yet. Stops an agent from
        // cancelling and resubscribing with the same promo code repeatedly.
        if (agentId.HasValue)
        {
            var alreadyRedeemed = await _uow.PromotionCodeRedemptions.FirstOrDefaultAsync(r =>
                r.PromotionCodeId == promo.Id && r.AgentUserId == agentId.Value);
            if (alreadyRedeemed != null) return null;
        }

        return promo;
    }

    private static decimal ComputeDiscountedAmount(decimal original, PromoDiscountType type, decimal value) => type switch
    {
        PromoDiscountType.PercentOff => Math.Max(0, Math.Round(original * (1 - value / 100m), 2)),
        PromoDiscountType.FlatAmountOff => Math.Max(0, original - value),
        _ => original
    };

    private async Task<string> GetOrCreatePromoPlanIdAsync(PromotionCode promo, BillingRule package, BillingPeriod period)
    {
        var cached = period == BillingPeriod.Annually ? promo.PayPalPromoPlanIdAnnual : promo.PayPalPromoPlanIdMonthly;
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var fullAmount = GetAmount(package, period);
        var discountedAmount = ComputeDiscountedAmount(fullAmount, promo.RecurringDiscountType, promo.RecurringDiscountValue);

        var productId = await CreatePayPalProductAsync(package);
        var planId = await CreatePromoPayPalPlanAsync(productId, package, period, discountedAmount, fullAmount, promo.RecurringDurationCycles);

        if (period == BillingPeriod.Annually)
        {
            promo.PayPalPromoPlanIdAnnual = planId;
        }
        else
        {
            promo.PayPalPromoPlanIdMonthly = planId;
        }
        _uow.PromotionCodes.Update(promo);
        await _uow.SaveChangesAsync();

        return planId;
    }

    private async Task<string> CreatePromoPayPalPlanAsync(string productId, BillingRule package, BillingPeriod period, decimal discountedAmount, decimal fullAmount, int? durationCycles)
    {
        var accessToken = await GetPayPalAccessTokenAsync();
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add("Prefer", "return=representation");

        var intervalUnit = period == BillingPeriod.Annually ? "YEAR" : "MONTH";
        var periodName = period == BillingPeriod.Annually ? "Annual" : "Monthly";

        var billingCycles = new List<object>();
        if (durationCycles.HasValue)
        {
            billingCycles.Add(new
            {
                frequency = new { interval_unit = intervalUnit, interval_count = 1 },
                tenure_type = "TRIAL",
                sequence = 1,
                total_cycles = durationCycles.Value,
                pricing_scheme = new { fixed_price = new { value = discountedAmount.ToString("0.00"), currency_code = "CAD" } }
            });
            billingCycles.Add(new
            {
                frequency = new { interval_unit = intervalUnit, interval_count = 1 },
                tenure_type = "REGULAR",
                sequence = 2,
                total_cycles = 0,
                pricing_scheme = new { fixed_price = new { value = fullAmount.ToString("0.00"), currency_code = "CAD" } }
            });
        }
        else
        {
            billingCycles.Add(new
            {
                frequency = new { interval_unit = intervalUnit, interval_count = 1 },
                tenure_type = "REGULAR",
                sequence = 1,
                total_cycles = 0,
                pricing_scheme = new { fixed_price = new { value = discountedAmount.ToString("0.00"), currency_code = "CAD" } }
            });
        }

        var payload = new
        {
            product_id = productId,
            name = $"{package.PackageName} {periodName} - Promo",
            description = $"{package.PackageName} {periodName.ToLowerInvariant()} recurring subscription with promotion pricing",
            status = "ACTIVE",
            billing_cycles = billingCycles,
            payment_preferences = new
            {
                auto_bill_outstanding = true,
                setup_fee_failure_action = "CONTINUE",
                payment_failure_threshold = 3
            }
        };

        using var response = await client.PostAsync(
            $"{_settings.BaseUrl}/v1/billing/plans",
            new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"));

        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PayPal promo plan creation failed: {json}");
        }

        using var promoDocument = JsonDocument.Parse(json);
        return promoDocument.RootElement.GetProperty("id").GetString() ?? string.Empty;
    }

    private async Task RecordPromoRedemptionAsync(int promotionCodeId, int userId, IPRO.Entities.Billing billing, DateTime redeemedAt)
    {
        var promo = await _uow.PromotionCodes.GetByIdAsync(promotionCodeId);
        if (promo == null) return;

        var package = await _uow.BillingRules.GetByIdAsync(billing.BillingRuleId);
        if (package == null) return;

        var fullAmount = GetAmount(package, billing.Period);
        var discountedAmount = promo.RecurringDiscountType != PromoDiscountType.None
            ? ComputeDiscountedAmount(fullAmount, promo.RecurringDiscountType, promo.RecurringDiscountValue)
            : fullAmount;
        var discountedSetupFee = promo.SetupFeeDiscountType != PromoDiscountType.None
            ? ComputeDiscountedAmount(package.SetupFee, promo.SetupFeeDiscountType, promo.SetupFeeDiscountValue)
            : package.SetupFee;

        // Atomic conditional increment (bypasses the change tracker, straight to the database) so
        // two concurrent redemptions near the last available slot can't both pass a stale in-memory
        // RedemptionCount check and over-redeem past MaxRedemptions -- the WHERE clause is
        // re-evaluated by the database at update time, not read-then-written from memory.
        var claimed = await _db.PromotionCodes
            .Where(p => p.Id == promotionCodeId && (p.MaxRedemptions == null || p.RedemptionCount < p.MaxRedemptions))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RedemptionCount, p => p.RedemptionCount + 1));
        if (claimed == 0)
        {
            // The race's loser lands here (review H-10): validation passed earlier, the discount
            // was already granted when the PayPal plan was priced, and only now does the database
            // say the cap was full. The money has moved, so record what actually happened -- count
            // it past the cap and say so loudly -- instead of leaving the counter disagreeing with
            // the redemption rows.
            await _db.PromotionCodes
                .Where(p => p.Id == promotionCodeId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.RedemptionCount, p => p.RedemptionCount + 1));
            _logger.LogError(
                "Promotion code {PromotionCodeId} was redeemed past its cap by agent {AgentUserId}; the discount was already applied when the plan was created. Review the code's limits.",
                promotionCodeId, userId);
        }

        await _uow.PromotionCodeRedemptions.AddAsync(new PromotionCodeRedemption
        {
            PromotionCodeId = promotionCodeId,
            AgentUserId = userId,
            BillingRuleId = billing.BillingRuleId,
            Period = billing.Period,
            OriginalRecurringAmount = fullAmount,
            DiscountedRecurringAmount = discountedAmount,
            OriginalSetupFee = package.SetupFee,
            DiscountedSetupFee = discountedSetupFee,
            RedeemedAt = redeemedAt
        });
    }

    // Audit #2 (A2-H2): the replacement paths deliberately leave the OLD row Active when its
    // PayPal cancellation fails, so the failure stays visible instead of hiding behind a local
    // "Cancelled". This hourly sweep is the retry that makes that state converge to one billable
    // subscription per agent: the newest Active row is the real one, every older Active row is
    // retried against PayPal until it confirms stopped. Rows with no PayPalSubscriptionId
    // (free/promo) converge immediately. Failures log at Error and are retried next run.
    // The system learns that a subscription ended ONLY from a webhook. One lost CANCELLED/EXPIRED
    // delivery -- or a buyer cancelling inside PayPal's own interface, which may never reach us --
    // leaves an Active Billing row, and IsAccessGatedAsync grants full access on the mere existence
    // of one. Nothing expired an Active row: BillingStatus.Expired was written only by the EXPIRED
    // webhook, and ReconcileDuplicateActiveSubscriptionsAsync only looks at agents holding TWO active
    // rows, so a single orphan was invisible forever (2026-08-14 ultra-audit).
    //
    // This asks PayPal directly, which is the only source that can be trusted about PayPal's state.
    // Deliberately conservative: an empty status means we could not reach PayPal or the call failed,
    // and that must never revoke a paying customer's access -- only an explicit CANCELLED/EXPIRED/
    // SUSPENDED answer does. Rows with no PayPalSubscriptionId are skipped: they are the comped and
    // trial agents, who legitimately have no subscription behind them.
    public async Task<int> ReconcileActiveSubscriptionsWithPayPalAsync()
    {
        if (!HasPayPalSettings()) return 0;

        var actives = (await _uow.Billings.FindAsync(b => b.Status == BillingStatus.Active))
            .Where(b => !string.IsNullOrWhiteSpace(b.PayPalSubscriptionId))
            .ToList();

        var corrected = 0;
        foreach (var billing in actives)
        {
            string status;
            try
            {
                status = await GetPayPalSubscriptionStatusAsync(billing.PayPalSubscriptionId);
            }
            catch (Exception ex)
            {
                // One unreachable subscription must not abort the sweep for everyone else.
                _logger.LogError(ex,
                    "Reconciliation: could not read PayPal status for subscription {SubscriptionId} (agent {AgentUserId}); will retry next run.",
                    billing.PayPalSubscriptionId, billing.AgentUserId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(status)) continue;

            var endedAtPayPal =
                status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("EXPIRED", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("SUSPENDED", StringComparison.OrdinalIgnoreCase);
            if (!endedAtPayPal) continue;

            billing.Status = status.Equals("EXPIRED", StringComparison.OrdinalIgnoreCase)
                ? BillingStatus.Expired
                : BillingStatus.Cancelled;
            billing.CancelledAt ??= DateTime.UtcNow;
            _uow.Billings.Update(billing);
            corrected++;

            _logger.LogWarning(
                "Reconciliation: PayPal reports subscription {SubscriptionId} for agent {AgentUserId} is {Status}, " +
                "but IPRO still had it Active -- a cancellation webhook was almost certainly lost. Local row corrected to {NewStatus}.",
                billing.PayPalSubscriptionId, billing.AgentUserId, status, billing.Status);
        }

        if (corrected > 0) await _uow.SaveChangesAsync();
        return corrected;
    }

    public async Task<int> ReconcileDuplicateActiveSubscriptionsAsync()
    {
        var actives = await _uow.Billings.FindAsync(b => b.Status == BillingStatus.Active);
        var converged = 0;
        foreach (var group in actives.GroupBy(b => b.AgentUserId).Where(g => g.Count() > 1))
        {
            var keep = group.OrderByDescending(b => b.Id).First();
            foreach (var stale in group.Where(b => b.Id != keep.Id))
            {
                if (!await CancelPayPalSubscriptionAsync(stale.PayPalSubscriptionId,
                        "Superseded IPRO subscription reconciled after an earlier failed cancellation."))
                {
                    _logger.LogError(
                        "Reconciliation: billing {BillingId} (PayPal {SubscriptionId}) for agent {AgentUserId} may still be billing alongside newer billing {KeepId}; retrying next run.",
                        stale.Id, stale.PayPalSubscriptionId, stale.AgentUserId, keep.Id);
                    continue;
                }

                stale.Status = BillingStatus.Cancelled;
                stale.CancelledAt = DateTime.UtcNow;
                _uow.Billings.Update(stale);
                converged++;
            }
        }

        if (converged > 0)
        {
            await _uow.SaveChangesAsync();
        }

        return converged;
    }

    private async Task<string> GetPayPalSubscriptionStatusAsync(string subscriptionId)
    {
        if (!HasPayPalSettings())
        {
            return string.Empty;
        }

        var accessToken = await GetPayPalAccessTokenAsync();
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.GetAsync($"{_settings.BaseUrl}/v1/billing/subscriptions/{subscriptionId}");
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        return GetWebhookString(document.RootElement, "status");
    }

    // Returns true only when PayPal has actually stopped the subscription.
    //
    // This used to return `Task`, so the outcome was unobservable: it logged a warning and every
    // caller carried on as if cancellation had worked. CancelSubscriptionAsync then marked the local
    // row Cancelled and returned true, which made the agent-facing "Subscription cancelled" a claim
    // we had not verified -- and left agent-delete's abort guard reading a value that was always
    // true. Money kept moving with no account attached to it.
    private async Task<bool> CancelPayPalSubscriptionAsync(string subscriptionId, string reason)
    {
        if (!HasPayPalSettings() || string.IsNullOrWhiteSpace(subscriptionId))
        {
            // Nothing to cancel at PayPal, so nothing failed. A free/promo agent with no PayPal
            // subscription must not be treated as a cancellation failure or they become undeletable.
            return true;
        }

        try
        {
            var accessToken = await GetPayPalAccessTokenAsync();
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var payload = new { reason };
            using var response = await client.PostAsync(
                $"{_settings.BaseUrl}/v1/billing/subscriptions/{subscriptionId}/cancel",
                new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"));

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            var body = await response.Content.ReadAsStringAsync();

            // 422 UNPROCESSABLE_ENTITY usually means "not in a cancellable state" -- but PayPal
            // uses 422 for other semantic failures too, so it must not be taken as success on its
            // own (review H-2). Ask for the subscription's actual status and accept ONLY the
            // terminal states, CANCELLED and EXPIRED (audit #2, A2-H3). APPROVAL_PENDING and
            // APPROVED are NOT safe: a stale approval link can still be completed later and the
            // subscription starts billing -- and CapturePaymentAsync itself treats APPROVED as
            // activation-ready, so calling it "stopped" here would contradict our own activation
            // logic. Those states, ACTIVE, SUSPENDED, and a failed lookup all stay failures; the
            // row stays visible and the hourly reconciliation sweep keeps retrying it.
            if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                var status = await GetPayPalSubscriptionStatusAsync(subscriptionId);
                if (status is "CANCELLED" or "EXPIRED")
                {
                    _logger.LogInformation(
                        "PayPal subscription {SubscriptionId} is {Status}; the 422 on cancel means there was nothing left to stop.",
                        subscriptionId, status);
                    return true;
                }

                _logger.LogError(
                    "PayPal returned 422 cancelling {SubscriptionId} but its status is '{Status}' (empty = status lookup failed). " +
                    "It may still be billing; the local row is NOT being marked cancelled. Body: {Body}",
                    subscriptionId, status, body);
                return false;
            }

            _logger.LogError(
                "PayPal subscription cancellation for {SubscriptionId} returned {StatusCode}: {Body}. " +
                "The subscription may still be billing; the local row is NOT being marked cancelled.",
                subscriptionId, (int)response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "PayPal subscription cancellation for {SubscriptionId} threw. The subscription may still " +
                "be billing; the local row is NOT being marked cancelled.",
                subscriptionId);
            return false;
        }
    }

    private async Task<PayPalOrderResult> CreatePayPalOrderAsync(IPRO.Entities.Invoice invoice, string packageName, string returnUrl, string cancelUrl)
    {
        var accessToken = await GetPayPalAccessTokenAsync();
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var payload = new
        {
            intent = "CAPTURE",
            purchase_units = new[]
            {
                new
                {
                    reference_id = invoice.InvoiceNumber,
                    description = $"IPRO Advisers {packageName}",
                    custom_id = invoice.Id.ToString(),
                    amount = new
                    {
                        currency_code = invoice.Currency,
                        value = invoice.Total.ToString("0.00")
                    }
                }
            },
            application_context = new
            {
                brand_name = "IPRO Advisers",
                user_action = "PAY_NOW",
                return_url = returnUrl,
                cancel_url = cancelUrl
            }
        };

        using var response = await client.PostAsync(
            $"{_settings.BaseUrl}/v2/checkout/orders",
            new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"));

        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PayPal order creation failed: {json}");
        }

        using var document = JsonDocument.Parse(json);
        var orderId = document.RootElement.GetProperty("id").GetString() ?? string.Empty;
        var approvalUrl = document.RootElement.GetProperty("links").EnumerateArray()
            .FirstOrDefault(link => link.TryGetProperty("rel", out var rel) && rel.GetString() == "approve")
            .GetProperty("href").GetString() ?? string.Empty;

        return new PayPalOrderResult(orderId, approvalUrl);
    }

    private async Task<bool> CapturePayPalOrderAsync(string orderId)
    {
        if (!HasPayPalSettings())
        {
            return false;
        }

        var accessToken = await GetPayPalAccessTokenAsync();
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.PostAsync(
            $"{_settings.BaseUrl}/v2/checkout/orders/{orderId}/capture",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        return response.IsSuccessStatusCode;
    }

    private async Task<string> GetPayPalAccessTokenAsync()
    {
        var client = _httpClientFactory.CreateClient();
        var clientId = _settings.ClientId.Trim();
        var clientSecret = _settings.ClientSecret.Trim();
        var rawCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", rawCredentials);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        });
        using var response = await client.PostAsync($"{_settings.BaseUrl}/v1/oauth2/token", content);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            if (json.Contains("invalid_client", StringComparison.OrdinalIgnoreCase))
            {
                var mode = _settings.IsSandbox ? "sandbox" : "live";
                throw new InvalidOperationException($"PayPal rejected the configured Client ID or Secret for {mode} mode. Check Azure app settings PayPal__ClientId, PayPal__ClientSecret, and PayPal__IsSandbox. Sandbox credentials only work when PayPal__IsSandbox is true; live credentials only work when it is false.");
            }

            throw new InvalidOperationException("PayPal token request failed. Please check the PayPal app settings in Azure and try again.");
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
    }

    private async Task<bool> VerifyWebhookSignatureAsync(JsonElement webhookEvent, PayPalWebhookHeaders headers)
    {
        if (!HasPayPalSettings() || string.IsNullOrWhiteSpace(_settings.WebhookId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(headers.TransmissionId) ||
            string.IsNullOrWhiteSpace(headers.TransmissionTime) ||
            string.IsNullOrWhiteSpace(headers.TransmissionSignature) ||
            string.IsNullOrWhiteSpace(headers.CertificateUrl) ||
            string.IsNullOrWhiteSpace(headers.AuthenticationAlgorithm))
        {
            return false;
        }

        var accessToken = await GetPayPalAccessTokenAsync();
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var body = new Dictionary<string, object?>
        {
            ["auth_algo"] = headers.AuthenticationAlgorithm,
            ["cert_url"] = headers.CertificateUrl,
            ["transmission_id"] = headers.TransmissionId,
            ["transmission_sig"] = headers.TransmissionSignature,
            ["transmission_time"] = headers.TransmissionTime,
            ["webhook_id"] = _settings.WebhookId,
            ["webhook_event"] = webhookEvent
        };

        using var response = await client.PostAsync(
            $"{_settings.BaseUrl}/v1/notifications/verify-webhook-signature",
            new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"));

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var verification = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return verification.RootElement.TryGetProperty("verification_status", out var status) &&
            status.GetString()?.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool HasPayPalSettings()
    {
        return !string.IsNullOrWhiteSpace(_settings.ClientId?.Trim())
            && !string.IsNullOrWhiteSpace(_settings.ClientSecret?.Trim());
    }

    private static bool IsUpgrade(BillingRule currentPackage, BillingRule requestedPackage)
    {
        return GetComparableMonthlyPrice(requestedPackage) > GetComparableMonthlyPrice(currentPackage);
    }

    private static decimal GetComparableMonthlyPrice(BillingRule package) =>
        package.MonthlyPrice <= 0 ? decimal.MaxValue : package.MonthlyPrice;

    private static decimal CalculateRemainingFraction(DateTime now, DateTime startDate, DateTime endDate)
    {
        if (endDate <= startDate || now >= endDate)
        {
            return 0;
        }

        var totalSeconds = (decimal)(endDate - startDate).TotalSeconds;
        var remainingSeconds = (decimal)(endDate - now).TotalSeconds;
        return Math.Clamp(remainingSeconds / totalSeconds, 0, 1);
    }

    private static decimal GetAmount(BillingRule package, BillingPeriod period) => period switch
    {
        BillingPeriod.Quarterly => package.QuarterlyPrice,
        BillingPeriod.Annually => package.AnnualPrice,
        _ => package.MonthlyPrice
    };

    // A period may be sold only when the package carries BOTH a positive price and a PayPal plan to
    // charge it on. Public so the pricing/registration screens can hide what cannot be bought, and
    // enforced server-side in CreateSubscriptionAsync so hiding a radio is never the only defence.
    public static bool IsPeriodOfferable(BillingRule package, BillingPeriod period) =>
        GetAmount(package, period) > 0 && !string.IsNullOrWhiteSpace(GetPayPalPlanId(package, period));

    private static string GetPayPalPlanId(BillingRule package, BillingPeriod period) => period switch
    {
        BillingPeriod.Annually => package.PayPalAnnualPlanId?.Trim() ?? string.Empty,
        _ => package.PayPalMonthlyPlanId?.Trim() ?? string.Empty
    };

    private static DateTime GetNextBillingDate(DateTime startDate, BillingPeriod period) => period switch
    {
        BillingPeriod.Quarterly => startDate.AddMonths(3),
        BillingPeriod.Annually => startDate.AddYears(1),
        _ => startDate.AddMonths(1)
    };

    // The start of the cycle the agent is currently paid through, derived by winding the NEXT
    // billing date back one period. Proration must never measure from Billing.StartDate: that is
    // written once at activation and never advanced on renewal, so after the first renewal the
    // denominator becomes the whole lifetime of the subscription instead of one cycle. A Silver
    // agent who renewed once and upgraded the next day was charged for ~48% of the difference
    // instead of ~97%; after twelve renewals the same upgrade cost about a thirteenth of its price.
    // The QA runs never caught it because every upgrade resets StartDate -- only a
    // renewed-but-not-yet-upgraded subscription shows it (2026-08-14 ultra-audit).
    private static DateTime GetCurrentCycleStart(DateTime nextBillingDate, BillingPeriod period) => period switch
    {
        BillingPeriod.Quarterly => nextBillingDate.AddMonths(-3),
        BillingPeriod.Annually => nextBillingDate.AddYears(-1),
        _ => nextBillingDate.AddMonths(-1)
    };

    private static bool IsPayPalSubscriptionApproved(string status) =>
        status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("APPROVED", StringComparison.OrdinalIgnoreCase);

    private static bool IsPayPalFailedInvoice(IPRO.Entities.Invoice invoice) =>
        invoice.PayPalTransactionId.StartsWith("PAYPAL_FAILED:", StringComparison.OrdinalIgnoreCase);

    private static string GetWebhookSubscriptionId(JsonElement resource)
    {
        var value = GetWebhookString(resource, "billing_agreement_id");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = GetWebhookString(resource, "subscription_id");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return GetWebhookString(resource, "id");
    }

    private static string GetWebhookString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind != JsonValueKind.Null
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private sealed record InvoiceLineDraft(string Description, decimal Amount);
    private sealed record PaidInvoiceEmailResult(bool Success, string Message)
    {
        public static PaidInvoiceEmailResult Sent() => new(true, "Invoice email sent.");
        public static PaidInvoiceEmailResult Failed(string message) => new(false, message);
    }

    private sealed record TaxCalculation(decimal Rate, decimal Amount, string Region);
    private sealed record PayPalOrderResult(string OrderId, string ApprovalUrl);
    private sealed record PayPalSubscriptionResult(string SubscriptionId, string ApprovalUrl);
}
