using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Billing;

// 678 (audit C-2): the setup fee a "keep my own website" checkout left out, paid when they switch to an iPro
// website. See WebsiteSetupFee.cs for the rule.
//
// 683 (the audit of 2026-11-26, PAY-1). The payment is followed by a marker in AgentOwnWebsites.SetupFeeOrderId:
//   <order>                  the order was made; they are at PayPal, or left
//   <order>|capturing        their return claimed it and asked PayPal to take the money
//   <order>|pending          PayPal took the request and is still processing the payment
//   <order>|paid|<capture>   PayPal has the money; the invoice is still to be written
// From "capturing" on, money may have moved: no new payment is started (BeginAsync), and nothing is called "not
// charged" unless PayPal itself says so. Before, a pending payment left the Pay button up (a second charge), a lost
// answer was shown as "nothing was charged", and a failure after the capture left no trace that money was taken.
public partial class PayPalBillingService : IWebsiteSetupFee
{
    public const string WebsiteSetupFeeLine = "Website setup fee (switch to an iPro website)";
    private const string CapturingState = "capturing";
    private const string PendingState = "pending";
    private const string PaidState = "paid";

    // At checkout (CreateSubscriptionAsync), for someone keeping their own website: what the setup fee would have been,
    // after the package's own waiver and any promotion code's setup discount; nothing when that comes to zero. A
    // referral gift is not taken off: with the fee left out, the gift went to the first billing periods instead.
    internal async Task RecordWaivedSetupFeeAsync(int userId, BillingRule package, PromotionCode? promo)
    {
        var fee = package.EffectiveSetupFee(DateTime.UtcNow);
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
        var row = await _db.AgentOwnWebsites.AsNoTracking()
            .Where(r => r.AgentUserId == userId)
            .Select(r => new { r.SetupFeeWaived, r.SetupFeeOrderId })
            .FirstOrDefaultAsync();
        if (row == null || row.SetupFeeWaived is not > 0m) return null;
        var marker = SetupFeeMarker.Read(row.SetupFeeOrderId);

        // Only while the subscription that left it out is running. Without an active subscription there is nothing
        // to switch for yet: their next checkout prices the fee itself once they use iPro's website. A payment
        // already in progress is settled whatever the subscription says now.
        if (!marker.InProgress &&
            !await _db.Billings.AnyAsync(b => b.AgentUserId == userId && b.Status == BillingStatus.Active)) return null;
        var tax = await CalculateTaxAsync(userId, row.SetupFeeWaived.Value);
        return new WebsiteSetupFeeQuote(row.SetupFeeWaived.Value, tax.Amount, tax.Rate, tax.Region, marker.InProgress ? marker.Order : null);
    }

    public async Task<BillingChangeResult> BeginAsync(int userId, string returnUrl, string cancelUrl)
    {
        var quote = await QuoteAsync(userId);
        if (quote == null) return BillingChangeResult.Failed("There is no setup fee to pay.");
        if (quote.OrderInProgress != null) return BillingChangeResult.Failed(SetupFeeInProgressMessage);
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

        // Only an order nobody has returned from is replaced: a payment that went into progress meanwhile (their
        // return in another tab) keeps its marker, and this new, unapproved order is simply never used.
        var orderId = order.OrderId;
        var total = quote.Total;
        var stored = await _db.AgentOwnWebsites
            .Where(r => r.AgentUserId == userId && (r.SetupFeeOrderId == null || !r.SetupFeeOrderId.Contains("|")))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.SetupFeeOrderId, orderId).SetProperty(r => r.SetupFeeOrderTotal, total));
        if (stored == 0) return BillingChangeResult.Failed(SetupFeeInProgressMessage);
        return new BillingChangeResult
        {
            Success = true,
            RequiresPayment = true,
            ApprovalUrl = order.ApprovalUrl,
            AmountDue = total,
            Message = "Please approve the setup fee payment with PayPal."
        };
    }

    private const string SetupFeeInProgressMessage =
        "A payment for the setup fee is already being confirmed with PayPal. Nothing more will be charged; use Check the payment on My Website.";

    public async Task<WebsiteSetupFeeOutcome> CompleteAsync(int userId, string orderId)
    {
        orderId = (orderId ?? string.Empty).Trim();
        if (orderId.Length == 0 || orderId.Contains('|')) return WebsiteSetupFeeOutcome.NothingOwed;

        var marker = SetupFeeMarker.Read(await _db.AgentOwnWebsites.AsNoTracking()
            .Where(r => r.AgentUserId == userId)
            .Select(r => r.SetupFeeOrderId)
            .FirstOrDefaultAsync());
        if (marker.Order != orderId) return WebsiteSetupFeeOutcome.NothingOwed;

        PayPalOrderLook look;
        switch (marker.State)
        {
            case "":
                // Claimed first, so a second return (a refresh, a double click) cannot ask for the money twice.
                var capturing = SetupFeeMarker.Write(orderId, CapturingState);
                var claimed = await _db.AgentOwnWebsites
                    .Where(r => r.AgentUserId == userId && r.SetupFeeOrderId == orderId)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.SetupFeeOrderId, capturing));
                if (claimed == 0) return WebsiteSetupFeeOutcome.Unfinished;   // another return of theirs took it this instant
                look = await CapturePayPalOrderOnceAsync(orderId);
                if (look.State == PayPalOrderState.Unknown) look = await LookAtPayPalOrderAsync(orderId);
                break;

            case CapturingState:
            case PendingState:
                // A return after a capture whose answer never arrived, or one PayPal was still processing: PayPal says
                // what became of it, and an order it still shows as approved but not taken is captured now.
                look = await LookAtPayPalOrderAsync(orderId);
                if (look.State == PayPalOrderState.Approved)
                {
                    look = await CapturePayPalOrderOnceAsync(orderId);
                    if (look.State == PayPalOrderState.Unknown) look = await LookAtPayPalOrderAsync(orderId);
                }
                break;

            case PaidState:
                return await FinishPaidSetupFeeAsync(userId, orderId);

            default:
                return WebsiteSetupFeeOutcome.NothingOwed;
        }

        switch (look.State)
        {
            case PayPalOrderState.Completed:
                await MoveSetupFeeMarkerAsync(userId, orderId, SetupFeeMarker.Write(orderId, PaidState, look.CaptureId));
                return await FinishPaidSetupFeeAsync(userId, orderId);

            case PayPalOrderState.Pending:
                await MoveSetupFeeMarkerAsync(userId, orderId, SetupFeeMarker.Write(orderId, PendingState));
                _logger.LogError(
                    "Website setup fee order {OrderId} for agent {AgentId} is PENDING at PayPal: not marked paid and the website not switched. " +
                    "It is checked again when they open Check the payment on My Website; if PayPal has completed it by then, it is invoiced and the website switched.",
                    orderId, userId);
                return WebsiteSetupFeeOutcome.Pending;

            case PayPalOrderState.Refused:
                // PayPal itself says nothing was taken.
                var wasCapturing = SetupFeeMarker.Write(orderId, CapturingState);
                var wasPending = SetupFeeMarker.Write(orderId, PendingState);
                await _db.AgentOwnWebsites
                    .Where(r => r.AgentUserId == userId && (r.SetupFeeOrderId == wasCapturing || r.SetupFeeOrderId == wasPending))
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.SetupFeeOrderId, (string?)null).SetProperty(r => r.SetupFeeOrderTotal, (decimal?)null));
                return WebsiteSetupFeeOutcome.Refused;

            case PayPalOrderState.Approved:
                // Approved by them, not taken, and the capture could not be made now: back to where their return began,
                // so the next look captures it.
                await MoveSetupFeeMarkerAsync(userId, orderId, orderId);
                return WebsiteSetupFeeOutcome.Unfinished;

            default:
                _logger.LogError(
                    "Website setup fee order {OrderId} for agent {AgentId}: PayPal did not say whether the money was taken. The payment is kept as in progress " +
                    "(no new payment can be started) and is settled when they open Check the payment on My Website. If it stays, look the order up in PayPal.",
                    orderId, userId);
                return WebsiteSetupFeeOutcome.Unfinished;
        }
    }

    // PayPal has the money. The marker is given up and the invoice written in ONE transaction: two returns cannot both
    // invoice it (the second waits on the row, then finds the marker gone), and if anything fails the marker stays
    // "paid" for the next look to finish. Read-committed, so the invoice number's retry sees numbers taken meanwhile.
    private async Task<WebsiteSetupFeeOutcome> FinishPaidSetupFeeAsync(int userId, string orderId)
    {
        Invoice invoice;
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            var row = await _db.AgentOwnWebsites.AsNoTracking()
                .Where(r => r.AgentUserId == userId)
                .Select(r => new { r.SetupFeeWaived, r.SetupFeeOrderTotal, r.SetupFeeOrderId })
                .FirstOrDefaultAsync();
            var marker = SetupFeeMarker.Read(row?.SetupFeeOrderId);
            if (row == null || marker.Order != orderId || marker.State != PaidState) return WebsiteSetupFeeOutcome.NothingOwed;

            var paidMarker = row.SetupFeeOrderId;
            var released = await _db.AgentOwnWebsites
                .Where(r => r.AgentUserId == userId && r.SetupFeeOrderId == paidMarker)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.SetupFeeWaived, (decimal?)null)
                    .SetProperty(r => r.SetupFeeOrderId, (string?)null)
                    .SetProperty(r => r.SetupFeeOrderTotal, (decimal?)null));
            if (released == 0) return WebsiteSetupFeeOutcome.NothingOwed;   // another return finished it

            // The invoice says what was taken: the total the order was made for, the fee, and the tax between them.
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
            invoice = await CreateInvoiceWithLinesAsync(billingId, userId, fee, taxAmount, tax.Rate, tax.Region, true,
                new[] { new InvoiceLineDraft(WebsiteSetupFeeLine, fee) });
            // PAY-5: the capture is what PayPal's own lists show and what a refund is made against; the order id is
            // kept only when PayPal gave no capture id.
            invoice.PayPalTransactionId = string.IsNullOrWhiteSpace(marker.Capture) ? orderId : marker.Capture;
            _uow.Invoices.Update(invoice);
            await _uow.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Website setup fee order {OrderId} for agent {AgentId} is PAID at PayPal but its invoice could not be written. The payment is kept as paid " +
                "and is invoiced, and the website switched, when they open Check the payment on My Website. Nothing more is charged.",
                orderId, userId);
            return WebsiteSetupFeeOutcome.Unfinished;
        }

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

    // From "capturing" or "pending" to the next state; a marker that has moved on meanwhile is left alone.
    private Task<int> MoveSetupFeeMarkerAsync(int userId, string orderId, string to)
    {
        var capturing = SetupFeeMarker.Write(orderId, CapturingState);
        var pending = SetupFeeMarker.Write(orderId, PendingState);
        return _db.AgentOwnWebsites
            .Where(r => r.AgentUserId == userId && (r.SetupFeeOrderId == capturing || r.SetupFeeOrderId == pending))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.SetupFeeOrderId, to));
    }

    public async Task CancelAsync(int userId, string? orderId)
    {
        var id = (orderId ?? string.Empty).Trim();
        if (id.Length == 0) return;
        // Only an order nobody returned from: a payment in progress is never cancelled from the browser.
        await _db.AgentOwnWebsites.Where(r => r.AgentUserId == userId && r.SetupFeeOrderId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.SetupFeeOrderId, (string?)null).SetProperty(r => r.SetupFeeOrderTotal, (decimal?)null));
    }

    // 683 (PAY-2): the fee is asked for only while a subscription is ACTIVE, and a checkout waiting for approval is not
    // one yet. Someone could start a checkout keeping their own website (priced without the fee), switch to an iPro
    // website for free, then approve it. The waiting checkout is cancelled at that switch (its approval link no longer
    // completes: the stale-approval guard), so their next checkout is priced as an iPro website's.
    public async Task<bool> DropPendingCheckoutAsync(int userId)
    {
        var waived = await _db.AgentOwnWebsites.AsNoTracking()
            .Where(r => r.AgentUserId == userId)
            .Select(r => r.SetupFeeWaived)
            .FirstOrDefaultAsync();
        if (waived is not > 0m) return false;
        if (await _db.Billings.AnyAsync(b => b.AgentUserId == userId && b.Status == BillingStatus.Active)) return false;   // QuoteAsync asks for the fee
        if (!await _db.Billings.AnyAsync(b => b.AgentUserId == userId && b.Status == BillingStatus.Pending)) return false;

        await CancelPendingChangesAsync(userId);
        _logger.LogWarning(
            "Agent {AgentId} switched to an iPro website while a checkout that left out the website setup fee was waiting for approval: that checkout was cancelled.",
            userId);
        return true;
    }

    // ---- PayPal, for this payment ----------------------------------------------------------------------------

    internal enum PayPalOrderState
    {
        Completed,  // the money is taken
        Pending,    // PayPal is processing the payment
        Approved,   // approved by the payer, not taken
        Refused,    // nothing was taken, and nothing will be: declined, never approved, voided, unknown to PayPal
        Unknown     // PayPal did not answer, or answered something that cannot be read
    }

    internal readonly record struct PayPalOrderLook(PayPalOrderState State, string CaptureId);

    // The capture, asked for under an idempotency key (PayPal-Request-Id): asking again for the same order returns the
    // first answer and takes the money once.
    private async Task<PayPalOrderLook> CapturePayPalOrderOnceAsync(string orderId)
    {
        if (!HasPayPalSettings()) return new(PayPalOrderState.Refused, string.Empty);
        try
        {
            var accessToken = await GetPayPalAccessTokenAsync();
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings.BaseUrl}/v2/checkout/orders/{Uri.EscapeDataString(orderId)}/capture")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("PayPal-Request-Id", "website-setup-fee-" + orderId);
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode) return ReadPayPalOrderLook(body);
            if ((int)response.StatusCode >= 500) return new(PayPalOrderState.Unknown, string.Empty);
            // Already captured (an earlier request got through): the order itself says how it ended.
            if (body.Contains("ORDER_ALREADY_CAPTURED", StringComparison.Ordinal)) return await LookAtPayPalOrderAsync(orderId);
            return new(PayPalOrderState.Refused, string.Empty);   // declined, not approved, no such order
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            _logger.LogWarning(ex, "Capturing the website setup fee order {OrderId} got no answer from PayPal.", orderId);
            return new(PayPalOrderState.Unknown, string.Empty);
        }
    }

    private async Task<PayPalOrderLook> LookAtPayPalOrderAsync(string orderId)
    {
        if (!HasPayPalSettings()) return new(PayPalOrderState.Unknown, string.Empty);
        try
        {
            var accessToken = await GetPayPalAccessTokenAsync();
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_settings.BaseUrl}/v2/checkout/orders/{Uri.EscapeDataString(orderId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new(PayPalOrderState.Refused, string.Empty);
            if (!response.IsSuccessStatusCode) return new(PayPalOrderState.Unknown, string.Empty);
            return ReadPayPalOrderLook(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            _logger.LogWarning(ex, "Looking up the website setup fee order {OrderId} got no answer from PayPal.", orderId);
            return new(PayPalOrderState.Unknown, string.Empty);
        }
    }

    // What an order (or a capture answer) says happened to the money: the capture's own status and id when there is
    // one (purchase_units[0].payments.captures[0]), else the order's status.
    internal static PayPalOrderLook ReadPayPalOrderLook(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("purchase_units", out var units) && units.ValueKind == JsonValueKind.Array && units.GetArrayLength() > 0
                && units[0].TryGetProperty("payments", out var payments) && payments.ValueKind == JsonValueKind.Object
                && payments.TryGetProperty("captures", out var captures) && captures.ValueKind == JsonValueKind.Array && captures.GetArrayLength() > 0)
            {
                var capture = captures[0];
                var captureId = capture.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? string.Empty : string.Empty;
                var captureStatus = capture.TryGetProperty("status", out var cs) && cs.ValueKind == JsonValueKind.String ? cs.GetString() : null;
                return captureStatus switch
                {
                    "COMPLETED" => new(PayPalOrderState.Completed, captureId),
                    "PENDING" => new(PayPalOrderState.Pending, captureId),
                    null => new(PayPalOrderState.Unknown, string.Empty),
                    _ => new(PayPalOrderState.Refused, string.Empty)   // DECLINED, FAILED, REFUNDED
                };
            }

            var status = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return status switch
            {
                "COMPLETED" => new(PayPalOrderState.Completed, string.Empty),
                "APPROVED" => new(PayPalOrderState.Approved, string.Empty),
                "CREATED" or "SAVED" or "PAYER_ACTION_REQUIRED" or "VOIDED" => new(PayPalOrderState.Refused, string.Empty),
                _ => new(PayPalOrderState.Unknown, string.Empty)
            };
        }
        catch (JsonException)
        {
            return new(PayPalOrderState.Unknown, string.Empty);
        }
    }
}

// The marker in AgentOwnWebsites.SetupFeeOrderId (see the top of PayPalBillingService.WebsiteSetupFee.cs).
internal readonly record struct SetupFeeMarker(string Order, string State, string Capture)
{
    // From "capturing" on, money may have moved.
    public bool InProgress => State.Length > 0;

    public static SetupFeeMarker Read(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new(string.Empty, string.Empty, string.Empty);
        var parts = value.Split('|');
        return new(parts[0], parts.Length > 1 ? parts[1] : string.Empty, parts.Length > 2 ? parts[2] : string.Empty);
    }

    public static string Write(string order, string state, string? capture = null) =>
        capture == null ? $"{order}|{state}" : $"{order}|{state}|{capture}";
}
