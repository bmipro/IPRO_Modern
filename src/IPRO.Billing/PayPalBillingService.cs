using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.Extensions.Options;

namespace IPRO.Billing;

public class PayPalBillingService : IBillingService
{
    private readonly IUnitOfWork _uow;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PayPalSettings _settings;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PayPalBillingService(IUnitOfWork uow, IHttpClientFactory httpClientFactory, IOptions<PayPalSettings> settings)
    {
        _uow = uow;
        _httpClientFactory = httpClientFactory;
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
                cancelUrl);
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

        var captured = await CapturePayPalOrderAsync(orderId);
        if (!captured)
        {
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
        if (billing == null || billing.AgentUserId != userId || billing.Status != BillingStatus.Pending)
        {
            return false;
        }

        billing.Status = BillingStatus.Cancelled;
        billing.CancelledAt = DateTime.UtcNow;
        _uow.Billings.Update(billing);

        var pendingChange = await _uow.SubscriptionChanges.FirstOrDefaultAsync(c =>
            c.BillingId == billing.Id && c.AgentUserId == userId && c.Status == SubscriptionChangeStatus.Pending);
        if (pendingChange != null)
        {
            pendingChange.Status = SubscriptionChangeStatus.Cancelled;
            pendingChange.CancelledAt = DateTime.UtcNow;
            _uow.SubscriptionChanges.Update(pendingChange);
        }

        await _uow.SaveChangesAsync();
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
        var invoiceList = invoices.OrderByDescending(i => i.IssuedAt).ToList();
        foreach (var invoice in invoiceList)
        {
            var billing = await _uow.Billings.GetByIdAsync(invoice.BillingId);
            if (billing != null)
            {
                invoice.Billing = billing;
            }
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

        return await CreateInvoiceAsync(activeSubscription.Id, userId, amount, false);
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
        DateTime? nextBillingDate = null)
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
            AmountDue = amountDue
        });
        await _uow.SaveChangesAsync();

        var invoice = await CreateInvoiceAsync(billing.Id, userId, amountDue, false);
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
            AmountDue = amountDue,
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
        await CreateInvoiceAsync(billing.Id, userId, 0, true);

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

    private async Task ApplyDuePendingChangesAsync(int userId)
    {
        var now = DateTime.UtcNow;
        var dueChanges = await _uow.SubscriptionChanges.FindAsync(c =>
            c.AgentUserId == userId &&
            c.Status == SubscriptionChangeStatus.Pending &&
            c.ChangeType == SubscriptionChangeType.Downgrade &&
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
        var rawCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.ClientId}:{_settings.ClientSecret}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", rawCredentials);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        });
        using var response = await client.PostAsync($"{_settings.BaseUrl}/v1/oauth2/token", content);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PayPal token request failed: {json}");
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
    }

    private bool HasPayPalSettings()
    {
        return !string.IsNullOrWhiteSpace(_settings.ClientId)
            && !string.IsNullOrWhiteSpace(_settings.ClientSecret);
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

    private sealed record PayPalOrderResult(string OrderId, string ApprovalUrl);
}
