using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace IPRO.Billing;

public class PayPalBillingService : IBillingService
{
    private readonly IUnitOfWork _uow;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly PayPalSettings _settings;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PayPalBillingService(IUnitOfWork uow, IHttpClientFactory httpClientFactory, IEmailService email, IOptions<PayPalSettings> settings, IConfiguration configuration)
    {
        _uow = uow;
        _httpClientFactory = httpClientFactory;
        _email = email;
        _configuration = configuration;
        _settings = settings.Value;
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

        var activeSubscription = await GetActiveSubscriptionAsync(userId);
        if (activeSubscription == null)
        {
            await CancelPendingChangesAsync(userId);

            var agent = await _uow.AgentUsers.GetByIdAsync(userId);
            var promo = await ValidatePromotionCodeAsync(agent?.PromotionCode, requestedPackage.Id);

            decimal? overrideAmount = null;
            string? overridePlanId = null;
            decimal? overrideSetupFee = null;

            if (promo != null)
            {
                if (promo.SetupFeeDiscountType != PromoDiscountType.None)
                {
                    overrideSetupFee = ComputeDiscountedAmount(requestedPackage.SetupFee, promo.SetupFeeDiscountType, promo.SetupFeeDiscountValue);
                }

                if (promo.RecurringDiscountType != PromoDiscountType.None)
                {
                    overrideAmount = ComputeDiscountedAmount(GetAmount(requestedPackage, period), promo.RecurringDiscountType, promo.RecurringDiscountValue);

                    var effectiveSetupFee = overrideSetupFee ?? requestedPackage.SetupFee;
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
            var remainingFraction = CalculateRemainingFraction(now, activeSubscription.StartDate, effectiveEnd);
            var credit = Math.Round(GetAmount(currentPackage, activeSubscription.Period) * remainingFraction, 2);
            var charge = Math.Round(GetAmount(requestedPackage, period) * remainingFraction, 2);
            var amountDue = Math.Max(0, charge - credit);

            return amountDue <= 0
                ? await ApplyUpgradeWithoutPaymentAsync(userId, activeSubscription, currentPackage, requestedPackage, period, credit, charge)
                : await BeginPaidChangeAsync(userId, currentPackage, requestedPackage, period, SubscriptionChangeType.Upgrade, now, credit, charge, amountDue, returnUrl, cancelUrl, activeSubscription.Id, effectiveEnd);
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
            b.AgentUserId == userId && b.Status == BillingStatus.Active);
        foreach (var subscription in activeSubscriptions)
        {
            subscription.Status = BillingStatus.Cancelled;
            subscription.CancelledAt = DateTime.UtcNow;
            _uow.Billings.Update(subscription);
        }

        billing.Status = BillingStatus.Active;
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

    private async Task<BillingChangeResult> ActivateSubscriptionBillingAsync(int userId, IPRO.Entities.Billing billing, IPRO.Entities.Invoice? invoice, string message)
    {
        var now = DateTime.UtcNow;
        if (invoice == null)
        {
            invoice = (await _uow.Invoices.FindAsync(i => i.BillingId == billing.Id && !i.IsPaid))
                .OrderByDescending(i => i.IssuedAt)
                .FirstOrDefault();
        }

        if (invoice != null)
        {
            invoice.IsPaid = true;
            _uow.Invoices.Update(invoice);
        }

        var activeSubscriptions = await _uow.Billings.FindAsync(b =>
            b.AgentUserId == userId && b.Status == BillingStatus.Active && b.Id != billing.Id);
        foreach (var subscription in activeSubscriptions)
        {
            if (!string.IsNullOrWhiteSpace(subscription.PayPalSubscriptionId))
            {
                await CancelPayPalSubscriptionAsync(subscription.PayPalSubscriptionId, "Replaced by a new IPRO subscription.");
            }

            subscription.Status = BillingStatus.Cancelled;
            subscription.CancelledAt = now;
            _uow.Billings.Update(subscription);
        }

        billing.Status = BillingStatus.Active;
        billing.StartDate = now;
        billing.NextBillingDate = GetNextBillingDate(now, billing.Period);
        _uow.Billings.Update(billing);

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

        PayPalOrderResult order;
        try
        {
            order = await CreatePayPalOrderAsync(invoice, package.PackageName, returnUrl, cancelUrl);
        }
        catch (Exception ex)
        {
            return BillingChangeResult.Failed($"PayPal checkout could not be restarted: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(order.ApprovalUrl))
        {
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
            await CancelPayPalSubscriptionAsync(subscription.PayPalSubscriptionId, "Agent cancelled subscription from IPRO billing.");
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

    private async Task<bool> HandleSubscriptionPaymentFailedWebhookAsync(string subscriptionId, string transactionId)
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

        var package = await _uow.BillingRules.GetByIdAsync(billing.BillingRuleId);
        var invoice = package == null
            ? await CreateInvoiceAsync(billing.Id, billing.AgentUserId, billing.Amount, false)
            : await CreateInvoiceAsync(billing.Id, billing.AgentUserId, package, billing.Period, billing.Amount, 0, false);
        invoice.PayPalTransactionId = $"PAYPAL_FAILED:{transactionId}";
        _uow.Invoices.Update(invoice);
        await _uow.SaveChangesAsync();
        return true;
    }

    private async Task<bool> HandleSubscriptionPaymentCompletedWebhookAsync(string subscriptionId, string transactionId, decimal amount)
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

        var pendingInvoice = (await _uow.Invoices.FindAsync(i => i.BillingId == billing.Id && !i.IsPaid))
            .OrderBy(i => i.IssuedAt)
            .FirstOrDefault();
        if (pendingInvoice != null)
        {
            pendingInvoice.IsPaid = true;
            pendingInvoice.PayPalTransactionId = transactionId;
            _uow.Invoices.Update(pendingInvoice);
        }
        else
        {
            var package = await _uow.BillingRules.GetByIdAsync(billing.BillingRuleId);
            var recurringAmount = amount > 0 ? amount : billing.Amount;
            var invoice = package == null
                ? await CreateInvoiceAsync(billing.Id, billing.AgentUserId, recurringAmount, true)
                : await CreateInvoiceAsync(billing.Id, billing.AgentUserId, package, billing.Period, recurringAmount, 0, true);
            invoice.PayPalTransactionId = transactionId;
            _uow.Invoices.Update(invoice);
            pendingInvoice = invoice;
        }

        if (billing.Status != BillingStatus.Active)
        {
            billing.Status = BillingStatus.Active;
            billing.StartDate = DateTime.UtcNow;
        }

        billing.NextBillingDate = GetNextBillingDate(DateTime.UtcNow, billing.Period);
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
        var packages = await _uow.BillingRules.FindAsync(p => p.IsActive);
        return packages.OrderBy(p => p.MonthlyPrice <= 0 ? decimal.MaxValue : p.MonthlyPrice).ToList();
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

        var setupFee = includeSetupFee ? (overrideSetupFee ?? requestedPackage.SetupFee) : 0;
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

        if (changeType == SubscriptionChangeType.Subscribe && promotionCodeId.HasValue && billing.Amount <= 0 && setupFee <= 0)
        {
            // Fully comped by a permanent promo code (recurring price and setup fee both discounted to $0) -
            // PayPal's Subscriptions API has no way to represent a free-forever recurring plan, so there is
            // nothing to check out; activate the package directly using the same activation path a real
            // PayPal payment confirmation would take.
            return await ActivateSubscriptionBillingAsync(userId, billing, invoice, "Your promotion code covers this package at no cost - your account is active now.");
        }

        if (changeType == SubscriptionChangeType.Subscribe && !string.IsNullOrWhiteSpace(billing.PayPalPlanId))
        {
            PayPalSubscriptionResult subscription;
            try
            {
                subscription = await CreatePayPalSubscriptionAsync(invoice, requestedPackage, period, setupFee, returnUrl, cancelUrl, billing.PayPalPlanId);
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

    private async Task<BillingChangeResult> ApplyUpgradeWithoutPaymentAsync(int userId, IPRO.Entities.Billing activeSubscription, BillingRule currentPackage, BillingRule requestedPackage, BillingPeriod period, decimal credit, decimal charge)
    {
        var now = DateTime.UtcNow;
        activeSubscription.Status = BillingStatus.Cancelled;
        activeSubscription.CancelledAt = now;
        _uow.Billings.Update(activeSubscription);

        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = userId,
            BillingRuleId = requestedPackage.Id,
            Amount = GetAmount(requestedPackage, period),
            Currency = "CAD",
            Status = BillingStatus.Active,
            Period = period,
            StartDate = now,
            NextBillingDate = activeSubscription.NextBillingDate ?? GetNextBillingDate(now, period),
            CreatedAt = now
        };

        await _uow.Billings.AddAsync(billing);
        await _uow.SaveChangesAsync();

        await _uow.SubscriptionChanges.AddAsync(new SubscriptionChange
        {
            AgentUserId = userId,
            CurrentBillingRuleId = currentPackage.Id,
            RequestedBillingRuleId = requestedPackage.Id,
            BillingId = billing.Id,
            ChangeType = SubscriptionChangeType.Upgrade,
            Status = SubscriptionChangeStatus.Applied,
            Period = period,
            EffectiveDate = now,
            ProratedCredit = credit,
            ProratedCharge = charge,
            AmountDue = 0,
            AppliedAt = now
        });
        await _uow.SaveChangesAsync();
        await CreateInvoiceAsync(billing.Id, userId, requestedPackage, period, 0, 0, true);

        return new BillingChangeResult { Success = true, Message = "Your package was upgraded." };
    }

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
            foreach (var subscription in activeSubscriptions)
            {
                subscription.Status = BillingStatus.Cancelled;
                subscription.CancelledAt = now;
                _uow.Billings.Update(subscription);
            }

            var amount = GetAmount(requestedPackage, change.Period);
            var billing = new IPRO.Entities.Billing
            {
                AgentUserId = userId,
                BillingRuleId = requestedPackage.Id,
                Amount = amount,
                Currency = change.Currency,
                Status = BillingStatus.Active,
                Period = change.Period,
                StartDate = now,
                NextBillingDate = GetNextBillingDate(now, change.Period),
                CreatedAt = now
            };

            await _uow.Billings.AddAsync(billing);
            await _uow.SaveChangesAsync();

            change.BillingId = billing.Id;
            change.Status = SubscriptionChangeStatus.Applied;
            change.AppliedAt = now;
            _uow.SubscriptionChanges.Update(change);
            await _uow.SaveChangesAsync();

            await CreateInvoiceAsync(billing.Id, userId, requestedPackage, change.Period, amount, 0, true);
            applied++;
        }

        return applied;
    }

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
        var invoice = new IPRO.Entities.Invoice
        {
            BillingId = billingId,
            AgentUserId = userId,
            InvoiceNumber = await GenerateInvoiceNumberAsync(issuedAt),
            SubTotal = subtotal,
            TaxAmount = taxAmount,
            TaxRate = taxRate,
            TaxRegion = taxRegion,
            Total = subtotal + taxAmount,
            Currency = "CAD",
            IssuedAt = issuedAt,
            IsPaid = isPaid
        };

        await _uow.Invoices.AddAsync(invoice);
        await _uow.SaveChangesAsync();

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
                  <div><strong>Date:</strong> {invoice.IssuedAt:MMMM d, yyyy}</div>
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
              <div style="margin-left:auto;margin-top:20px;max-width:320px;">
                <div style="display:flex;justify-content:space-between;border-bottom:1px solid #e5edf7;padding:8px 0;"><span>Subtotal</span><strong>${invoice.SubTotal:N2} {invoice.Currency}</strong></div>
                <div style="display:flex;justify-content:space-between;border-bottom:1px solid #e5edf7;padding:8px 0;"><span>Tax {WebUtility.HtmlEncode(invoice.TaxRegion)}</span><strong>${invoice.TaxAmount:N2} {invoice.Currency}</strong></div>
                <div style="display:flex;justify-content:space-between;padding:12px 0;color:#1457d9;font-size:20px;"><strong>Total</strong><strong>${invoice.Total:N2} {invoice.Currency}</strong></div>
              </div>
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

    private async Task<PayPalSubscriptionResult> CreatePayPalSubscriptionAsync(IPRO.Entities.Invoice invoice, BillingRule package, BillingPeriod period, decimal setupFee, string returnUrl, string cancelUrl, string? planIdOverride = null)
    {
        var planId = planIdOverride ?? GetPayPalPlanId(package, period);
        if (string.IsNullOrWhiteSpace(planId))
        {
            throw new InvalidOperationException("This package does not have a PayPal plan ID for the selected billing period.");
        }

        var accessToken = await GetPayPalAccessTokenAsync();
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var paymentPreferences = new Dictionary<string, object>();
        if (setupFee > 0)
        {
            paymentPreferences["setup_fee"] = new
            {
                currency_code = invoice.Currency,
                value = setupFee.ToString("0.00")
            };
        }

        if (invoice.TaxRate > 0)
        {
            paymentPreferences["taxes"] = new
            {
                percentage = (invoice.TaxRate * 100).ToString("0.###"),
                inclusive = false
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

        if (paymentPreferences.Count > 0)
        {
            payload["plan"] = new
            {
                payment_preferences = paymentPreferences
            };
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

    private async Task<string> CreatePayPalPlanAsync(string productId, BillingRule package, BillingPeriod period)
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

        var intervalUnit = period == BillingPeriod.Annually ? "YEAR" : "MONTH";
        var periodName = period == BillingPeriod.Annually ? "Annual" : "Monthly";
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

    public async Task<PromotionCode?> ValidatePromotionCodeAsync(string? code, int billingRuleId)
    {
        code = code?.Trim();
        if (string.IsNullOrWhiteSpace(code)) return null;

        var promo = await _uow.PromotionCodes.FirstOrDefaultAsync(p => p.Code.ToLower() == code.ToLower());
        if (promo == null || !promo.IsActive) return null;
        if (promo.ExpiresAt.HasValue && promo.ExpiresAt.Value < DateTime.UtcNow) return null;
        if (promo.MaxRedemptions.HasValue && promo.RedemptionCount >= promo.MaxRedemptions.Value) return null;
        if (promo.RecurringDiscountType != PromoDiscountType.None && promo.RestrictedBillingRuleId != billingRuleId) return null;

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

        promo.RedemptionCount++;
        _uow.PromotionCodes.Update(promo);

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

    private async Task CancelPayPalSubscriptionAsync(string subscriptionId, string reason)
    {
        if (!HasPayPalSettings() || string.IsNullOrWhiteSpace(subscriptionId))
        {
            return;
        }

        try
        {
            var accessToken = await GetPayPalAccessTokenAsync();
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var payload = new { reason };
            await client.PostAsync(
                $"{_settings.BaseUrl}/v1/billing/subscriptions/{subscriptionId}/cancel",
                new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"));
        }
        catch
        {
            // Local cancellation should still proceed if PayPal is temporarily unavailable.
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

    private static bool IsPayPalSubscriptionApproved(string status) =>
        status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("APPROVAL_PENDING", StringComparison.OrdinalIgnoreCase);

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
