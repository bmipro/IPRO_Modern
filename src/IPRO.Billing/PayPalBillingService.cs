using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IPRO.Billing;

public class PayPalBillingService : IBillingService
{
    public async Task<BillingService?> GetActiveSubscriptionAsync(int userId) // note the ?
    {
        await Task.CompletedTask;
        return null; // or return new BillingService{}
    }

    public async Task<bool> CreateSubscriptionAsync(int userId, int planId, BillingPeriod period)
    {
        await Task.CompletedTask;
        return true;
    }

    public async Task<bool> CancelSubscriptionAsync(int userId)
    {
        await Task.CompletedTask;
        return true;
    }

    public async Task<bool> HandleWebhookAsync(string eventType, string payload, string signature, decimal amount)
    {
        await Task.CompletedTask;
        return true;
    }

    public async Task<List<Invoice>> GetInvoicesAsync(int userId)
    {
        await Task.CompletedTask;
        return new List<Invoice>();
    }

    public async Task<Invoice> GenerateInvoiceAsync(int userId, decimal amount, string description)
    {
        await Task.CompletedTask;
        return new Invoice { Amount = amount, Description = description };
    }

    public async Task<List<BillingPackage>> GetPackagesAsync()
    {
        await Task.CompletedTask;
        return new List<BillingPackage>();
    }
}