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

    public async Task<BillingChangeResult> CreateSubscriptionAsync(int userId, int billingRuleId, BillingPeriod period, string returnUrl, string cancelUrl, string? downgradeMode = null)
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

        // ADMIN-2 / BILLING-9: a PayPal plan charges the price it was CREATED with, frozen — editing
        // the package price in Super Admin does not touch the plan. The Packages screen warns on
        // divergence (422b), but a warning an admin can ignore is not a guard: checkout would charge
        // the frozen plan price while the invoice shows the edited package price. Refuse instead —
        // fail closed until the admin re-syncs the plans. Null recorded price = plan synced before
        // the snapshot columns existed (pre-422b); that is "divergence unknown", allowed, and the
        // banner still nags the admin to re-sync. The customer message carries no numbers on
        // purpose; the log carries both for the operator.
        if (HasDivergentPlanPrice(requestedPackage, period))
        {
            _logger.LogError(
                "Refusing checkout for agent {AgentId} on package {PackageId} ({Period}): package price {PackagePrice} " +
                "diverges from the PayPal plan's frozen price {PlanPrice}. Re-sync the plans in Super Admin -> Packages.",
                userId, requestedPackage.Id, period, GetAmount(requestedPackage, period),
                period == BillingPeriod.Annually ? requestedPackage.PayPalAnnualPlanPrice : requestedPackage.PayPalMonthlyPlanPrice);
            return BillingChangeResult.Failed(
                "This package's pricing is being updated and checkout is paused for a moment. " +
                "Please try again shortly or contact support; no payment has been taken.");
        }

        var activeSubscription = await GetActiveSubscriptionAsync(userId);
        if (activeSubscription == null)
        {
            await CancelPendingChangesAsync(userId);

            // AUDIT H2: "no Active row" does NOT mean "owes us money from today". An agent who
            // cancelled still owns everything up to Billing.PaidThroughAt (DOCS/22), and until
            // 2026-08-24 this branch billed them immediately for a window they had already paid
            // for -- a straight double-charge, and the Billing page offered exactly that button
            // because it showed no current package at all. The new subscription now STARTS when
            // the prepaid time runs out, so cover is continuous and nothing is paid for twice.
            var paidThrough = await _uow.Billings.FirstOrDefaultAsync(b =>
                b.AgentUserId == userId &&
                (b.Status == BillingStatus.Cancelled || b.Status == BillingStatus.Expired) &&
                b.PaidThroughAt != null &&
                b.PaidThroughAt > DateTime.UtcNow);
            var resubscribeStart = paidThrough?.PaidThroughAt;

            var agent = await _uow.AgentUsers.GetByIdAsync(userId);
            var promo = await ValidatePromotionCodeAsync(agent?.PromotionCode, requestedPackage.Id, userId);

            // M-8 / A2-H4 (fixed 2026-08-20): the cap slot is claimed HERE, atomically, before the
            // discount is priced into anything -- not at activation, after the money has moved.
            // The old order let the race's loser redeem one past the cap "because the discount was
            // already applied". Now the loser is simply told the code is used up, while the
            // discount is still just a number on a screen. An abandoned checkout releases its slot
            // when the pending change is cancelled (CancelPendingChangesAsync), so slots do not
            // leak; between abandonment and that cancellation the code errs toward refusing --
            // fail-closed, like the rest of billing.
            if (promo != null && promo.MaxRedemptions.HasValue)
            {
                var slotClaimed = await _db.PromotionCodes
                    .Where(pc => pc.Id == promo.Id && (pc.MaxRedemptions == null || pc.RedemptionCount < pc.MaxRedemptions))
                    .ExecuteUpdateAsync(su => su.SetProperty(pc => pc.RedemptionCount, pc => pc.RedemptionCount + 1));
                if (slotClaimed == 0)
                {
                    return BillingChangeResult.Failed("That promotion code has reached its redemption limit.");
                }
            }

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
                            // M-8: this exit sits between the slot claim and checkout creation --
                            // it must give the slot back like every other failure after the claim.
                            await ReleasePromoSlotAsync(promo);
                            return BillingChangeResult.Failed("This promotion code's pricing can't be set up with PayPal right now (a permanent 100%-or-more discount isn't supported unless the setup fee is also fully discounted). Please contact support.");
                        }
                    }
                }
            }

            // An agent finishing a scheduled downgrade/term switch arrives here with no active
            // subscription -- that is the designed H-7 flow, not a new customer. They paid the
            // setup fee at their original signup; charging it again for completing a plan change
            // we ourselves scheduled is double-billing (2026-08-16 audit). Two bounds keep this a
            // completion waiver, not a free re-entry door: a 90-day window, and CONSUMPTION -- once
            // any later subscription has actually activated, the change was completed and a fresh
            // signup after a voluntary cancel pays the fee like anyone else (review pass).
            var completesScheduledChange = false;
            // H6: only a SCHEDULED downgrade's Applied row opens this door. A convert-downgrade
            // also lands as an Applied Downgrade, but it is ALREADY complete -- its own billing
            // was the new subscription -- so a later voluntary re-signup after one must pay the
            // fee like anyone else. The two are distinguishable by construction: a convert always
            // carries the credit it converted (ProratedCredit > 0, ComputeConvertCredit refuses
            // creditDays <= 0), while ScheduleDowngradeAsync writes ProratedCredit = 0.
            var latestAppliedDowngrade = (await _uow.SubscriptionChanges.FindAsync(c =>
                    c.AgentUserId == userId &&
                    c.ChangeType == SubscriptionChangeType.Downgrade &&
                    c.Status == SubscriptionChangeStatus.Applied &&
                    c.ProratedCredit == 0m &&
                    c.RequestedBillingRuleId == requestedPackage.Id))
                .Where(c => c.AppliedAt.HasValue && c.AppliedAt.Value > DateTime.UtcNow.AddDays(-90))
                .OrderByDescending(c => c.AppliedAt)
                .FirstOrDefault();
            if (latestAppliedDowngrade != null)
            {
                // Consumption: once any later subscription actually activated, OR the agent
                // answered the downgrade by cancelling outright (H6 -- a Cancel row is an answer,
                // and re-entry after it is voluntary), the waiver is spent. F3c (wave 4): a
                // Cancel row referencing the SAME billing as the applied downgrade is the
                // downgrade's OWN cancellation -- the old subscription's death recorded by a
                // racing door -- not the agent answering; it must not spend the waiver.
                var alreadyCompleted = (await _uow.SubscriptionChanges.FindAsync(c =>
                        c.AgentUserId == userId &&
                        (c.ChangeType == SubscriptionChangeType.Subscribe || c.ChangeType == SubscriptionChangeType.Cancel) &&
                        c.Status == SubscriptionChangeStatus.Applied))
                    .Any(c => c.AppliedAt.HasValue && c.AppliedAt.Value > latestAppliedDowngrade.AppliedAt!.Value &&
                              (c.ChangeType == SubscriptionChangeType.Subscribe ||
                               !(c.BillingId.HasValue && latestAppliedDowngrade.BillingId.HasValue && c.BillingId == latestAppliedDowngrade.BillingId)));
                completesScheduledChange = !alreadyCompleted;
            }

            var effectiveAmount = overrideAmount ?? GetAmount(requestedPackage, period);
            var checkoutResult = await BeginPaidChangeAsync(
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
                nextBillingDate: resubscribeStart,
                deferredStart: resubscribeStart,
                includeSetupFee: !completesScheduledChange,
                overrideAmount: overrideAmount,
                overridePlanId: overridePlanId,
                overrideSetupFee: overrideSetupFee,
                promotionCodeId: promo?.Id);

            // M-8: a checkout that failed to start never created the pending change whose later
            // cancellation would release the claimed slot -- give it back here or capped codes
            // leak capacity on every PayPal hiccup.
            if (!checkoutResult.Success)
            {
                await ReleasePromoSlotAsync(promo);
            }

            return checkoutResult;
        }

        if (activeSubscription.BillingRuleId == requestedPackage.Id)
        {
            if (period == activeSubscription.Period)
            {
                await CancelPendingChangesAsync(userId);
                return new BillingChangeResult { Success = true, Message = "You are already on that package." };
            }

            // Same package, different term (e.g. Gold monthly -> Gold annual). This used to compare
            // only the package id and answer "you are already on that package", which made switching
            // billing terms impossible anywhere in the product -- and silently threw away any pending
            // change as a side effect (2026-08-16 audit). A term switch is scheduled exactly like a
            // downgrade: it takes effect when the paid period ends, then the agent re-approves at
            // PayPal on the new term. Nothing is cut short, so there is nothing to prorate.
            var termSwitchDate = await ScheduleDowngradeAsync(userId, activeSubscription, requestedPackage, requestedPackage, period);
            // New A: same mechanism as a scheduled downgrade, same disclosure duty.
            return new BillingChangeResult
            {
                Success = true,
                Message = $"Your switch to {FormatPeriod(period)} billing is scheduled for {termSwitchDate:MMMM d, yyyy}, when your current billing period ends. " +
                          $"On that date your current subscription ends, and we'll email you to finish the switch with a quick PayPal approval. " +
                          $"Until you approve, your account pauses at the Billing page — everything is kept, nothing is deleted."
            };
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
            var (credit, charge) = CalculateUpgradeProration(
                currentPackage, requestedPackage, activeSubscription.Period,
                activeSubscription.Amount, now, effectiveEnd);
            var amountDue = Math.Max(0, charge - credit);

            // The no-refund clamp is deliberate (the ToS says the current period is not refunded;
            // the agent keeps service in kind instead) -- but the forfeited amount must be visible
            // in the ledger conversation, not silently zeroed. SubscriptionChange keeps the real
            // credit/charge, and this log line is the operator's cue that value was surrendered.
            if (credit > charge)
            {
                _logger.LogWarning(
                    "Upgrade proration for agent {AgentId}: credit {Credit:F2} exceeds charge {Charge:F2}; " +
                    "{Forfeited:F2} of prepaid value is compensated in kind (service until {EffectiveEnd:yyyy-MM-dd}), not in cash.",
                    userId, credit, charge, credit - charge, effectiveEnd);
            }

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

        // DOCS/22 Offer-Both: "convert" switches the agent to the cheaper package NOW and turns
        // the unused prepaid value into free time on it, at the rate they actually paid (the
        // annual discount belongs to those who stay). Mechanism: the same supersede machinery as
        // upgrades -- a new PayPal subscription whose start_time sits at the end of the credit,
        // with the old subscription cancelled only after the new one activates. The default
        // (and the only mode for monthly subscribers) remains the scheduled end-of-period switch.
        if (string.Equals(downgradeMode, "convert", StringComparison.OrdinalIgnoreCase))
        {
            if (activeSubscription.Period != BillingPeriod.Annually)
            {
                return BillingChangeResult.Failed(
                    "Switching now with credit applies to annual subscriptions. Monthly downgrades take effect at the end of the paid month, so nothing is lost by scheduling.");
            }

            await CancelPendingChangesAsync(userId);
            var convertNow = DateTime.UtcNow;
            var paidThroughEnd = await ResolvePaidThroughEndAsync(activeSubscription);
            var convertCycleStart = GetCurrentCycleStart(paidThroughEnd, activeSubscription.Period);
            // LOW-1 (wave 5): credit derives from money ACTUALLY PAID (DOCS/22:80), never the
            // list price. The fallback priced a zeroed-Amount row at the full list annual, so a
            // fully-comped agent could "convert" money never paid into free time on another
            // package. Fall back to what actually settled on the row; zero paid converts to zero,
            // refused BEFORE any checkout row exists.
            var paidForCycle = activeSubscription.Amount > 0
                ? activeSubscription.Amount
                : (await _uow.Invoices.FindAsync(i => i.BillingId == activeSubscription.Id && i.IsPaid))
                    .Where(i => i.SubTotal > 0m)
                    .Sum(i => i.SubTotal);
            if (paidForCycle <= 0m)
            {
                return BillingChangeResult.Failed(
                    "There is no paid value on this subscription to convert -- switching with credit applies to money you have actually paid. Use the regular downgrade instead.");
            }
            var (remainingNet, creditDays, creditEnd) = ComputeConvertCredit(
                paidForCycle, requestedPackage.MonthlyPrice, convertNow, convertCycleStart, paidThroughEnd);
            if (creditDays <= 0)
            {
                return BillingChangeResult.Failed(
                    "There is no unused prepaid value left to convert, so there is nothing to gain over the scheduled switch. Use the regular downgrade instead.");
            }

            return await BeginPaidChangeAsync(userId, currentPackage, requestedPackage, period,
                SubscriptionChangeType.Downgrade, convertNow, remainingNet, 0m, 0m,
                returnUrl, cancelUrl, activeSubscription.Id,
                nextBillingDate: creditEnd, deferredStart: creditEnd);
        }

        var downgradeDate = await ScheduleDowngradeAsync(userId, activeSubscription, currentPackage, requestedPackage, period);
        // New A (audit 2026-08-25): the apply is not a seamless switch -- it cancels the current
        // subscription and PayPal requires the agent to re-approve the new one, with access
        // paused at the Billing page in between. The agent must learn that HERE, when deciding,
        // not from an email sent after it has already happened (which H7 says can silently fail).
        return new BillingChangeResult
        {
            Success = true,
            Message = $"Your downgrade to {requestedPackage.PackageName} is scheduled for {downgradeDate:MMMM d, yyyy}. " +
                      $"On that date your current subscription ends, and we'll email you to finish the switch with a quick PayPal approval. " +
                      $"Until you approve, your account pauses at the Billing page — everything is kept, nothing is deleted."
        };
    }

    // The unused value of the running annual cycle, at the rate ACTUALLY PAID for it, converted
    // into whole free days on the new package (rounded up -- the agent's favour, DOCS/22). Pure
    // and internal so the proration matrix tests can pin it like CalculateUpgradeProration.
    internal static (decimal RemainingNet, int CreditDays, DateTime CreditEnd) ComputeConvertCredit(
        decimal amountPaidForCycle, decimal newMonthlyPrice, DateTime now, DateTime cycleStart, DateTime cycleEnd)
    {
        var fraction = CalculateRemainingFraction(now, cycleStart, cycleEnd);
        var remainingNet = Math.Round(amountPaidForCycle * fraction, 2);
        var creditDays = PrepaidValue.CreditDays(remainingNet, newMonthlyPrice);
        return (remainingNet, creditDays, now.AddDays(creditDays));
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
            subscriptionBilling.PayPalSubscriptionId == orderId &&
            subscriptionBilling.Status is not (BillingStatus.Pending or BillingStatus.Active))
        {
            // A Cancelled/Expired/Failed row must NEVER be resurrected: the invoice-first lookup
            // has no status filter, so a superseded upgrade's stale approval link could
            // re-activate a billing the system had already replaced -- and a still-pending
            // downgrade would later destroy the survivor (2026-08-16 audit collision). But the
            // buyer DID just approve a real PayPal subscription; refusing quietly would leave it
            // billing with no account attached (review pass), so it is stopped here and now.
            var stopped = await CancelPayPalSubscriptionAsync(subscriptionBilling.PayPalSubscriptionId,
                "Checkout completed after this plan change was superseded in IPRO.");
            _logger.LogWarning(
                "Agent {AgentUserId} completed a stale approval for superseded billing {BillingId} (status {Status}); PayPal subscription {SubscriptionId} cancel result: {Stopped}.",
                userId, subscriptionBilling.Id, subscriptionBilling.Status, subscriptionBilling.PayPalSubscriptionId, stopped);
            // Wave-2 B: no "you will not be charged" promise -- a subscription checkout can
            // capture the setup fee and first cycle AT approval, before this guard runs. Any such
            // capture lands in the refund queue via the sale handler's captured-after-end net;
            // say that instead of promising what the code cannot guarantee.
            return BillingChangeResult.Failed(stopped
                ? "That checkout was from an earlier plan change that has since been replaced, so it was not activated and the PayPal approval has been cancelled. If a payment was taken when you approved, it is flagged for refund automatically -- contact support if you have any doubt. Please choose your package again."
                : "That checkout was from an earlier plan change that has since been replaced and was not activated. We could not immediately confirm the PayPal cancellation, so please also check your PayPal account for a subscription to cancel; any payment taken is flagged for refund. Contact support if unsure.");
        }

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
        else if (invoice != null && !invoice.IsPaid && invoice.Total <= 0)
        {
            // A $0 invoice whose checkout just completed: nothing was ever owed, no webhook will
            // ever confirm it, and leaving it Unpaid invites the oldest-unpaid fallback to hand a
            // REAL later charge to it (2026-08-16 audit, IPRO-2026-000009). Settled here -- at
            // activation, not at creation, so an ABANDONED zero-due checkout keeps its unpaid
            // invoice and with it the "payment pending" banner and Resume button.
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
        // An upgrade's subscription is created with start_time = the date the agent already paid
        // up to (see BeginPaidChangeAsync), so its FIRST charge can be months away -- recomputing
        // "now + one period" here overwrote that with a date PayPal will never bill on (the
        // Sep-16-vs-July banner, 2026-08-16 audit). See ResolveNextBillingDateOnPayment for the
        // exact keep-vs-recompute rule shared with the sale webhook.
        billing.NextBillingDate = ResolveNextBillingDateOnPayment(billing.NextBillingDate, now, billing.Period);
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
        // No receipt for a $0 adjustment settled as a bookkeeping step -- a "payment received"
        // email for $0.00 confuses more than it informs. The fully-comped promo path
        // (paymentConfirmed: true) keeps its explicit "no cost" receipt.
        if (invoice != null && invoice.IsPaid && (invoice.Total > 0 || paymentConfirmed))
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
        var resumedChangeWasConvert = await _uow.SubscriptionChanges.FirstOrDefaultAsync(c =>
            c.BillingId == billing.Id && c.AgentUserId == userId &&
            c.Status == SubscriptionChangeStatus.Pending &&
            c.ChangeType == SubscriptionChangeType.Downgrade) != null;
        if (!await CancelPendingPaymentAsync(userId, invoice.Id))
        {
            return BillingChangeResult.Failed(
                "We could not clear the previous payment attempt. Please refresh and try again, or contact support.");
        }

        // Delegate rather than reimplement: this is the same path a first-time subscribe takes, so
        // promotion codes, trial-package refusal, setup fees and plan creation all behave identically
        // instead of drifting from a second copy of the logic. LOW-4 (wave 5): a Downgrade-typed
        // pending change on the abandoned checkout means it WAS a convert -- resume must recreate
        // the convert (switch now, credit applied), not silently schedule an end-of-period
        // downgrade the agent never asked for. The shape was read BEFORE the void erased it.
        return await CreateSubscriptionAsync(userId, billingRuleId, period, returnUrl, cancelUrl,
            resumedChangeWasConvert ? "convert" : null);
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

        // H5: this is the "Cancel checkout" / Resume path -- it must give the claimed promo slot
        // back exactly like CancelPendingChangesAsync does, or the slot leaks for good and the
        // agent's own RETRY finds "redemption limit reached" and silently pays full price plus
        // the un-waived setup fee. Wave-2 SLOT: released only AFTER the void committed -- the old
        // order released first, so a failing save left the row Pending and a retry released the
        // same claim twice, letting a capped code over-admit. Same floor-0 conditional decrement.
        if (pendingChange?.PromotionCodeId != null)
        {
            await _db.PromotionCodes
                .Where(p => p.Id == pendingChange.PromotionCodeId.Value && p.MaxRedemptions != null && p.RedemptionCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.RedemptionCount, p => p.RedemptionCount - 1));
        }
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

        // C2: resolve the end of the CURRENT paid cycle BEFORE cancelling at PayPal --
        // next_billing_time stops being reported once the subscription dies, and the clawback
        // below must anchor on the cycle PayPal is actually billing, not on activation day.
        var paidThroughEnd = await ResolvePaidThroughEndAsync(subscription);

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

        await ApplyCancellationOutcomeAsync(subscription, BillingStatus.Cancelled, paidThroughEnd,
            "Agent cancelled from IPRO billing");
        return true;
    }

    // DOCS/22 (2026-08-20): cancelled-but-paid-through. THE one place a subscription's death turns
    // into money-and-access truth, whoever pulled the trigger: the agent in our portal, the agent
    // inside PayPal's own interface (M5 -- our ToS explicitly invites that), or the reconcile job
    // discovering a lost webhook. Access is honored to what was paid for; an annual cancel before
    // the month-10 crossover produces a refund the owner processes manually from the SuperAdmin
    // refund queue. All math in PrepaidValue; every input recorded on the Cancel change row.
    internal async Task ApplyCancellationOutcomeAsync(
        IPRO.Entities.Billing subscription, BillingStatus targetStatus, DateTime paidThroughEnd, string trigger)
    {
        var now = DateTime.UtcNow;

        // F3c / jobs-4 (wave 4): a row with ANOTHER Active billing alongside it was SUPERSEDED --
        // its unused value already moved into the replacement as upgrade/convert proration
        // credit, and the system converges to one Active subscription per agent, so a second
        // Active row is only ever the replacement. Minting the DOCS/22 outcome here would hand
        // that value out twice (a full clawback refund on top of the credit), and the Applied
        // Cancel row would consume the H6 completion waiver. Raw flip instead: the subscription
        // ended because it was replaced, not because the agent walked away.
        var replacement = await _uow.Billings.FirstOrDefaultAsync(b =>
            b.AgentUserId == subscription.AgentUserId &&
            b.Id != subscription.Id &&
            b.Status == BillingStatus.Active);
        if (replacement != null)
        {
            subscription.Status = targetStatus;
            subscription.CancelledAt ??= now;
            _uow.Billings.Update(subscription);
            await _uow.SaveChangesAsync();
            _logger.LogInformation(
                "Billing {BillingId} (agent {AgentUserId}) was superseded by Active billing {ReplacementId}; flipped to {Status} with no cancellation outcome -- its value lives in the replacement. [{Trigger}]",
                subscription.Id, subscription.AgentUserId, replacement.Id, targetStatus, trigger);
            return;
        }

        // Wave-2 D (audit 2026-08-25): the outcome is minted EXACTLY ONCE, and the DATABASE --
        // not this caller's possibly-stale tracked entity -- decides who mints it. Three doors
        // (self-cancel, webhook, reconcile) can race: the webhook fires the instant our own
        // cancel API call lands, and the reconcile acts on rows it loaded minutes earlier. A
        // read-then-act status check let two doors both pass and put two identical Pending rows
        // in the manual refund queue. The fence is an INSERT with BillingId as the primary key,
        // committed in the SAME transaction as the outcome below: the losing door's duplicate-key
        // failure rolls its whole mint back, and a crash mid-mint rolls the claim back with it,
        // so the row stays Active and the next hourly pass retries cleanly. (A status-flip claim
        // was rejected precisely because it commits ahead of the outcome and a failure between
        // the two eats the cancellation.)
        _db.BillingCancellationClaims.Add(new BillingCancellationClaim
        {
            BillingId = subscription.Id,
            ClaimedAt = now,
            Trigger = trigger.Length <= 64 ? trigger : trigger[..64]
        });

        // C2: measure from the start of the CURRENT cycle. Billing.StartDate is written at
        // activation and never advanced on renewal, so anyone past their first anniversary
        // measured 12+ months "used", hit the crossover branch, and got $0 refund with a
        // PaidThroughAt in the PAST -- gated the moment they cancelled. Same prohibition the
        // upgrade path already obeys via GetCurrentCycleStart; the cancel path did not.
        var cycleStart = GetCurrentCycleStart(paidThroughEnd, subscription.Period);

        // Wave-2 A: the refund derives from money ACTUALLY SETTLED on this row for the RUNNING
        // cycle -- not from Billing.Amount, which is a price, not a payment. Deferred-start rows
        // (convert credits, annual-to-annual upgrades) carry an Amount that was never captured:
        // pricing the clawback off it minted phantom refunds (up to ~90% of an annual on $0
        // collected) and, for credit windows, slashed PaidThroughAt months into the past. The
        // window starts 3 days before cycleStart to absorb renewal invoices stamped slightly
        // before the anniversary. Base is clamped to Amount because the first-year invoice's
        // SubTotal includes the setup fee, which is not refundable prepaid value.
        var cyclePayments = (await _uow.Invoices.FindAsync(i =>
                i.BillingId == subscription.Id && i.IsPaid))
            .Where(i => i.SubTotal > 0m && i.IssuedAt >= cycleStart.AddDays(-3))
            .OrderByDescending(i => i.IssuedAt)
            .ToList();
        var settledThisCycle = cyclePayments.Sum(i => i.SubTotal);
        var refundBase = Math.Min(subscription.Amount, settledThisCycle);
        var payingInvoice = cyclePayments.FirstOrDefault();

        // M3: tax at the rate actually charged on the invoice being refunded (DOCS/22 line 36),
        // never the agent's CURRENT province -- repricing keeps HST already remitted to CRA.
        var lastPaid = (await _uow.Invoices.FindAsync(i => i.BillingId == subscription.Id && i.IsPaid))
            .OrderByDescending(i => i.IssuedAt)
            .FirstOrDefault();
        var rateSource = payingInvoice ?? lastPaid;
        var taxRate = rateSource != null
            ? rateSource.TaxRate
            : (await CalculateTaxAsync(subscription.AgentUserId, Math.Max(subscription.Amount, 0.01m))).Rate;

        // M4: the clawback's monthly rate stays Amount/10 (the documented two-months-free
        // structure, DOCS/22 line 45) so the month-10 crossover holds by construction; only the
        // refundable BASE shrinks to what was captured.
        var monthlyNetAtPurchase = subscription.Amount / 10m;

        var neverBilledThisCycle = now < cycleStart || refundBase <= 0m;

        // POLICY (owner decision 2026-08-25, wave 3): the refund is the FULL unused value at the
        // row's rate (Amount - used x Amount/10), capped at everything actually settled in the
        // running cycle across ALL the agent's rows. The wave-2 interim capped at this row's own
        // captures, which short-changed a mid-year upgrader by the old row's remainder (~$700 on
        // the worked example); the owner chose the simple agent-favouring rule -- "worst case I
        // lose ~2 months" -- and the cap guarantees the queue never instructs refunding more than
        // the cycle collected. When the refund exceeds this row's own capture, the note tells the
        // operator how much to take from which prior transaction.
        var outcome = subscription.Period == BillingPeriod.Annually && !neverBilledThisCycle
            ? PrepaidValue.AnnualCancel(subscription.Amount, monthlyNetAtPurchase, taxRate, cycleStart, now)
            : PrepaidValue.MonthlyCancel(paidThroughEnd, now);

        var crossRowNote = string.Empty;
        if (subscription.Period == BillingPeriod.Annually && !neverBilledThisCycle && outcome.RefundNet > 0m)
        {
            var otherRowPayments = (await _uow.Invoices.FindAsync(i =>
                    i.AgentUserId == subscription.AgentUserId && i.IsPaid && i.BillingId != subscription.Id))
                .Where(i => i.SubTotal > 0m && i.IssuedAt >= cycleStart.AddDays(-3))
                .OrderByDescending(i => i.IssuedAt)
                .ToList();
            var settledAcrossCycle = settledThisCycle + otherRowPayments.Sum(i => i.SubTotal);
            if (outcome.RefundNet > settledAcrossCycle)
            {
                var cappedNet = Math.Round(settledAcrossCycle, 2);
                var cappedTax = Math.Round(cappedNet * taxRate, 2);
                outcome = outcome with { RefundNet = cappedNet, RefundTax = cappedTax, RefundGross = cappedNet + cappedTax };
                crossRowNote += $" Refund capped at {cappedNet:0.00} -- the total settled this cycle across the agent's rows.";
            }
            if (otherRowPayments.Count > 0 && outcome.RefundNet > settledThisCycle)
            {
                var fromOthers = outcome.RefundNet - settledThisCycle;
                var otherTxns = string.Join(", ", otherRowPayments.Select(i => SettlingTransactionRef(i.PayPalTransactionId)).Where(t => t.Length > 0));
                crossRowNote += $" This row's capture covers {settledThisCycle:0.00} net; take the remaining {fromOthers:0.00} net against the prior transaction(s): {otherTxns}.";
            }
        }

        subscription.Status = targetStatus;
        subscription.CancelledAt ??= now;
        subscription.PaidThroughAt = outcome.PaidThroughAt;
        _uow.Billings.Update(subscription);

        await _uow.SubscriptionChanges.AddAsync(new SubscriptionChange
        {
            AgentUserId = subscription.AgentUserId,
            CurrentBillingRuleId = subscription.BillingRuleId,
            RequestedBillingRuleId = subscription.BillingRuleId,
            BillingId = subscription.Id,
            ChangeType = SubscriptionChangeType.Cancel,
            Status = SubscriptionChangeStatus.Applied,
            Period = subscription.Period,
            EffectiveDate = now,
            AppliedAt = now,
            AmountDue = 0m,
            Currency = subscription.Currency,
            RefundNetAmount = outcome.RefundNet,
            RefundTaxAmount = outcome.RefundTax,
            RefundGrossAmount = outcome.RefundGross,
            RefundStatus = outcome.RefundGross > 0m ? RefundStatus.Pending : RefundStatus.None,
            // The refund is claimed against the invoice that PAID the running cycle -- never the
            // merely-latest invoice, which for a convert is the $0 conversion record. The id is
            // normalized to the SETTLING transaction: a failed-payment marker appends every
            // retry's id comma-joined, the settled one last, and the raw list both overflows the
            // varchar(64) column and points the queue at a FAILED transaction.
            RefundPayPalTransactionId = SettlingTransactionRef(payingInvoice?.PayPalTransactionId),
            RefundWindowEndsAt = payingInvoice != null ? PrepaidValue.RefundWindowEndsAt(payingInvoice.IssuedAt) : null,
            RefundResolutionNote = (outcome.RefundGross > 0m
                ? $"Annual clawback: {outcome.MonthsUsed} month(s) of the cycle starting {cycleStart:yyyy-MM-dd} used at {monthlyNetAtPurchase:0.00}/mo (= priced {subscription.Amount:0.00}/10); refund {outcome.RefundNet:0.00} + tax {outcome.RefundTax:0.00} (rate {taxRate:0.###} as invoiced).{crossRowNote}"
                : subscription.Period == BillingPeriod.Annually && neverBilledThisCycle
                    ? $"No refund due (nothing has settled on this subscription for the running period -- deferred start or credit window); access honored to {outcome.PaidThroughAt:yyyy-MM-dd}."
                    : $"No refund due ({(subscription.Period == BillingPeriod.Annually ? $"month {outcome.MonthsUsed} is at/past the crossover" : "monthly plan")}); access honored to {outcome.PaidThroughAt:yyyy-MM-dd}.")
                + $" [{trigger}]"
        });

        try
        {
            await _uow.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            // Another door won the fence between this caller's status read and its save. Its mint
            // is complete (the claim only ever commits WITH an outcome); ours rolls back whole.
            // The tracker still holds our rejected claim + change row -- clear it so the caller's
            // loop (the reconcile iterates many rows) continues clean, M7-style.
            _db.ChangeTracker.Clear();
            subscription.Status = targetStatus;   // keep the caller's in-memory view coherent
            _logger.LogInformation(
                "Cancellation outcome for billing {BillingId} was minted by another door first; duplicate mint rolled back. [{Trigger}]",
                subscription.Id, trigger);
        }
    }

    private static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.InnerException is MySqlConnector.MySqlException { Number: 1062 };

    /// The transaction a manual refund must target: the last comma-separated segment (the settled
    /// payment -- failed-marker invoices append retry ids in order), with any failed-marker prefix
    /// stripped, clamped to the column's 64 chars.
    internal static string SettlingTransactionRef(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var last = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        const string failedPrefix = "PAYPAL_FAILED:";
        if (last.StartsWith(failedPrefix, StringComparison.Ordinal)) last = last[failedPrefix.Length..];
        return last.Length <= 64 ? last : last[..64];
    }

    public async Task<int> ProcessDueSubscriptionChangesAsync()
    {
        // H5: sweep stale checkouts first. A Pending change whose BILLING is still Pending is an
        // approval link nobody completed -- the agent closed the PayPal tab and never came back.
        // Left alone it holds its claimed promo slot forever (there was no sweeper at all), and
        // an abandoned CONVERT checkout would sit Pending indefinitely once H12 below stopped the
        // apply loop from wrongly eating it. 48 hours is far past any PayPal approval link's
        // usefulness. Scheduled downgrades are untouchable here BY SELECTION: they ride an
        // ACTIVE billing and may legitimately wait months. Guarded so a sweep failure cannot
        // block the due downgrades below (M19).
        try
        {
            var staleCutoff = DateTime.UtcNow.AddHours(-48);
            var staleCandidates = await _uow.SubscriptionChanges.FindAsync(c =>
                c.Status == SubscriptionChangeStatus.Pending && c.CreatedAt <= staleCutoff);
            var sweptAny = false;
            var slotsToRelease = new List<int>();
            foreach (var stale in staleCandidates)
            {
                var checkoutBilling = stale.BillingId.HasValue
                    ? await _uow.Billings.GetByIdAsync(stale.BillingId.Value)
                    : null;
                if (checkoutBilling == null || checkoutBilling.Status != BillingStatus.Pending) continue;

                // Wave-2 B (audit 2026-08-25): a checkout that reached PayPal is asked about at
                // PayPal BEFORE being voided locally -- "48h old here" says nothing about the
                // approval link there. Fail-safe in every uncertain direction: unreachable or
                // unrecognised -> leave it for an hour when PayPal can answer; ACTIVE/APPROVED ->
                // the agent COMPLETED this checkout and our activation was lost -- voiding it
                // guarantees PayPal bills forever against a Cancelled row, so it is left alone
                // and logged for the activation/reconcile machinery; APPROVAL_PENDING -> a cancel
                // is attempted best-effort (PayPal cannot always cancel an unapproved
                // subscription) and the sweep proceeds -- a late approval is then caught by the
                // stale-approval guard and its captured money by the refund-queue net in the sale
                // handler.
                if (!string.IsNullOrWhiteSpace(checkoutBilling.PayPalSubscriptionId))
                {
                    string paypalStatus;
                    try
                    {
                        (paypalStatus, _) = await GetPayPalSubscriptionSnapshotAsync(checkoutBilling.PayPalSubscriptionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Stale-checkout sweep: could not ask PayPal about subscription {SubscriptionId}; leaving change {ChangeId} for the next pass.",
                            checkoutBilling.PayPalSubscriptionId, stale.Id);
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(paypalStatus))
                    {
                        _logger.LogWarning(
                            "Stale-checkout sweep: PayPal gave no status for subscription {SubscriptionId}; leaving change {ChangeId} for the next pass.",
                            checkoutBilling.PayPalSubscriptionId, stale.Id);
                        continue;
                    }
                    if (paypalStatus.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) ||
                        paypalStatus.Equals("APPROVED", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError(
                            "Stale-checkout sweep: PayPal reports subscription {SubscriptionId} is {Status} but billing {BillingId} is still locally Pending -- an activation was lost. NOT sweeping; investigate/replay the activation.",
                            checkoutBilling.PayPalSubscriptionId, paypalStatus, checkoutBilling.Id);
                        continue;
                    }
                    if (paypalStatus.Equals("APPROVAL_PENDING", StringComparison.OrdinalIgnoreCase) &&
                        !await CancelPayPalSubscriptionAsync(checkoutBilling.PayPalSubscriptionId,
                            "Stale IPRO checkout swept after 48 hours."))
                    {
                        _logger.LogWarning(
                            "Stale-checkout sweep: could not cancel approval-pending subscription {SubscriptionId} at PayPal; sweeping locally anyway -- a late approval is caught by the stale-approval guard and the refund-queue net.",
                            checkoutBilling.PayPalSubscriptionId);
                    }
                }

                var sweepNow = DateTime.UtcNow;
                stale.Status = SubscriptionChangeStatus.Cancelled;
                stale.CancelledAt = sweepNow;
                _uow.SubscriptionChanges.Update(stale);
                checkoutBilling.Status = BillingStatus.Cancelled;
                checkoutBilling.CancelledAt = sweepNow;
                _uow.Billings.Update(checkoutBilling);
                if (stale.PromotionCodeId.HasValue)
                {
                    slotsToRelease.Add(stale.PromotionCodeId.Value);
                }
                sweptAny = true;
                _logger.LogInformation(
                    "Swept stale checkout: change {ChangeId} (agent {AgentUserId}, {ChangeType}) sat Pending since {CreatedAt:yyyy-MM-dd HH:mm}Z with billing {BillingId} never completed; cancelled and promo slot (if any) released.",
                    stale.Id, stale.AgentUserId, stale.ChangeType, stale.CreatedAt, checkoutBilling.Id);
            }
            if (sweptAny)
            {
                await _uow.SaveChangesAsync();
                // Wave-2 SLOT: the release happens ONLY after the void has committed. The old
                // order released first (ExecuteUpdate commits immediately) and a failing save
                // then left the row Pending -- re-swept and re-released every hour, letting a
                // capped code over-admit. A crash between the save and this loop leaks the slot
                // instead (fail-closed, billing's stated preference), and the leak is visible in
                // the log line above.
                foreach (var promoId in slotsToRelease)
                {
                    await _db.PromotionCodes
                        .Where(p => p.Id == promoId && p.MaxRedemptions != null && p.RedemptionCount > 0)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.RedemptionCount, p => p.RedemptionCount - 1));
                }
            }
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogError(ex, "Stale-checkout sweep failed; continuing with due subscription changes.");
        }

        // Same lead window as ApplyDuePendingChangesAsync: the whole point of firing early is that
        // the PayPal cancel lands BEFORE PayPal bills the next cycle, so the hourly selector must
        // see the change inside the window too, not only once the boundary has already passed.
        var dueBy = DateTime.UtcNow + DowngradeApplyLeadWindow;
        var dueChanges = await _uow.SubscriptionChanges.FindAsync(c =>
            c.Status == SubscriptionChangeStatus.Pending &&
            c.ChangeType == SubscriptionChangeType.Downgrade &&
            c.EffectiveDate <= dueBy);

        var applied = 0;
        foreach (var agentId in dueChanges.Select(c => c.AgentUserId).Distinct())
        {
            // A5-M-JOBISOLATION (fixed 2026-08-20): one agent's PayPal error used to abort the
            // whole hour's run -- every remaining agent's scheduled change waited for the next
            // tick, and kept waiting if the same agent kept failing. Each agent now fails alone.
            try
            {
                applied += await ApplyDuePendingChangesAsync(agentId);
            }
            catch (Exception ex)
            {
                // M7: continuing on a DIRTY shared change tracker is not isolation -- agent A's
                // unsaved poisoned mutations ride agent B's SaveChangesAsync and every remaining
                // agent fails identically. Drop A's partial work (the hourly retry redoes it) so
                // B starts clean.
                _db.ChangeTracker.Clear();
                _logger.LogError(ex,
                    "Applying due subscription changes for agent {AgentUserId} failed; tracker cleared, continuing with the remaining agents.",
                    agentId);
            }
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
            // M19: each problem billing fails alone. GetActiveSubscriptionAsync below reaches
            // PayPal (via the due-change apply) and the email leg can throw -- pre-fix, one bad
            // item aborted the whole run and every remaining agent's notice waited for an hour
            // that kept never coming while the same item kept failing first.
            try
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
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                _logger.LogError(ex,
                    "Billing-issue notification for billing {BillingId} (agent {AgentUserId}) failed; tracker cleared, continuing with the remaining items.",
                    billing.Id, billing.AgentUserId);
            }
        }

        var failedSubscriptionInvoices = (await _uow.Invoices.FindAsync(i =>
                !i.IsPaid && i.PayPalTransactionId.StartsWith("PAYPAL_FAILED:")))
            .OrderBy(i => i.IssuedAt)
            .ToList();
        foreach (var invoice in failedSubscriptionInvoices)
        {
            try
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
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                _logger.LogError(ex,
                    "Failed-payment notification for invoice {InvoiceId} failed; tracker cleared, continuing with the remaining items.",
                    invoice.Id);
            }
        }

        // Applied downgrades the agent never completed. The apply path sends exactly ONE email,
        // and this state creates neither a Failed billing nor an unpaid invoice, so neither block
        // above ever fires for it -- an agent who missed that one email simply stopped paying and
        // stopped being contacted (2026-08-16 audit). Two follow-ups at day 3 and day 7, deduped
        // through the same OperateLogs mechanism as the issue emails.
        //
        // M16: converts are excluded BY SELECTION -- a convert also lands as an Applied Downgrade
        // but needs no completion (its own billing WAS the new subscription; ProratedCredit > 0
        // marks it, same distinguisher as the H6 waiver). Dunning one is telling an agent to
        // finish something already finished.
        var staleDowngrades = (await _uow.SubscriptionChanges.FindAsync(c =>
                c.ChangeType == SubscriptionChangeType.Downgrade &&
                c.Status == SubscriptionChangeStatus.Applied &&
                c.ProratedCredit == 0m))
            .Where(c => c.AppliedAt.HasValue && c.AppliedAt.Value > DateTime.UtcNow.AddDays(-30))
            .ToList();
        foreach (var change in staleDowngrades)
        {
            try
            {
            var daysSince = (DateTime.UtcNow - change.AppliedAt!.Value).TotalDays;
            var bucket = daysSince >= 7 ? 7 : daysSince >= 3 ? 3 : 0;
            if (bucket == 0) continue;

            // LOW-8 (wave 5): the touches stay ORDERED. If no successful run landed inside the
            // day 3-7 window (job down, or this item's guard firing for days), the bucket jumped
            // straight to 7 and the day-3 touch silently never went out -- the two-touch design
            // degraded to one with no record. An overdue first run sends day 3 now; the next run
            // sends day 7.
            if (bucket == 7)
            {
                var day3Sent = await _uow.OperateLogs.FirstOrDefaultAsync(l =>
                    l.AgentUserId == change.AgentUserId &&
                    l.Module == "Billing" &&
                    l.Action == "DowngradeCompletionReminder" &&
                    l.Description == $"Change:{change.Id}:Day:3");
                if (day3Sent == null) bucket = 3;
            }

            // "Acted on it" means a live/in-flight billing now, OR any billing created after the
            // change applied, OR (M16) an Applied Cancel row after it -- an agent who answered the
            // downgrade by cancelling outright must not be dunned to "complete" it (the cancel
            // writes no Billing row, which is why the billing-only check missed it).
            var appliedAt = change.AppliedAt!.Value;
            var actedOn = (await _uow.Billings.FindAsync(b =>
                b.AgentUserId == change.AgentUserId &&
                (b.Status == BillingStatus.Active || b.Status == BillingStatus.Pending || b.CreatedAt > appliedAt))).Any();
            if (!actedOn)
            {
                // F3c (wave 4): a Cancel row on the downgrade's OWN billing is the old
                // subscription's death recorded by a racing door -- not the agent answering.
                actedOn = (await _uow.SubscriptionChanges.FindAsync(c =>
                    c.AgentUserId == change.AgentUserId &&
                    c.ChangeType == SubscriptionChangeType.Cancel &&
                    c.Status == SubscriptionChangeStatus.Applied)).Any(c =>
                        c.AppliedAt.HasValue && c.AppliedAt.Value > appliedAt &&
                        !(c.BillingId.HasValue && change.BillingId.HasValue && c.BillingId == change.BillingId));
            }
            if (actedOn) continue;

            var dedupKey = $"Change:{change.Id}:Day:{bucket}";
            var alreadySent = await _uow.OperateLogs.FirstOrDefaultAsync(l =>
                l.AgentUserId == change.AgentUserId &&
                l.Module == "Billing" &&
                l.Action == "DowngradeCompletionReminder" &&
                l.Description == dedupKey);
            if (alreadySent != null) continue;

            var requestedPackage = await _uow.BillingRules.GetByIdAsync(change.RequestedBillingRuleId);
            if (requestedPackage == null) continue;

            await SendDowngradeReadyToCompleteEmailAsync(change.AgentUserId, requestedPackage, change.Period);
            await _uow.OperateLogs.AddAsync(new OperateLog
            {
                AgentUserId = change.AgentUserId,
                Module = "Billing",
                Action = "DowngradeCompletionReminder",
                Description = dedupKey,
                CreatedAt = DateTime.UtcNow
            });
            await _uow.SaveChangesAsync();
            sent++;
            }
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                _logger.LogError(ex,
                    "Downgrade-completion reminder for change {ChangeId} (agent {AgentUserId}) failed; tracker cleared, continuing with the remaining items.",
                    change.Id, change.AgentUserId);
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

        // F5 (wave 3, 2026-08-25): promo plans price AGAINST this package and were frozen at
        // creation with no divergence guard of their own -- after a price edit + re-sync, a promo
        // checkout invoiced the NEW price while PayPal charged the OLD frozen one, forever (the
        // exact defect class ADMIN-2 closed for the package's own plans). Every sync clears the
        // cached promo plan ids for promos restricted to this package, BEFORE any PayPal call:
        // they lazily recreate at the next checkout against the CURRENT price, and a sync that
        // fails midway has still only forced a re-price, never a wrong one. The old promo plans
        // are left to age out at PayPal like any replaced plan (deactivation is best-effort
        // there too); nothing subscribes to them once the cache is gone.
        var frozenPromos = (await _uow.PromotionCodes.FindAsync(p =>
                p.RestrictedBillingRuleId == billingRuleId &&
                (p.PayPalPromoPlanIdMonthly != null && p.PayPalPromoPlanIdMonthly != "" ||
                 p.PayPalPromoPlanIdAnnual != null && p.PayPalPromoPlanIdAnnual != ""))).ToList();
        if (frozenPromos.Count > 0)
        {
            foreach (var frozen in frozenPromos)
            {
                _logger.LogInformation(
                    "Package {PackageId} plan sync: clearing frozen promo plan(s) for code {Code} (monthly '{M}', annual '{A}') so they re-price against the current package on next use.",
                    billingRuleId, frozen.Code, frozen.PayPalPromoPlanIdMonthly, frozen.PayPalPromoPlanIdAnnual);
                frozen.PayPalPromoPlanIdMonthly = string.Empty;
                frozen.PayPalPromoPlanIdAnnual = string.Empty;
                _uow.PromotionCodes.Update(frozen);
            }
            await _uow.SaveChangesAsync();
        }

        // ADMIN-9 shape, both halves fixed here. (1) Each plan id is PERSISTED the moment it is
        // created: the old code created monthly, then annual, then saved both -- so an exception on
        // the annual creation discarded a real, just-created monthly plan, leaving it live at
        // PayPal with no local record of its existence. (2) The plan id being REPLACED (or wiped by
        // a zeroed price) used to be simply overwritten -- the old plan stayed ACTIVE at PayPal,
        // subscribable, unfindable. It is now deactivated best-effort and the old->new transition is
        // written to OperateLogs either way, so an orphan is at worst a logged, deactivation-failed
        // plan rather than an untraceable one. Deactivation does not touch existing subscribers:
        // PayPal keeps billing active subscriptions on a deactivated plan; it only blocks NEW ones.
        var previousMonthlyPlanId = package.PayPalMonthlyPlanId?.Trim() ?? string.Empty;
        var previousAnnualPlanId = package.PayPalAnnualPlanId?.Trim() ?? string.Empty;
        try
        {
            var productId = await CreatePayPalProductAsync(package);

            var monthlyPlanId = package.MonthlyPrice > 0
                ? await CreatePayPalPlanAsync(productId, package, BillingPeriod.Monthly)
                : string.Empty;
            package.PayPalMonthlyPlanId = monthlyPlanId;
            // Snapshot the price the plan is frozen at (422b) -- the Packages screen warns on
            // divergence, and CreateSubscriptionAsync refuses checkout on it.
            package.PayPalMonthlyPlanPrice = string.IsNullOrEmpty(monthlyPlanId) ? null : package.MonthlyPrice;
            _uow.BillingRules.Update(package);
            await _uow.SaveChangesAsync();
            await RetireReplacedPlanAsync(package, "Monthly", previousMonthlyPlanId, monthlyPlanId);

            var annualPlanId = package.AnnualPrice > 0
                ? await CreatePayPalPlanAsync(productId, package, BillingPeriod.Annually)
                : string.Empty;
            package.PayPalAnnualPlanId = annualPlanId;
            package.PayPalAnnualPlanPrice = string.IsNullOrEmpty(annualPlanId) ? null : package.AnnualPrice;
            _uow.BillingRules.Update(package);
            await _uow.SaveChangesAsync();
            await RetireReplacedPlanAsync(package, "Annual", previousAnnualPlanId, annualPlanId);

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

    // When a re-sync replaces or wipes a plan id, the OLD plan does not disappear from PayPal --
    // deactivate it so it cannot take new subscribers, and record the transition in OperateLogs so
    // it can always be found again. Best-effort by design: a deactivation failure must not fail the
    // sync (the new plans are already live and saved), it just leaves a logged, findable orphan.
    private async Task RetireReplacedPlanAsync(BillingRule package, string periodName, string previousPlanId, string newPlanId)
    {
        if (string.IsNullOrWhiteSpace(previousPlanId) || string.Equals(previousPlanId, newPlanId, StringComparison.Ordinal))
        {
            return;
        }

        var deactivated = false;
        try
        {
            var accessToken = await GetPayPalAccessTokenAsync();
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.PostAsync(
                $"{_settings.BaseUrl}/v1/billing/plans/{previousPlanId}/deactivate",
                new StringContent("", System.Text.Encoding.UTF8, "application/json"));
            // 204 = deactivated; 422 UNPROCESSABLE typically means it already was. Either way the
            // plan can no longer take new subscribers.
            deactivated = response.IsSuccessStatusCode || (int)response.StatusCode == 422;
            if (!deactivated)
            {
                _logger.LogWarning(
                    "Could not deactivate replaced PayPal {Period} plan {PlanId} for package {PackageId}: HTTP {Status}. " +
                    "The plan is still ACTIVE at PayPal and recorded only in OperateLogs.",
                    periodName, previousPlanId, package.Id, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not deactivate replaced PayPal {Period} plan {PlanId} for package {PackageId}. " +
                "The plan is still ACTIVE at PayPal and recorded only in OperateLogs.",
                periodName, previousPlanId, package.Id);
        }

        await _uow.OperateLogs.AddAsync(new OperateLog
        {
            AgentUserId = 0,
            Module = "Billing",
            Action = "PayPalPlanReplaced",
            Description = $"Package:{package.Id}:{periodName}:old={previousPlanId}:new={(string.IsNullOrEmpty(newPlanId) ? "(none)" : newPlanId)}:deactivated={deactivated}",
            CreatedAt = DateTime.UtcNow
        });
        await _uow.SaveChangesAsync();
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

        // Same rule as CapturePaymentAsync's status guard, or the webhook is a back door around
        // it (review pass): a Cancelled/Expired/Failed billing means this checkout was superseded
        // locally -- a later plan change, a "Keep My Current Plan" click, or a full cancel -- but
        // the buyer completed the stale PayPal approval link anyway. Activating would resurrect
        // the dead row AND let a still-Pending downgrade destroy the survivor later. The PayPal
        // subscription the buyer just approved is REAL and will bill, so it must be stopped, not
        // just ignored -- an unstopped one is exactly the orphan the hourly reconcile can't see
        // (it only checks rows we consider Active).
        if (billing.Status is BillingStatus.Cancelled or BillingStatus.Expired or BillingStatus.Failed)
        {
            if (await CancelPayPalSubscriptionAsync(billing.PayPalSubscriptionId, "Checkout completed after this plan change was superseded in IPRO."))
            {
                _logger.LogWarning(
                    "ACTIVATED webhook for superseded billing {BillingId} (agent {AgentUserId}, status {Status}): the stale PayPal subscription {SubscriptionId} was cancelled instead of resurrecting the row.",
                    billing.Id, billing.AgentUserId, billing.Status, billing.PayPalSubscriptionId);
            }
            else
            {
                _logger.LogError(
                    "ACTIVATED webhook for superseded billing {BillingId} (agent {AgentUserId}): could NOT cancel stale PayPal subscription {SubscriptionId} -- it may bill an account we consider closed. Verify at PayPal.",
                    billing.Id, billing.AgentUserId, billing.PayPalSubscriptionId);
            }
            return true;
        }

        var invoice = (await _uow.Invoices.FindAsync(i => i.BillingId == billing.Id && !i.IsPaid))
            .OrderByDescending(i => i.IssuedAt)
            .FirstOrDefault();
        await ActivateSubscriptionBillingAsync(billing.AgentUserId, billing, invoice, "PayPal subscription activated.");
        return true;
    }

    internal async Task<bool> HandleSubscriptionCancelledWebhookAsync(string subscriptionId, BillingStatus status)
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

        // M5: a PayPal-initiated cancellation is still a cancellation under DOCS/22 -- the agent
        // owns what they paid for, and an annual cancel before the crossover is owed a refund.
        // This used to set Status/CancelledAt raw: PaidThroughAt stayed null (= old-style
        // IMMEDIATE gate) and no refund row was ever written, so cancelling "through PayPal" --
        // which our shipped ToS explicitly invites -- silently forfeited both. F6 (wave 4): a
        // SUSPENDED (Failed) row takes this door too -- the suspension gated the agent as a
        // payment problem, but a cancel that follows it still ends a subscription with unconsumed
        // paid time, and the raw path made Failed an antechamber that forfeited the whole
        // outcome. Suspension itself (-> Failed) keeps the raw path and the immediate gate on
        // purpose; the double-mint fence makes a replayed CANCELLED after this idempotent.
        if ((billing.Status == BillingStatus.Active || billing.Status == BillingStatus.Failed) &&
            (status == BillingStatus.Cancelled || status == BillingStatus.Expired))
        {
            var paidThroughEnd = await ResolvePaidThroughEndAsync(billing);
            await ApplyCancellationOutcomeAsync(billing, status, paidThroughEnd,
                status == BillingStatus.Expired ? "PayPal reported the subscription expired" : "Cancelled at PayPal (webhook)");
            return true;
        }

        // F6 (wave 4): a TERMINAL state is never relabelled by a late delivery. PayPal retries
        // failed deliveries for days, so a SUSPENDED (or stale EXPIRED) event from BEFORE a
        // cancellation can land after it -- and relabelling Cancelled -> Failed silently voided
        // the paid-through honor (every gate reads Cancelled/Expired only) with no repair path,
        // because the reconcile inspects Active rows exclusively.
        if (billing.Status is BillingStatus.Cancelled or BillingStatus.Expired)
        {
            _logger.LogWarning(
                "Ignoring late {Incoming} delivery for subscription {SubscriptionId}: billing {BillingId} is already terminally {Current} and keeps its paid-through honor.",
                status, subscriptionId, billing.Id, billing.Status);
            return true;
        }

        var wasPendingCheckout = billing.Status == BillingStatus.Pending &&
            (status == BillingStatus.Cancelled || status == BillingStatus.Expired);

        billing.Status = status;
        billing.CancelledAt ??= DateTime.UtcNow;
        _uow.Billings.Update(billing);

        // LOW-5 (wave 5): a CANCELLED/EXPIRED delivery for a PENDING checkout takes the whole
        // checkout with it -- flipping only the billing orphaned the Pending change row (the 48h
        // sweep skips non-Pending billings) with its claimed promo slot and a stale banner, until
        // the agent's next subscribe action happened to clean it. Slot released after the save,
        // per SLOT.
        int? orphanedPromoId = null;
        if (wasPendingCheckout)
        {
            var orphanedChange = await _uow.SubscriptionChanges.FirstOrDefaultAsync(c =>
                c.BillingId == billing.Id && c.Status == SubscriptionChangeStatus.Pending);
            if (orphanedChange != null)
            {
                orphanedChange.Status = SubscriptionChangeStatus.Cancelled;
                orphanedChange.CancelledAt = DateTime.UtcNow;
                _uow.SubscriptionChanges.Update(orphanedChange);
                orphanedPromoId = orphanedChange.PromotionCodeId;
            }
        }

        await _uow.SaveChangesAsync();
        if (orphanedPromoId.HasValue)
        {
            await _db.PromotionCodes
                .Where(p => p.Id == orphanedPromoId.Value && p.MaxRedemptions != null && p.RedemptionCount > 0)
                .ExecuteUpdateAsync(su => su.SetProperty(p => p.RedemptionCount, p => p.RedemptionCount - 1));
        }
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
        // The oldest-unpaid fallback must never hand a REAL charge to a $0 invoice: new $0
        // adjustment invoices are now born paid, but rows minted before that fix still exist in
        // production, and marking one paid with a $60 transaction sends a $0.00 receipt while the
        // $60 goes uninvoiced.
        var pendingInvoice = amount > 0
            ? unpaidInvoices.FirstOrDefault(i => Math.Abs(i.Total - amount) <= 0.02m)
                ?? unpaidInvoices.FirstOrDefault(i => i.Total > 0)
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
            // F2 (wave 3, 2026-08-25): de-tax at the rate this subscription was actually SOLD at
            // -- the rate on its last paid invoice -- never the agent's CURRENT province. The
            // PayPal plan's gross was built tax-inclusive at creation time and does not change
            // when the agent moves; splitting $678.00 (built as $600 + 13% ON) at Alberta's 5%
            // recorded $645.71 + $32.29 GST, corrupted the CRA remittance on both provinces, and
            // the Amount-sync below then poisoned every later proration and clawback with 645.71.
            var soldAtInvoice = (await _uow.Invoices.FindAsync(i =>
                    i.BillingId == billing.Id && i.IsPaid))
                .Where(i => i.SubTotal > 0m)
                .OrderByDescending(i => i.IssuedAt)
                .FirstOrDefault();

            decimal recurringAmount;
            if (amount > 0)
            {
                var soldAtRate = soldAtInvoice?.TaxRate
                    ?? (await CalculateTaxAsync(billing.AgentUserId, amount)).Rate;
                recurringAmount = soldAtRate > 0
                    ? Math.Round(amount / (1 + soldAtRate), 2)
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
                else if (recurringAmount > 0)
                {
                    // PayPal's settled charge IS what this subscription costs per cycle now --
                    // keep Billing.Amount in step with it. A duration-limited promo writes the
                    // discounted price into Amount at signup and nothing updated it when the promo
                    // cycles lapsed and PayPal began charging full price, so upgrade proration
                    // credited the stale discount forever (2026-08-16 review pass).
                    _logger.LogInformation(
                        "Billing {BillingId}: settled charge {Charged:F2} differs from stored Amount {Stored:F2}; Amount updated to match what PayPal actually bills.",
                        billing.Id, recurringAmount, billing.Amount);
                    billing.Amount = recurringAmount;
                }
            }
            else
            {
                recurringAmount = billing.Amount;
            }

            // F2: the invoice carries the sold-at rate too -- computing it fresh inside
            // CreateInvoiceAsync would re-apply the CURRENT province and reintroduce the split
            // this block just avoided.
            var invoice = package == null
                ? await CreateInvoiceAsync(billing.Id, billing.AgentUserId, recurringAmount, true)
                : await CreateInvoiceAsync(billing.Id, billing.AgentUserId, package, billing.Period, recurringAmount, 0, true,
                    taxRateOverride: soldAtInvoice?.TaxRate, taxRegionOverride: soldAtInvoice?.TaxRegion);
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

            // Wave-2 B (audit 2026-08-25): money captured against a subscription we consider ended
            // must land in the REFUND QUEUE, not just an error log -- a swept checkout whose
            // approval link was completed late, a lost-cancel row PayPal keeps billing, the
            // resurrect-guard cases: in every one of them a real charge existed only as a log line
            // nobody works. One row per settled transaction (the txn-id replay guard upstream
            // makes this idempotent); AppliedAt stays NULL on purpose -- this is a money-recovery
            // marker, not an agent action, and both the H6 waiver consumption and the M16 dunning
            // suppression key on AppliedAt.
            if (amount > 0m && !string.IsNullOrWhiteSpace(transactionId))
            {
                await _uow.SubscriptionChanges.AddAsync(new SubscriptionChange
                {
                    AgentUserId = billing.AgentUserId,
                    CurrentBillingRuleId = billing.BillingRuleId,
                    RequestedBillingRuleId = billing.BillingRuleId,
                    BillingId = billing.Id,
                    ChangeType = SubscriptionChangeType.Cancel,
                    Status = SubscriptionChangeStatus.Applied,
                    Period = billing.Period,
                    EffectiveDate = DateTime.UtcNow,
                    AppliedAt = null,
                    AmountDue = 0m,
                    Currency = billing.Currency,
                    RefundNetAmount = pendingInvoice?.SubTotal ?? amount,
                    RefundTaxAmount = pendingInvoice?.TaxAmount ?? 0m,
                    RefundGrossAmount = pendingInvoice?.Total ?? amount,
                    RefundStatus = RefundStatus.Pending,
                    RefundPayPalTransactionId = SettlingTransactionRef(transactionId),
                    RefundWindowEndsAt = PrepaidValue.RefundWindowEndsAt(DateTime.UtcNow),
                    RefundResolutionNote =
                        $"PayPal captured {amount:0.00} {billing.Currency} (txn {transactionId}) against {billing.Status} subscription {billing.PayPalSubscriptionId} -- the agent received nothing for it. Refund at PayPal, then verify the subscription is stopped there. [captured-after-end net]"
                });
            }
        }
        else
        {
            if (billing.Status != BillingStatus.Active)
            {
                billing.Status = BillingStatus.Active;
                billing.StartDate = DateTime.UtcNow;
                // LOW-6 (wave 5): the suspension-era timestamp must not survive the recovery --
                // every later cancellation door writes CancelledAt with ??=, so a REAL cancel
                // months from now would keep showing the old suspension date in Admin.
                billing.CancelledAt = null;
            }
            await SyncAgentPackageAsync(billing.AgentUserId, billing.BillingRuleId);
            // Same keep-vs-recompute rule as activation -- a deferred upgrade's SETUP-FEE sale
            // lands here minutes after approval, and the unconditional recompute re-corrupted the
            // deferred first-charge date the activation fix had just preserved (review pass).
            billing.NextBillingDate = ResolveNextBillingDateOnPayment(billing.NextBillingDate, DateTime.UtcNow, billing.Period);
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
        DateTime? deferredStart = null,
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

        // A $0 adjustment invoice is settled at ACTIVATION (ActivateSubscriptionBillingAsync), not
        // here at creation: born-paid stranded abandoned zero-due checkouts -- with no unpaid
        // invoice, GetBillingIssueAsync raised no "payment pending" banner and no Resume button,
        // so an agent who closed the PayPal tab was silently stuck (review pass). Unpaid until the
        // checkout completes, settled the moment it does; the webhook's oldest-unpaid fallback
        // skips $0 rows either way.
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
                              || changeType == SubscriptionChangeType.Upgrade
                              // A convert-downgrade (DOCS/22) also supersedes: new sub, deferred start.
                              || deferredStart.HasValue;

        if (startsSubscription && !string.IsNullOrWhiteSpace(billing.PayPalPlanId))
        {
            // For an upgrade the prorated amount is the up-front charge; the plan carries the
            // recurring price from the next cycle on.
            var subscriptionSetupFee = changeType == SubscriptionChangeType.Upgrade
                ? setupFee + Math.Max(0, amountDue)
                : setupFee;

            // Upgrades defer the first recurring charge to the date the agent has already paid up to.
            // A new subscription passes null and bills immediately, which is correct for a signup.
            var subscriptionStart = changeType == SubscriptionChangeType.Upgrade ? nextBillingDate : deferredStart;

            // The gross-up rate comes from the AGENT, probed against the plan's own recurring
            // price -- the invoice's rate is 0 whenever the invoice is $0, and that must not
            // decide how the recurring charge is taxed for the life of the subscription.
            var subscriptionTaxRate = invoice.TaxRate > 0
                ? invoice.TaxRate
                : (await CalculateTaxAsync(userId, Math.Max(billing.Amount, 0.01m))).Rate;

            PayPalSubscriptionResult subscription;
            try
            {
                subscription = await CreatePayPalSubscriptionAsync(invoice, requestedPackage, period, subscriptionSetupFee, returnUrl, cancelUrl, subscriptionTaxRate, billing.PayPalPlanId, subscriptionStart);
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

    // The date the agent's current paid period actually ends. It decides when a scheduled change
    // fires and how much unused value a convert-downgrade credits, so it must come from PayPal's
    // own schedule, not from the local NextBillingDate column -- that column has been wrong before
    // (an upgrade's deferred start was clobbered to "now + one period"), and freezing a wrong date
    // would destroy a subscription with months of prepaid service left (2026-08-16 audit, the
    // owner's own account). PayPal unreachable or no linked subscription (comped agents) falls
    // back to the local value; a confirmed >26h drift also repairs the local column on the spot.
    private async Task<DateTime> ResolvePaidThroughEndAsync(IPRO.Entities.Billing currentSubscription)
    {
        DateTime? payPalNextBilling = null;
        if (!string.IsNullOrWhiteSpace(currentSubscription.PayPalSubscriptionId))
        {
            try
            {
                var snapshot = await GetPayPalSubscriptionSnapshotAsync(currentSubscription.PayPalSubscriptionId);
                payPalNextBilling = snapshot.NextBillingTime;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not read PayPal's next billing time for subscription {SubscriptionId}; falling back to the locally stored date.",
                    currentSubscription.PayPalSubscriptionId);
            }
        }

        if (payPalNextBilling.HasValue && currentSubscription.NextBillingDate.HasValue &&
            Math.Abs((payPalNextBilling.Value - currentSubscription.NextBillingDate.Value).TotalHours) > 26)
        {
            currentSubscription.NextBillingDate = payPalNextBilling.Value;
            _uow.Billings.Update(currentSubscription);
        }

        return payPalNextBilling
            ?? currentSubscription.NextBillingDate
            ?? GetNextBillingDate(DateTime.UtcNow, currentSubscription.Period);
    }

    private async Task<DateTime> ScheduleDowngradeAsync(int userId, IPRO.Entities.Billing currentSubscription, BillingRule currentPackage, BillingRule requestedPackage, BillingPeriod period)
    {
        await CancelPendingChangesAsync(userId);
        var effectiveDate = await ResolvePaidThroughEndAsync(currentSubscription);

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
        return effectiveDate;
    }

    // The agent-facing undo for a scheduled downgrade/term switch. Until this existed a mis-click
    // was locked in: no endpoint could clear the pending change, and the UI disables both the
    // current package's button and the target's, so the only self-service escapes were subscribing
    // to a THIRD package or cancelling the whole subscription (2026-08-16 audit). Deliberately
    // scoped to Downgrade-type changes -- an in-flight upgrade checkout has its own cancel flow
    // and its Pending billing must not be touched from here.
    public async Task<BillingChangeResult> CancelScheduledChangeAsync(int userId)
    {
        var pendingDowngrades = (await _uow.SubscriptionChanges.FindAsync(c =>
            c.AgentUserId == userId &&
            c.Status == SubscriptionChangeStatus.Pending &&
            c.ChangeType == SubscriptionChangeType.Downgrade)).ToList();

        if (pendingDowngrades.Count == 0)
        {
            return BillingChangeResult.Failed("There is no scheduled plan change to cancel.");
        }

        var undoPromoIds = new List<int>();
        foreach (var change in pendingDowngrades)
        {
            change.Status = SubscriptionChangeStatus.Cancelled;
            change.CancelledAt = DateTime.UtcNow;
            _uow.SubscriptionChanges.Update(change);

            // F5 (wave 4): a Downgrade row whose billing is PENDING is an in-flight CONVERT
            // checkout, not a scheduled change (the H12 lesson) -- cancelling only the change row
            // stranded the Pending billing forever (no sweeper matches change-Cancelled +
            // billing-Pending), kept the promo slot claimed, and left the PayPal approval link
            // LIVE: approving it later executed the very change the agent had just undone. Undo
            // the whole checkout: void the billing, release the slot (after the save, per SLOT),
            // and best-effort cancel the approval at PayPal -- a capture that slips through
            // anyway is caught by the captured-after-end refund net.
            var checkoutBilling = change.BillingId.HasValue
                ? await _uow.Billings.GetByIdAsync(change.BillingId.Value)
                : null;
            if (checkoutBilling != null && checkoutBilling.Status == BillingStatus.Pending)
            {
                if (!string.IsNullOrWhiteSpace(checkoutBilling.PayPalSubscriptionId) &&
                    !await CancelPayPalSubscriptionAsync(checkoutBilling.PayPalSubscriptionId,
                        "Agent kept their current plan; convert checkout undone."))
                {
                    _logger.LogWarning(
                        "Keep-My-Current-Plan: could not cancel approval-pending subscription {SubscriptionId} at PayPal; voiding locally anyway -- a late capture lands in the refund queue.",
                        checkoutBilling.PayPalSubscriptionId);
                }
                checkoutBilling.Status = BillingStatus.Cancelled;
                checkoutBilling.CancelledAt = DateTime.UtcNow;
                _uow.Billings.Update(checkoutBilling);
                if (change.PromotionCodeId.HasValue)
                {
                    undoPromoIds.Add(change.PromotionCodeId.Value);
                }
            }
        }

        await _uow.SaveChangesAsync();
        foreach (var promoId in undoPromoIds)
        {
            await _db.PromotionCodes
                .Where(p => p.Id == promoId && p.MaxRedemptions != null && p.RedemptionCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.RedemptionCount, p => p.RedemptionCount - 1));
        }
        return new BillingChangeResult
        {
            Success = true,
            Message = "Your scheduled plan change has been cancelled. Your current package continues unchanged."
        };
    }

    private async Task CancelPendingChangesAsync(int userId)
    {
        var pendingChanges = await _uow.SubscriptionChanges.FindAsync(c =>
            c.AgentUserId == userId && c.Status == SubscriptionChangeStatus.Pending);

        var releasablePromoIds = new List<int>();
        foreach (var change in pendingChanges)
        {
            change.Status = SubscriptionChangeStatus.Cancelled;
            change.CancelledAt = DateTime.UtcNow;
            _uow.SubscriptionChanges.Update(change);
            if (change.PromotionCodeId.HasValue)
            {
                releasablePromoIds.Add(change.PromotionCodeId.Value);
            }
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

        // M-8: a dead checkout gives its claimed promo slot back (floor 0 -- a release can never
        // invent capacity). Runs exactly once per change because only a Pending row can enter the
        // loop above, and it leaves as Cancelled. Wave-2 SLOT: released only AFTER the void
        // committed -- releasing first meant a failed save left the rows Pending and the retry
        // released the same claims twice, letting a capped code over-admit.
        foreach (var promoId in releasablePromoIds)
        {
            await _db.PromotionCodes
                .Where(p => p.Id == promoId && p.MaxRedemptions != null && p.RedemptionCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.RedemptionCount, p => p.RedemptionCount - 1));
        }
    }

    // How early a due downgrade may fire. The apply predicate used to be EffectiveDate <= now with
    // an hourly job behind it, so the PayPal cancel could only land AT or AFTER the instant PayPal's
    // engine bills the next cycle -- lose that race and the agent pays a full extra term (a whole
    // YEAR on annual) seconds before being cancelled, with no refund path. Firing inside this window
    // BEFORE the boundary means the cancel always beats the charge; the agent gives up at most a few
    // hours of already-paid service, against a whole billing period of wrong charges.
    private static readonly TimeSpan DowngradeApplyLeadWindow = TimeSpan.FromHours(6);

    private async Task<int> ApplyDuePendingChangesAsync(int userId)
    {
        var now = DateTime.UtcNow;
        var dueBy = now + DowngradeApplyLeadWindow;
        var dueChanges = await _uow.SubscriptionChanges.FindAsync(c =>
            c.AgentUserId == userId &&
            c.Status == SubscriptionChangeStatus.Pending &&
            c.ChangeType == SubscriptionChangeType.Downgrade &&
            c.EffectiveDate <= dueBy);

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

            // Cancel ONLY the subscription this change was scheduled against. The loop used to
            // cancel every Active row for the agent, so a stale pending downgrade could destroy a
            // NEWER, fully-paid subscription created after it (the resurrected-billing collision,
            // 2026-08-16 audit). If the referenced billing is no longer Active, the world moved on
            // -- the change is stale and cancels itself instead of firing.
            var subscription = change.BillingId.HasValue
                ? await _uow.Billings.GetByIdAsync(change.BillingId.Value)
                : null;

            // H12: a Downgrade row whose BillingId points at a PENDING billing is a CONVERT
            // checkout in flight (BeginPaidChangeAsync writes the NEW billing's id), not a
            // scheduled downgrade against a dead subscription. The agent may still be at PayPal
            // approving it -- it is not ours to apply OR to cancel: completion activates it, the
            // Cancel-checkout button voids it, and the 48h stale sweep above cleans true
            // abandonment. Pre-fix this loop read "not Active" as "stale" and ate the convert
            // within the hour, which also wrongly lifted the UX-TERMSWITCH guard.
            if (subscription != null && subscription.Status == BillingStatus.Pending)
            {
                continue;
            }

            if (subscription == null || subscription.Status != BillingStatus.Active)
            {
                change.Status = SubscriptionChangeStatus.Cancelled;
                change.CancelledAt = now;
                _uow.SubscriptionChanges.Update(change);
                await _uow.SaveChangesAsync();
                _logger.LogInformation(
                    "Scheduled downgrade {ChangeId} for agent {AgentUserId} references billing {BillingId} which is no longer Active; the change is stale and has been cancelled without firing.",
                    change.Id, userId, change.BillingId);
                continue;
            }

            // Last look at PayPal before destroying a subscription: if PayPal's own schedule says
            // the real boundary is still far away, the frozen EffectiveDate was stale (the exact
            // corruption the 2026-08-16 audit found on the owner's account -- local date Sep 16,
            // real PayPal charge the following July). Push the change to PayPal's date and walk
            // away; firing on a stale date forfeits every prepaid day between the two.
            if (!string.IsNullOrWhiteSpace(subscription.PayPalSubscriptionId))
            {
                DateTime? payPalNextBilling = null;
                try
                {
                    var snapshot = await GetPayPalSubscriptionSnapshotAsync(subscription.PayPalSubscriptionId);
                    payPalNextBilling = snapshot.NextBillingTime;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Could not confirm PayPal's next billing time before applying downgrade {ChangeId}; proceeding on the scheduled date.",
                        change.Id);
                }

                if (payPalNextBilling.HasValue && payPalNextBilling.Value > dueBy.AddHours(20))
                {
                    change.EffectiveDate = payPalNextBilling.Value;
                    _uow.SubscriptionChanges.Update(change);
                    if (!subscription.NextBillingDate.HasValue ||
                        Math.Abs((subscription.NextBillingDate.Value - payPalNextBilling.Value).TotalHours) > 26)
                    {
                        subscription.NextBillingDate = payPalNextBilling.Value;
                        _uow.Billings.Update(subscription);
                    }
                    await _uow.SaveChangesAsync();
                    _logger.LogWarning(
                        "Scheduled downgrade {ChangeId} for agent {AgentUserId} was due locally but PayPal will not bill until {PayPalDate:yyyy-MM-dd}; the change has been re-scheduled to that date instead of cancelling a paid-up subscription early.",
                        change.Id, userId, payPalNextBilling.Value);
                    continue;
                }
            }

            // Shrink the undo race to milliseconds: the agent may have clicked "Keep My Current
            // Plan" (CancelScheduledChangeAsync, a separate DbContext) while this loop was busy
            // with the PayPal round-trips above. A fresh untracked read just before the
            // irreversible cancel is the last cheap chance to notice; a residual race narrower
            // than this needs row locking and is accepted (review pass).
            var freshStatus = await _db.SubscriptionChanges.AsNoTracking()
                .Where(c => c.Id == change.Id)
                .Select(c => (SubscriptionChangeStatus?)c.Status)
                .FirstOrDefaultAsync();
            if (freshStatus != SubscriptionChangeStatus.Pending)
            {
                continue;
            }

            // Review H-1: only a confirmed PayPal stop may mark the row Cancelled. Unlike the
            // upgrade paths, nothing has been charged yet here, so a failure can simply leave
            // the change Pending -- this job runs hourly and retries it naturally.
            if (!await CancelPayPalSubscriptionAsync(subscription.PayPalSubscriptionId, "Replaced by a scheduled IPRO package downgrade."))
            {
                _logger.LogError(
                    "Billing {BillingId} (PayPal {SubscriptionId}) could not be cancelled for agent {AgentUserId}'s scheduled downgrade; leaving the change Pending to retry next run.",
                    subscription.Id, subscription.PayPalSubscriptionId, userId);
                await _uow.SaveChangesAsync();
                continue;
            }

            subscription.Status = BillingStatus.Cancelled;
            subscription.CancelledAt = now;
            _uow.Billings.Update(subscription);

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

            await SendDowngradeReadyToCompleteEmailAsync(userId, requestedPackage, change.Period);
            applied++;
        }

        return applied;
    }

    private async Task SendDowngradeReadyToCompleteEmailAsync(int userId, BillingRule requestedPackage, BillingPeriod period)
    {
        var agent = await _uow.AgentUsers.GetByIdAsync(userId);
        if (agent == null || string.IsNullOrWhiteSpace(agent.Email)) return;

        var fullName = $"{agent.FirstName} {agent.LastName}".Trim();
        // LOW-9 (wave 5): name the TERM the agent picked. A term switch names the same package on
        // both sides, so an email naming only the package let the agent complete on their OLD
        // term -- the fee waiver matches on package id alone and would have blessed it.
        var periodLabel = period == BillingPeriod.Annually ? "annual billing" : "monthly billing";
        var billingUrl = BuildBillingPageUrl();
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#193f82;color:white"><h1 style="margin:0;font-size:24px">IPRO Advisers</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p>Hi {System.Net.WebUtility.HtmlEncode(fullName)},</p>
                <p>Your scheduled plan change to <strong>{System.Net.WebUtility.HtmlEncode(requestedPackage.PackageName)} ({periodLabel})</strong> is now in effect, and your previous subscription has been cancelled.</p>
                <p>One step left: PayPal requires you to re-approve a new subscription any time the plan changes, so please visit Billing to finish subscribing to {System.Net.WebUtility.HtmlEncode(requestedPackage.PackageName)} on {periodLabel}. No setup fee applies. Until then, your account will be limited to the Billing page.</p>
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

    private async Task<IPRO.Entities.Invoice> CreateInvoiceAsync(int billingId, int userId, BillingRule package, BillingPeriod period, decimal recurringAmount, decimal setupFee, bool isPaid,
        decimal? taxRateOverride = null, string? taxRegionOverride = null)
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
        // F2 (wave 3): a renewal invoice keeps the rate the subscription was SOLD at (the caller
        // passes it from the prior paid invoice). Everything else keeps pricing at the agent's
        // current province, which is correct for NEW money.
        if (taxRateOverride.HasValue)
        {
            var overrideAmount = Math.Round(subtotal * taxRateOverride.Value, 2);
            return await CreateInvoiceWithLinesAsync(billingId, userId, subtotal, overrideAmount,
                taxRateOverride.Value, taxRegionOverride ?? string.Empty, isPaid, lineItems);
        }
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

    internal static string NormalizeProvince(string? province)
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
        // F3 (wave 3): the universal abbreviation; missing it zero-rated PEI's 15% HST silently.
        ["PEI"] = "PE",
        ["P.E.I."] = "PE",
        ["QUEBEC"] = "QC",
        ["QUÉBEC"] = "QC",
        ["SASKATCHEWAN"] = "SK",
        ["YUKON"] = "YT",
        // F3 (wave 3): the register's own dropdown emits THIS label -- every Yukon signup
        // zero-rated from day one because only bare "YUKON" was mapped.
        ["YUKON TERRITORY"] = "YT",
        ["NWT"] = "NT"
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
              <p>{(status == BillingStatus.Failed
                    ? "Your subscription is suspended at PayPal, so there is nothing to retry here. Please sign in to your IPRO Agent Portal, open <strong>Billing</strong>, and subscribe again -- or contact support and we will help."
                    : "Please sign in to your IPRO Agent Portal and open <strong>Billing</strong> to complete your payment.")}</p>
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

    // taxRate is resolved by the CALLER from the agent's province, never taken from the first
    // invoice: a zero-due change (e.g. an upgrade fully covered by proration credit) produces a $0
    // invoice whose TaxRate short-circuits to 0, and gating the gross-up on that billed the
    // subscription NET for its whole life -- HST simply never collected (audit issue #7, first hit
    // live on the 2026-08-16 annual->monthly upgrade). The invoice's own rate and the agent's rate
    // are the same number whenever the invoice has a positive subtotal; they diverge only in the
    // $0 case this parameter exists to fix.
    private async Task<PayPalSubscriptionResult> CreatePayPalSubscriptionAsync(IPRO.Entities.Invoice invoice, BillingRule package, BillingPeriod period, decimal setupFee, string returnUrl, string cancelUrl, decimal taxRate, string? planIdOverride = null, DateTime? startTimeUtc = null)
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
        if (taxRate > 0)
        {
            taxes = new
            {
                percentage = (taxRate * 100).ToString("0.###", CultureInfo.InvariantCulture),
                inclusive = true
            };
            cycleOverrides = await BuildTaxInclusiveCycleOverridesAsync(client, planId, taxRate);
        }

        if (setupFee > 0)
        {
            var setupFeeCharged = taxRate > 0 ? AddTax(setupFee, taxRate) : setupFee;
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

    // M-8: gives a claimed cap slot back after any post-claim failure. No-op for uncapped codes
    // (they are never claimed early) and floored at zero -- a release can never invent capacity.
    private async Task ReleasePromoSlotAsync(PromotionCode? promo)
    {
        if (promo == null || !promo.MaxRedemptions.HasValue) return;
        await _db.PromotionCodes
            .Where(pc => pc.Id == promo.Id && pc.MaxRedemptions != null && pc.RedemptionCount > 0)
            .ExecuteUpdateAsync(su => su.SetProperty(pc => pc.RedemptionCount, pc => pc.RedemptionCount - 1));
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
        // M-8 (2026-08-20): the capped slot is claimed at CHECKOUT CREATION now (see
        // CreateSubscriptionAsync), so activation does NOT increment capped codes again --
        // double-counting would burn a stranger's slot. Uncapped codes keep counting here, where
        // they always did. Checkouts created before this change moved carry an unclaimed slot
        // through activation; that tail undercounts by at most the few in-flight checkouts at
        // deploy time and then disappears.
        var promoRow = await _uow.PromotionCodes.GetByIdAsync(promotionCodeId);
        if (promoRow != null && !promoRow.MaxRedemptions.HasValue)
        {
            await _db.PromotionCodes
                .Where(p => p.Id == promotionCodeId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.RedemptionCount, p => p.RedemptionCount + 1));
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
            DateTime? payPalNextBilling;
            try
            {
                (status, payPalNextBilling) = await GetPayPalSubscriptionSnapshotAsync(billing.PayPalSubscriptionId);
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

            // Wave-2 ISO (audit 2026-08-25): the snapshot guard above only isolated the PayPal
            // read -- an exception in the OUTCOME leg (a poisoned tracker, a column overflow)
            // aborted the whole hourly sweep and every remaining drifted subscription kept full
            // access for another hour, indefinitely while the same row kept failing first. Each
            // row now fails alone, M7-style: clear the tracker, move on, retry next run.
            try
            {

            var endedAtPayPal =
                status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("EXPIRED", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("SUSPENDED", StringComparison.OrdinalIgnoreCase);

            // NextBillingDate drifts from PayPal's engine whenever a locally computed date was
            // wrong -- the worst case being an upgrade's deferred start clobbered to "now + one
            // period", which told the owner "Next billing: September 16" for a charge PayPal had
            // scheduled the following July (2026-08-16 audit). PayPal's billing_info is the truth
            // about PayPal's own schedule, so an ACTIVE row is synced to it here; the tolerance
            // absorbs timezone-of-day noise without letting a real discrepancy sit.
            if (!endedAtPayPal &&
                status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) &&
                payPalNextBilling.HasValue &&
                (!billing.NextBillingDate.HasValue ||
                 Math.Abs((billing.NextBillingDate.Value - payPalNextBilling.Value).TotalHours) > 26))
            {
                _logger.LogWarning(
                    "Reconciliation: billing {BillingId} (agent {AgentUserId}) stored NextBillingDate {Local} but PayPal will charge on {PayPal}; corrected to PayPal's date.",
                    billing.Id, billing.AgentUserId,
                    billing.NextBillingDate?.ToString("yyyy-MM-dd") ?? "(none)",
                    payPalNextBilling.Value.ToString("yyyy-MM-dd"));
                billing.NextBillingDate = payPalNextBilling.Value;
                _uow.Billings.Update(billing);
                corrected++;
            }

            if (!endedAtPayPal) continue;

            // SUSPENDED is a payment problem, not an ending: the webhook door already maps it to
            // Failed (immediate gate, dunning fires, no refund) and this door must agree -- it
            // used to fold suspensions into Cancelled, which under the M5 outcome below would
            // have minted a refund row for a subscription PayPal can still reactivate.
            if (status.Equals("SUSPENDED", StringComparison.OrdinalIgnoreCase))
            {
                billing.Status = BillingStatus.Failed;
                billing.CancelledAt ??= DateTime.UtcNow;
                _uow.Billings.Update(billing);
                corrected++;
                _logger.LogWarning(
                    "Reconciliation: PayPal reports subscription {SubscriptionId} for agent {AgentUserId} is SUSPENDED; local row set to Failed (payment problem, no cancellation outcome).",
                    billing.PayPalSubscriptionId, billing.AgentUserId);
                continue;
            }

            // M5: the reconcile discovery of a lost CANCELLED/EXPIRED delivery is the same event
            // as the webhook arriving -- it owes the agent the same paid-through honor and the
            // same refund row, not just a status flip. The rows in this loop are Active by
            // selection, so the double-refund guard is the selection itself.
            var endedStatus = status.Equals("EXPIRED", StringComparison.OrdinalIgnoreCase)
                ? BillingStatus.Expired
                : BillingStatus.Cancelled;
            // LOW-10 (wave 5): with NO billing date anywhere -- PayPal reports none and the row
            // never stored one -- the computed fallback invented paidThroughEnd = now + 1 period,
            // which the access branch then honored: a data-gap row earned a full YEAR of
            // paid-through from thin air. A gap this deep needs a human; flip raw and log.
            if (!payPalNextBilling.HasValue && !billing.NextBillingDate.HasValue)
            {
                billing.Status = endedStatus;
                billing.CancelledAt ??= DateTime.UtcNow;
                _uow.Billings.Update(billing);
                await _uow.SaveChangesAsync();
                corrected++;
                _logger.LogError(
                    "Reconciliation: subscription {SubscriptionId} (agent {AgentUserId}) ended at PayPal but has NO next-billing date anywhere -- flipped to {Status} with no outcome; review manually for any paid-through owed.",
                    billing.PayPalSubscriptionId, billing.AgentUserId, endedStatus);
                continue;
            }
            var reconciledPaidThroughEnd = payPalNextBilling ?? billing.NextBillingDate!.Value;
            await ApplyCancellationOutcomeAsync(billing, endedStatus, reconciledPaidThroughEnd,
                "Reconcile: PayPal reported the subscription ended; the webhook was lost");
            corrected++;

            _logger.LogWarning(
                "Reconciliation: PayPal reports subscription {SubscriptionId} for agent {AgentUserId} is {Status}, " +
                "but IPRO still had it Active -- a cancellation webhook was almost certainly lost. Local row corrected to {NewStatus}.",
                billing.PayPalSubscriptionId, billing.AgentUserId, status, billing.Status);

            }
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                _logger.LogError(ex,
                    "Reconciliation: applying the outcome for subscription {SubscriptionId} (agent {AgentUserId}) failed; tracker cleared, continuing with the remaining rows.",
                    billing.PayPalSubscriptionId, billing.AgentUserId);
            }
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
                // JOBS-5 (wave 4): each pair fails ALONE, and its convergence commits
                // immediately. The old shape mutated every pair and saved ONCE at the end -- a
                // poisoned save discarded every convergence in the batch and handed stage 4 a
                // dirty tracker (the exact M7 lesson, unapplied to this stage).
                try
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
                    await _uow.SaveChangesAsync();
                    converged++;
                }
                catch (Exception ex)
                {
                    _db.ChangeTracker.Clear();
                    _logger.LogError(ex,
                        "Reconciliation: converging duplicate billing {BillingId} (agent {AgentUserId}) failed; tracker cleared, continuing with the remaining duplicates.",
                        stale.Id, stale.AgentUserId);
                }
            }
        }

        return converged;
    }

    private async Task<string> GetPayPalSubscriptionStatusAsync(string subscriptionId)
    {
        return (await GetPayPalSubscriptionSnapshotAsync(subscriptionId)).Status;
    }

    // Status plus the date PayPal will actually charge next (billing_info.next_billing_time).
    // For a deferred-start subscription that has not begun billing, next_billing_time IS the
    // start_time -- which makes it the one authoritative answer to "when does money move next",
    // something no locally computed date can promise after upgrades/downgrades reshuffle rows.
    private async Task<(string Status, DateTime? NextBillingTime)> GetPayPalSubscriptionSnapshotAsync(string subscriptionId)
    {
        if (!HasPayPalSettings())
        {
            return (string.Empty, null);
        }

        var accessToken = await GetPayPalAccessTokenAsync();
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.GetAsync($"{_settings.BaseUrl}/v1/billing/subscriptions/{subscriptionId}");
        if (!response.IsSuccessStatusCode)
        {
            return (string.Empty, null);
        }

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var status = GetWebhookString(document.RootElement, "status");

        DateTime? nextBillingTime = null;
        if (document.RootElement.TryGetProperty("billing_info", out var billingInfo) &&
            billingInfo.ValueKind == JsonValueKind.Object &&
            billingInfo.TryGetProperty("next_billing_time", out var nextElement) &&
            nextElement.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(nextElement.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            nextBillingTime = parsed;
        }

        return (status, nextBillingTime);
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

    // internal (not private): the proration/classification/date helpers are the exact functions the
    // 418b regression matrix pins down -- the annual->monthly unit-mismatch shipped precisely
    // because no test could reach this math (2026-08-16 audit).
    internal static bool IsUpgrade(BillingRule currentPackage, BillingRule requestedPackage)
    {
        return GetComparableMonthlyPrice(requestedPackage) > GetComparableMonthlyPrice(currentPackage);
    }

    private static decimal GetComparableMonthlyPrice(BillingRule package) =>
        package.MonthlyPrice <= 0 ? decimal.MaxValue : package.MonthlyPrice;

    internal static decimal CalculateRemainingFraction(DateTime now, DateTime startDate, DateTime endDate)
    {
        if (endDate <= startDate || now >= endDate)
        {
            return 0;
        }

        var totalSeconds = (decimal)(endDate - startDate).TotalSeconds;
        var remainingSeconds = (decimal)(endDate - now).TotalSeconds;
        return Math.Clamp(remainingSeconds / totalSeconds, 0, 1);
    }

    internal static decimal GetAmount(BillingRule package, BillingPeriod period) => period switch
    {
        BillingPeriod.Quarterly => package.QuarterlyPrice,
        BillingPeriod.Annually => package.AnnualPrice,
        _ => package.MonthlyPrice
    };

    // The one upgrade-proration function, covering BOTH row shapes (2026-08-16 audits, first and
    // review passes). Internal so the 418b matrix pins it down.
    //
    // NORMAL row -- `now` falls inside the cycle ending at effectiveEnd: both sides are priced in
    // the units of the CYCLE BEING SPLIT (the old period). The original bug priced the charge in
    // the REQUESTED period's units, so a Silver-ANNUAL agent moving to Gold-MONTHLY had ~10.7
    // months of Gold priced as 0.89 of one month and the tier difference was given away. The
    // credit uses what the agent ACTUALLY paid for the running cycle (Billing.Amount); the
    // fallback for legacy zeroed Amounts derives a real cycle price (GetCycleEquivalentAmount),
    // never GetAmount, which returns $0 for exactly the Quarterly-bug rows the fallback exists for.
    //
    // DEFERRED row -- `now` is BEFORE the cycle that ends at effectiveEnd: the row came out of an
    // earlier upgrade whose new subscription starts months away (Period=Monthly but the prepaid
    // stretch runs to the old paid-through date). Cycle math is meaningless here -- the review
    // pass showed CalculateRemainingFraction clamping to 1 and selling ~11 months of the next
    // tier for one month's difference. Instead, price the ACTUAL remaining stretch day by day at
    // both tiers' monthly list rates; the in-kind compensation these rows carry is already valued
    // at list, so list (not historical Amount, which describes one month) is the right basis.
    internal static (decimal Credit, decimal Charge) CalculateUpgradeProration(
        BillingRule currentPackage, BillingRule requestedPackage, BillingPeriod currentPeriod,
        decimal amountPaidForCycle, DateTime now, DateTime effectiveEnd)
    {
        var cycleStart = GetCurrentCycleStart(effectiveEnd, currentPeriod);
        if (now >= cycleStart)
        {
            var fraction = CalculateRemainingFraction(now, cycleStart, effectiveEnd);
            var paid = amountPaidForCycle > 0
                ? amountPaidForCycle
                : GetCycleEquivalentAmount(currentPackage, currentPeriod);
            return (Math.Round(paid * fraction, 2),
                    Math.Round(GetCycleEquivalentAmount(requestedPackage, currentPeriod) * fraction, 2));
        }

        var remainingDays = (decimal)(effectiveEnd - now).TotalDays;
        if (remainingDays <= 0)
        {
            return (0m, 0m);
        }

        const decimal daysPerMonth = 30.4375m; // 365.25 / 12, the same convention PayPal bills on
        return (Math.Round(currentPackage.MonthlyPrice * remainingDays / daysPerMonth, 2),
                Math.Round(requestedPackage.MonthlyPrice * remainingDays / daysPerMonth, 2));
    }

    // The package's price for one cycle of the given length, deriving from the monthly price when
    // no direct price exists for that period. Used by upgrade proration, where the remainder of the
    // OLD cycle must be priced at the NEW tier in the old cycle's units -- a package with no annual
    // price must still cost 12 months' worth for a year, never 0. Where a real annual price exists
    // it is used as-is, so an annual-cycle remainder keeps the annual discount the agent originally
    // committed to.
    internal static decimal GetCycleEquivalentAmount(BillingRule package, BillingPeriod cyclePeriod)
    {
        var direct = GetAmount(package, cyclePeriod);
        if (direct > 0) return direct;
        return cyclePeriod switch
        {
            BillingPeriod.Annually => package.MonthlyPrice * 12,
            BillingPeriod.Quarterly => package.MonthlyPrice * 3,
            _ => package.MonthlyPrice
        };
    }

    // A period may be sold only when the package carries BOTH a positive price and a PayPal plan to
    // charge it on. Public so the pricing/registration screens can hide what cannot be bought, and
    // enforced server-side in CreateSubscriptionAsync so hiding a radio is never the only defence.
    public static bool IsPeriodOfferable(BillingRule package, BillingPeriod period) =>
        GetAmount(package, period) > 0 && !string.IsNullOrWhiteSpace(GetPayPalPlanId(package, period));

    // ADMIN-2 / BILLING-9: true when the package's editable price no longer matches the price its
    // PayPal plan was created with (the snapshot SyncPayPalPlansAsync records). A plan's price is
    // frozen at creation, so in this state checkout would CHARGE the frozen price while the invoice
    // SHOWS the edited one — CreateSubscriptionAsync refuses rather than letting them disagree.
    // A null snapshot means the plan predates the snapshot columns: divergence unknown, allowed,
    // and the Packages screen's banner keeps nagging for a re-sync. Internal so the guard tests can
    // pin it the way BillingPeriodGuardTests pins IsPeriodOfferable.
    internal static bool HasDivergentPlanPrice(BillingRule package, BillingPeriod period)
    {
        var recorded = period == BillingPeriod.Annually
            ? package.PayPalAnnualPlanPrice
            : package.PayPalMonthlyPlanPrice;
        return recorded.HasValue && recorded.Value != GetAmount(package, period);
    }

    private static string GetPayPalPlanId(BillingRule package, BillingPeriod period) => period switch
    {
        BillingPeriod.Annually => package.PayPalAnnualPlanId?.Trim() ?? string.Empty,
        _ => package.PayPalMonthlyPlanId?.Trim() ?? string.Empty
    };

    internal static DateTime GetNextBillingDate(DateTime startDate, BillingPeriod period) => period switch
    {
        BillingPeriod.Quarterly => startDate.AddMonths(3),
        BillingPeriod.Annually => startDate.AddYears(1),
        _ => startDate.AddMonths(1)
    };

    // What NextBillingDate to store when a subscription activates or a recurring sale settles.
    // Keep the stored date ONLY when it is beyond one normal period from now -- that is the
    // signature of a deferred-start upgrade (first charge at the old paid-through date, months
    // away), which "now + one period" corrupted into a date PayPal never bills on (the Sep-16
    // vs-July banner, 2026-08-16 audit). Anything within one period -- fresh signups, renewals,
    // monthly upgrades deferred by mere weeks -- recomputes exactly as before; the hourly
    // reconcile trues up any residual days-level drift from PayPal's own billing_info. Applied
    // at BOTH write sites (activation and the sale webhook): the review pass showed fixing only
    // one lets the other re-corrupt the date within minutes, in either webhook ordering.
    internal static DateTime ResolveNextBillingDateOnPayment(DateTime? stored, DateTime now, BillingPeriod period)
    {
        var recomputed = GetNextBillingDate(now, period);
        return stored.HasValue && stored.Value > recomputed ? stored.Value : recomputed;
    }

    // The start of the cycle the agent is currently paid through, derived by winding the NEXT
    // billing date back one period. Proration must never measure from Billing.StartDate: that is
    // written once at activation and never advanced on renewal, so after the first renewal the
    // denominator becomes the whole lifetime of the subscription instead of one cycle. A Silver
    // agent who renewed once and upgraded the next day was charged for ~48% of the difference
    // instead of ~97%; after twelve renewals the same upgrade cost about a thirteenth of its price.
    // The QA runs never caught it because every upgrade resets StartDate -- only a
    // renewed-but-not-yet-upgraded subscription shows it (2026-08-14 ultra-audit).
    internal static DateTime GetCurrentCycleStart(DateTime nextBillingDate, BillingPeriod period) => period switch
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
