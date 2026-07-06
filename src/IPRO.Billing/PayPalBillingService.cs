using IPRO.DataAccess.Repositories;
using IPRO.Entities;

namespace IPRO.Billing;

public class PayPalBillingService : IBillingService
{
    private readonly IUnitOfWork _uow;

    public PayPalBillingService(IUnitOfWork uow)
    {
        _uow = uow;
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

    public async Task<bool> CreateSubscriptionAsync(int userId, int billingRuleId, BillingPeriod period)
    {
        var requestedPackage = await _uow.BillingRules.FirstOrDefaultAsync(p => p.Id == billingRuleId && p.IsActive);
        if (requestedPackage == null)
        {
            return false;
        }

        var activeSubscription = await GetActiveSubscriptionAsync(userId);
        if (activeSubscription == null)
        {
            await CancelPendingChangesAsync(userId);
            await CreateInitialSubscriptionAsync(userId, requestedPackage, period);
            return true;
        }

        if (activeSubscription.BillingRuleId == requestedPackage.Id)
        {
            await CancelPendingChangesAsync(userId);
            return true;
        }

        var currentPackage = await _uow.BillingRules.GetByIdAsync(activeSubscription.BillingRuleId);
        if (currentPackage == null)
        {
            return false;
        }

        if (IsUpgrade(currentPackage, requestedPackage))
        {
            await CancelPendingChangesAsync(userId);
            await ApplyUpgradeAsync(userId, activeSubscription, currentPackage, requestedPackage, period);
            return true;
        }

        await ScheduleDowngradeAsync(userId, activeSubscription, currentPackage, requestedPackage, period);
        return true;
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

    public async Task<bool> HandleWebhookAsync(string eventType, string payload, string signature, decimal amount)
    {
        await Task.CompletedTask;
        return true;
    }

    public async Task<List<IPRO.Entities.Invoice>> GetInvoicesAsync(int userId)
    {
        var invoices = await _uow.Invoices.FindAsync(i => i.AgentUserId == userId);
        return invoices.OrderByDescending(i => i.IssuedAt).ToList();
    }

    public async Task<IPRO.Entities.Invoice> GenerateInvoiceAsync(int userId, decimal amount, string description)
    {
        var activeSubscription = await GetActiveSubscriptionAsync(userId);
        if (activeSubscription == null)
        {
            throw new InvalidOperationException("Cannot generate an invoice without an active subscription.");
        }

        return await CreateInvoiceAsync(activeSubscription.Id, userId, amount, false);
    }

    public async Task<List<BillingRule>> GetPackagesAsync()
    {
        var packages = await _uow.BillingRules.FindAsync(p => p.IsActive);
        return packages.OrderBy(p => p.MonthlyPrice <= 0 ? decimal.MaxValue : p.MonthlyPrice).ToList();
    }

    private async Task CreateInitialSubscriptionAsync(int userId, BillingRule package, BillingPeriod period)
    {
        var amount = GetAmount(package, period);
        var now = DateTime.UtcNow;
        var billing = new IPRO.Entities.Billing
        {
            AgentUserId = userId,
            BillingRuleId = package.Id,
            Amount = amount,
            Currency = "CAD",
            Status = BillingStatus.Active,
            Period = period,
            StartDate = now,
            NextBillingDate = GetNextBillingDate(now, period),
            CreatedAt = now
        };

        await _uow.Billings.AddAsync(billing);
        await _uow.SaveChangesAsync();

        await _uow.SubscriptionChanges.AddAsync(new SubscriptionChange
        {
            AgentUserId = userId,
            RequestedBillingRuleId = package.Id,
            BillingId = billing.Id,
            ChangeType = SubscriptionChangeType.Subscribe,
            Status = SubscriptionChangeStatus.Applied,
            Period = period,
            EffectiveDate = now,
            ProratedCharge = amount,
            AmountDue = amount,
            AppliedAt = now
        });
        await _uow.SaveChangesAsync();

        await CreateInvoiceAsync(billing.Id, userId, amount, true);
    }

    private async Task ApplyUpgradeAsync(int userId, IPRO.Entities.Billing currentSubscription, BillingRule currentPackage, BillingRule requestedPackage, BillingPeriod period)
    {
        var now = DateTime.UtcNow;
        var effectiveEnd = currentSubscription.NextBillingDate ?? GetNextBillingDate(now, currentSubscription.Period);
        var remainingFraction = CalculateRemainingFraction(now, currentSubscription.StartDate, effectiveEnd);
        var credit = Math.Round(GetAmount(currentPackage, currentSubscription.Period) * remainingFraction, 2);
        var charge = Math.Round(GetAmount(requestedPackage, period) * remainingFraction, 2);
        var amountDue = Math.Max(0, charge - credit);

        currentSubscription.Status = BillingStatus.Cancelled;
        currentSubscription.CancelledAt = now;
        _uow.Billings.Update(currentSubscription);

        var upgradedBilling = new IPRO.Entities.Billing
        {
            AgentUserId = userId,
            BillingRuleId = requestedPackage.Id,
            Amount = GetAmount(requestedPackage, period),
            Currency = "CAD",
            Status = BillingStatus.Active,
            Period = period,
            StartDate = now,
            NextBillingDate = effectiveEnd > now ? effectiveEnd : GetNextBillingDate(now, period),
            CreatedAt = now
        };

        await _uow.Billings.AddAsync(upgradedBilling);
        await _uow.SaveChangesAsync();

        await _uow.SubscriptionChanges.AddAsync(new SubscriptionChange
        {
            AgentUserId = userId,
            CurrentBillingRuleId = currentPackage.Id,
            RequestedBillingRuleId = requestedPackage.Id,
            BillingId = upgradedBilling.Id,
            ChangeType = SubscriptionChangeType.Upgrade,
            Status = SubscriptionChangeStatus.Applied,
            Period = period,
            EffectiveDate = now,
            ProratedCredit = credit,
            ProratedCharge = charge,
            AmountDue = amountDue,
            AppliedAt = now
        });
        await _uow.SaveChangesAsync();

        await CreateInvoiceAsync(upgradedBilling.Id, userId, amountDue, true);
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

        await _uow.SaveChangesAsync();
    }

    private async Task ApplyDuePendingChangesAsync(int userId)
    {
        var now = DateTime.UtcNow;
        var dueChanges = await _uow.SubscriptionChanges.FindAsync(c =>
            c.AgentUserId == userId &&
            c.Status == SubscriptionChangeStatus.Pending &&
            c.EffectiveDate <= now);

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

            await CreateInvoiceAsync(billing.Id, userId, amount, true);
        }
    }

    private async Task<IPRO.Entities.Invoice> CreateInvoiceAsync(int billingId, int userId, decimal amount, bool isPaid)
    {
        var invoice = new IPRO.Entities.Invoice
        {
            BillingId = billingId,
            AgentUserId = userId,
            InvoiceNumber = $"IPRO-{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{userId}",
            SubTotal = amount,
            TaxAmount = 0,
            Total = amount,
            Currency = "CAD",
            IssuedAt = DateTime.UtcNow,
            IsPaid = isPaid
        };

        await _uow.Invoices.AddAsync(invoice);
        await _uow.SaveChangesAsync();
        return invoice;
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
}
