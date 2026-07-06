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
        return await _uow.Billings.FirstOrDefaultAsync(b =>
            b.AgentUserId == userId && b.Status == BillingStatus.Active);
    }

    public async Task<bool> CreateSubscriptionAsync(int userId, int billingRuleId, BillingPeriod period)
    {
        var package = await _uow.BillingRules.FirstOrDefaultAsync(p => p.Id == billingRuleId && p.IsActive);
        if (package == null)
        {
            return false;
        }

        var activeSubscriptions = await _uow.Billings.FindAsync(b =>
            b.AgentUserId == userId && b.Status == BillingStatus.Active);
        foreach (var subscription in activeSubscriptions)
        {
            subscription.Status = BillingStatus.Cancelled;
            subscription.CancelledAt = DateTime.UtcNow;
            _uow.Billings.Update(subscription);
        }

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

        await CreateInvoiceAsync(billing.Id, userId, amount, true);
        return true;
    }

    public async Task<bool> CancelSubscriptionAsync(int userId)
    {
        var subscription = await GetActiveSubscriptionAsync(userId);
        if (subscription == null)
        {
            return false;
        }

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
        return packages.OrderBy(p => p.MonthlyPrice).ToList();
    }

    private async Task<IPRO.Entities.Invoice> CreateInvoiceAsync(int billingId, int userId, decimal amount, bool isPaid)
    {
        var invoice = new IPRO.Entities.Invoice
        {
            BillingId = billingId,
            AgentUserId = userId,
            InvoiceNumber = $"IPRO-{DateTime.UtcNow:yyyyMMddHHmmss}-{userId}",
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
