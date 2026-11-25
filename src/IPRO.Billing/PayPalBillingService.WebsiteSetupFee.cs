using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Billing;

// 678 (audit C-2): the setup fee a "keep my own website" checkout left out, paid when they switch to an iPro
// website. See WebsiteSetupFee.cs for the rule.
public partial class PayPalBillingService : IWebsiteSetupFee
{
    public const string WebsiteSetupFeeLine = "Website setup fee (switch to an iPro website)";
    private const string CapturingSuffix = "|capturing";
    private const string PendingSuffix = "|pending";

    // At checkout (CreateSubscriptionAsync): what the setup fee would have been had they not kept their own website,
    // after the package's own waiver and any promotion code's setup discount. A referral gift is not taken off:
    // with the fee left out, the gift went to the first billing periods instead.
    internal async Task RecordWaivedSetupFeeAsync(int userId, BillingRule package, PromotionCode? promo, bool nothingLeftOut = false)
    {
        var fee = nothingLeftOut ? 0m : package.EffectiveSetupFee(DateTime.UtcNow);
        if (promo != null && promo.SetupFeeDiscountType != PromoDiscountType.None)
        {
            fee = ComputeDiscountedAmount(fee, promo.SetupFeeDiscountType, promo.SetupFeeDiscountValue);
        }
        fee = Math.Max(0m, Math.Round(fee, 2, MidpointRounding.AwayFromZero));
        decimal? waived = fee > 0m ? fee : null;
        await _db.AgentOwnWebsites.Where(r => r.AgentUserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.SetupFeeWaived, waived));
    }

    public async Task<WebsiteSetupFeeQuote?> QuoteAsync(int userId)
    {
        var waived = await _db.AgentOwnWebsites.AsNoTracking()
            .Where(r => r.AgentUserId == userId)
            .Select(r => r.SetupFeeWaived)
            .FirstOrDefaultAsync();
        if (waived is not > 0m) return null;
        // Only while the subscription that left it out is running. Without an active subscription there is nothing
        // to switch for yet: their next checkout prices the fee itself once they use iPro's website.
        if (!await _db.Billings.AnyAsync(b => b.AgentUserId == userId && b.Status == BillingStatus.Active)) return null;
        var tax = await CalculateTaxAsync(userId, waived.Value);
        return new WebsiteSetupFeeQuote(waived.Value, tax.Amount, tax.Rate, tax.Region);
    }

    public async Task<BillingChangeResult> BeginAsync(int userId, string returnUrl, string cancelUrl)
    {
        var quote = await QuoteAsync(userId);
        if (quote == null) return BillingChangeResult.Failed("There is no setup fee to pay.");
        var taxAgent = await _uow.AgentUsers.GetByIdAsync(userId);
        if (TaxAddress.Problem(taxAgent?.Country, taxAgent?.Province) is { } taxProblem) return BillingChangeResult.Failed(taxProblem);
        if (!HasPayPalSettings()) return BillingChangeResult.Failed("Payments are not set up on this platform, so the setup fee cannot be paid now. Please contact support.");

        // A stand-in invoice carries the reference and the amount to PayPal; the real invoice is written only once the
        // money is taken, so nothing unpaid sits on their subscription for a renewal to be matched to.
        var standIn = new Invoice { Id = 0, InvoiceNumber = $"WEBSITE-SETUP-{userId}", Currency = "CAD", Total = quote.Total };
        PayPalOrderResult order;
        try
        {
            order = await CreatePayPalOrderAsync(standIn, "website setup fee", returnUrl, cancelUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The website setup fee order for agent {AgentId} could not be created at PayPal.", userId);
            return BillingChangeResult.Failed("PayPal could not start the payment just now. Nothing was charged; please try again in a few minutes.");
        }
        if (string.IsNullOrWhiteSpace(order.OrderId) || string.IsNullOrWhiteSpace(order.ApprovalUrl))
        {
            return BillingChangeResult.Failed("PayPal did not return a payment page. Nothing was charged; please try again.");
        }

        var orderId = order.OrderId;
        var total = quote.Total;
        await _db.AgentOwnWebsites.Where(r => r.AgentUserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.SetupFeeOrderId, orderId).SetProperty(r => r.SetupFeeOrderTotal, total));
        return new BillingChangeResult
        {
            Success = true,
            RequiresPayment = true,
            ApprovalUrl = order.ApprovalUrl,
            AmountDue = total,
            Message = "Please approve the setup fee payment with PayPal."
        };
    }

    public async Task<WebsiteSetupFeeOutcome> CompleteAsync(int userId, string orderId)
    {
        orderId = (orderId ?? string.Empty).Trim();
        if (orderId.Length == 0 || orderId.Contains('|')) return WebsiteSetupFeeOutcome.NothingOwed;

        // Claimed first, so a second return (a refresh, a double click) cannot capture or invoice it twice.
        var capturing = orderId + CapturingSuffix;
        var claimed = await _db.AgentOwnWebsites
            .Where(r => r.AgentUserId == userId && r.SetupFeeOrderId == orderId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.SetupFeeOrderId, capturing));
        if (claimed == 0) return WebsiteSetupFeeOutcome.NothingOwed;

        var row = await _db.AgentOwnWebsites.AsNoTracking()
            .Where(r => r.AgentUserId == userId)
            .Select(r => new { r.SetupFeeWaived, r.SetupFeeOrderTotal })
            .FirstAsync();

        OrderCapture capture;
        try
        {
            capture = await CapturePayPalOrderAsync(orderId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capturing the website setup fee order {OrderId} for agent {AgentId} failed.", orderId, userId);
            capture = OrderCapture.Refused;
        }

        if (capture == OrderCapture.Pending)
        {
            var pending = orderId + PendingSuffix;
            await _db.AgentOwnWebsites.Where(r => r.AgentUserId == userId)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.SetupFeeOrderId, pending));
            _logger.LogError(
                "Website setup fee order {OrderId} for agent {AgentId} was captured as PENDING: not marked paid and the website not switched. " +
                "Check it in PayPal; once PayPal completes it, invoice it and switch their website by hand.",
                orderId, userId);
            return WebsiteSetupFeeOutcome.Pending;
        }

        if (capture != OrderCapture.Completed)
        {
            await _db.AgentOwnWebsites.Where(r => r.AgentUserId == userId && r.SetupFeeOrderId == capturing)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.SetupFeeOrderId, (string?)null).SetProperty(r => r.SetupFeeOrderTotal, (decimal?)null));
            return WebsiteSetupFeeOutcome.Refused;
        }

        // Paid. The invoice says what was taken: the total the order was made for, the fee, and the tax between them.
        var fee = row.SetupFeeWaived ?? 0m;
        var total = row.SetupFeeOrderTotal ?? fee;
        var tax = await CalculateTaxAsync(userId, fee);
        var taxAmount = tax.Amount;
        if (fee + taxAmount != total)
        {
            _logger.LogError(
                "Website setup fee order {OrderId} for agent {AgentId}: taken {Total}, but fee {Fee} plus tax {Tax} now comes to {Now} " +
                "(their province changed between the order and the payment?). The invoice records what was taken.",
                orderId, userId, total, fee, taxAmount, fee + taxAmount);
            taxAmount = Math.Max(0m, total - fee);
        }

        var billingId = await _db.Billings.AsNoTracking()
            .Where(b => b.AgentUserId == userId)
            .OrderByDescending(b => b.Status == BillingStatus.Active).ThenByDescending(b => b.Id)
            .Select(b => b.Id)
            .FirstOrDefaultAsync();
        var invoice = await CreateInvoiceWithLinesAsync(billingId, userId, fee, taxAmount, tax.Rate, tax.Region, true,
            new[] { new InvoiceLineDraft(WebsiteSetupFeeLine, fee) });
        invoice.PayPalTransactionId = orderId;
        _uow.Invoices.Update(invoice);
        await _uow.SaveChangesAsync();

        await _db.AgentOwnWebsites.Where(r => r.AgentUserId == userId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.SetupFeeWaived, (decimal?)null)
                .SetProperty(r => r.SetupFeeOrderId, (string?)null)
                .SetProperty(r => r.SetupFeeOrderTotal, (decimal?)null));

        try
        {
            await SendPaidInvoiceEmailAsync(invoice.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The paid invoice email for the website setup fee (invoice {InvoiceId}) failed.", invoice.Id);
        }
        return WebsiteSetupFeeOutcome.Paid;
    }

    public async Task CancelAsync(int userId, string? orderId)
    {
        var id = (orderId ?? string.Empty).Trim();
        if (id.Length == 0) return;
        await _db.AgentOwnWebsites.Where(r => r.AgentUserId == userId && r.SetupFeeOrderId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.SetupFeeOrderId, (string?)null).SetProperty(r => r.SetupFeeOrderTotal, (decimal?)null));
    }
}
