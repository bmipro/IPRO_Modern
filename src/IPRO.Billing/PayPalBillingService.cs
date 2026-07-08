using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.Extensions.Options;

namespace IPRO.Billing;

public class PayPalBillingService : IBillingService
{
    private readonly IUnitOfWork _uow;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEmailService _email;
    private readonly PayPalSettings _settings;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PayPalBillingService(IUnitOfWork uow, IHttpClientFactory httpClientFactory, IEmailService email, IOptions<PayPalSettings> settings)
    {
        _uow = uow;
        _httpClientFactory = httpClientFactory;
        _email = email;
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
        var invoices = await GetInvoicesAsync(userId);
        var failedInvoice = invoices.FirstOrDefault(i => !i.IsPaid && i.Billing?.Status == BillingStatus.Failed);
        if (failedInvoice != null)
        {
            return await BuildBillingIssueAsync(failedInvoice, "Payment failed", "Your last payment could not be completed. Please update or retry your payment to keep your IPRO services active.");
        }

        var pendingInvoice = invoices.FirstOrDefault(i => !i.IsPaid && i.Billing?.Status == BillingStatus.Pending && i.IssuedAt <= DateTime.UtcNow.AddHours(-24));
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
            return await BeginPaidChangeAsync(
                userId,
                null,
                requestedPackage,
                period,
                SubscriptionChangeType.Subscribe,
                DateTime.UtcNow,
                0,
                GetAmount(requestedPackage, period),
                GetAmount(requestedPackage, period),
                returnUrl,
                cancelUrl,
                includeSetupFee: true);
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
            return BillingChangeResult.Failed("Missing PayPal order id.");
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
        return new BillingChangeResult
        {
            Success = true,
            Message = "Payment confirmed and your package is active."
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
            var invoice = (await _uow.Invoices.FindAsync(i =>
                    i.BillingId == billing.Id && !i.IsPaid))
                .OrderByDescending(i => i.IssuedAt)
                .FirstOrDefault();
            if (invoice == null)
            {
                continue;
            }

            var alreadyLogged = await _uow.OperateLogs.FirstOrDefaultAsync(l =>
                l.AgentUserId == billing.AgentUserId &&
                l.Module == "Billing" &&
                l.Action == "BillingIssueEmail" &&
                l.Description == $"Billing:{billing.Id}:Invoice:{invoice.Id}");
            if (alreadyLogged != null)
            {
                continue;
            }

            var agent = await _uow.AgentUsers.GetByIdAsync(billing.AgentUserId);
            if (agent == null || string.IsNullOrWhiteSpace(agent.Email))
            {
                continue;
            }

            var package = await _uow.BillingRules.GetByIdAsync(billing.BillingRuleId);
            var fullName = $"{agent.FirstName} {agent.LastName}".Trim();
            var amount = $"{invoice.Total:N2} {invoice.Currency}";
            var packageName = package?.PackageName ?? "your IPRO package";
            var subject = billing.Status == BillingStatus.Failed
                ? "Action required: IPRO payment failed"
                : "Reminder: IPRO payment pending";
            var html = BuildBillingIssueEmailHtml(fullName, packageName, amount, billing.Status);
            var text = $"Hello {fullName},\n\nWe need your attention on the payment for {packageName}. Amount: {amount}. Please sign in to your IPRO Agent Portal and go to Billing to correct the issue.\n\nIPRO Management";

            if (await _email.SendAsync(agent.Email, fullName, subject, html, text))
            {
                await _uow.OperateLogs.AddAsync(new OperateLog
                {
                    AgentUserId = billing.AgentUserId,
                    Module = "Billing",
                    Action = "BillingIssueEmail",
                    Description = $"Billing:{billing.Id}:Invoice:{invoice.Id}",
                    CreatedAt = DateTime.UtcNow
                });
                await _uow.SaveChangesAsync();
                sent++;
            }
        }

        return sent;
    }

    public async Task<bool> HandleWebhookAsync(string eventType, string payload, string signature, decimal amount)
    {
        await Task.CompletedTask;
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
        bool includeSetupFee = false)
    {
        if (!HasPayPalSettings())
        {
            return BillingChangeResult.Failed("PayPal is not configured yet. Please add PayPal ClientId and ClientSecret in Azure app settings.");
        }

        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = userId,
            BillingRuleId = requestedPackage.Id,
            Amount = GetAmount(requestedPackage, period),
            Currency = "CAD",
            Status = BillingStatus.Pending,
            Period = period,
            StartDate = effectiveDate,
            NextBillingDate = nextBillingDate ?? GetNextBillingDate(effectiveDate, period),
            CreatedAt = DateTime.UtcNow
        };

        await _uow.Billings.AddAsync(billing);
        await _uow.SaveChangesAsync();

        var setupFee = includeSetupFee ? requestedPackage.SetupFee : 0;
        var invoice = await CreateInvoiceAsync(billing.Id, userId, requestedPackage, period, amountDue, setupFee, false);

        await _uow.SubscriptionChanges.AddAsync(new SubscriptionChange
        {
            AgentUserId = userId,
            CurrentBillingRuleId = currentPackage?.Id,
            RequestedBillingRuleId = requestedPackage.Id,
            BillingId = billing.Id,
            ChangeType = changeType,
            Status = SubscriptionChangeStatus.Pending,
            Period = period,
            EffectiveDate = effectiveDate,
            ProratedCredit = credit,
            ProratedCharge = charge,
            AmountDue = invoice.Total
        });
        await _uow.SaveChangesAsync();

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
        var invoice = new IPRO.Entities.Invoice
        {
            BillingId = billingId,
            AgentUserId = userId,
            InvoiceNumber = $"IPRO-{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{userId}",
            SubTotal = subtotal,
            TaxAmount = taxAmount,
            TaxRate = taxRate,
            TaxRegion = taxRegion,
            Total = subtotal + taxAmount,
            Currency = "CAD",
            IssuedAt = DateTime.UtcNow,
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

    private static DateTime GetNextBillingDate(DateTime startDate, BillingPeriod period) => period switch
    {
        BillingPeriod.Quarterly => startDate.AddMonths(3),
        BillingPeriod.Annually => startDate.AddYears(1),
        _ => startDate.AddMonths(1)
    };

    private sealed record InvoiceLineDraft(string Description, decimal Amount);
    private sealed record TaxCalculation(decimal Rate, decimal Amount, string Region);
    private sealed record PayPalOrderResult(string OrderId, string ApprovalUrl);
}
