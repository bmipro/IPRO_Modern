using System;
using System.Text.Json;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.DataAccess;

public enum StripeWebhookOutcome { Ignored, Malformed, NotFound, AlreadyRecorded, Settled, RecordedShort }

// 527 (2026-09-27): what a verified Stripe Connect event does to an invoice. Only
// checkout.session.completed with payment_status "paid" settles anything, and only for a session
// that carries our invoice id and comes from the account the invoice's adviser connected. One
// payment row per event id: Stripe resends for days, and a resend must be a no-op. An amount short
// of the total is recorded but does not mark the invoice paid (Checkout is created for the exact
// total, so this is a guard, not a path).
public static class StripeWebhookSettlement
{
    public const string InvoiceMetadataKey = "ipro_invoice_id";

    public static async Task<StripeWebhookOutcome> ApplyAsync(IPRODbContext db, string payload, DateTime nowUtc, ILogger? logger = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return StripeWebhookOutcome.Malformed;
        }

        using (document)
        {
            var root = document.RootElement;
            var eventId = ReadString(root, "id");
            if (string.IsNullOrWhiteSpace(eventId)) return StripeWebhookOutcome.Malformed;
            if (ReadString(root, "type") != "checkout.session.completed") return StripeWebhookOutcome.Ignored;
            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var session)) return StripeWebhookOutcome.Malformed;
            if (ReadString(session, "payment_status") != "paid") return StripeWebhookOutcome.Ignored;

            int invoiceId = 0;
            if (session.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                int.TryParse(ReadString(metadata, InvoiceMetadataKey), out invoiceId);
            }
            if (invoiceId <= 0) return StripeWebhookOutcome.Ignored;                 // not one of ours

            if (await db.ClientInvoicePayments.AnyAsync(p => p.Provider == PaymentProviders.Stripe && p.ProviderEventId == eventId))
                return StripeWebhookOutcome.AlreadyRecorded;

            var invoice = await db.ClientInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId);
            if (invoice == null) return StripeWebhookOutcome.NotFound;

            // The event must come from the account this adviser connected (active or since closed:
            // a session created before a disconnect still settles).
            var account = ReadString(root, "account");
            var connection = await db.AgentPaymentConnections.AsNoTracking()
                .FirstOrDefaultAsync(c => c.AgentUserId == invoice.AgentUserId && c.Provider == PaymentProviders.Stripe);
            if (connection == null || !string.Equals(connection.ExternalAccountId, account, StringComparison.Ordinal))
            {
                logger?.LogWarning("Stripe event {EventId} for invoice {InvoiceId} came from account {Account}, not the adviser's connection; ignored", eventId, invoiceId, account);
                return StripeWebhookOutcome.Ignored;
            }

            var amount = session.TryGetProperty("amount_total", out var amountElement) && amountElement.TryGetInt64(out var cents) ? cents / 100m : 0m;
            var currency = ReadString(session, "currency");
            currency = string.IsNullOrWhiteSpace(currency) ? invoice.Currency : currency.ToUpperInvariant();
            var paymentId = ReadString(session, "payment_intent");
            if (string.IsNullOrWhiteSpace(paymentId)) paymentId = ReadString(session, "id");

            db.ClientInvoicePayments.Add(new ClientInvoicePayment
            {
                ClientInvoiceId = invoice.Id,
                AgentUserId = invoice.AgentUserId,
                Provider = PaymentProviders.Stripe,
                ProviderEventId = eventId,
                ProviderPaymentId = paymentId,
                Amount = amount,
                Currency = currency,
                ReceivedAt = nowUtc,
                CreatedAt = nowUtc
            });

            var covers = amount + 0.005m >= invoice.Total;
            if (covers && invoice.Status != ClientInvoiceStatus.Paid)
            {
                invoice.Status = ClientInvoiceStatus.Paid;
                invoice.PaidAt = nowUtc;
                invoice.PaidMethod = ClientInvoicePaymentMethod.Online;
                invoice.UpdatedAt = nowUtc;
            }
            await db.SaveChangesAsync();

            if (!covers)
            {
                logger?.LogWarning("Stripe event {EventId} paid {Amount} {Currency} against invoice {InvoiceId} of {Total}; recorded, not settled", eventId, amount, currency, invoiceId, invoice.Total);
                return StripeWebhookOutcome.RecordedShort;
            }
            return StripeWebhookOutcome.Settled;
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
