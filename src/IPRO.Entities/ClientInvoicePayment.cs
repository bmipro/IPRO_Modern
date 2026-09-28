using System;

namespace IPRO.Entities;

// 527 (2026-09-27): a payment a processor reported for a client invoice. One row per processor
// event, unique on (Provider, ProviderEventId): Stripe resends events for days and the same
// notice arriving twice must never pay an invoice twice. Its own table so the invoice row keeps
// its shape and the audit trail of what money moved lives in one place.
public class ClientInvoicePayment
{
    public int Id { get; set; }
    public int ClientInvoiceId { get; set; }
    public int AgentUserId { get; set; }
    public string Provider { get; set; } = string.Empty;            // a PaymentProviders value
    public string ProviderEventId { get; set; } = string.Empty;     // Stripe: evt_...
    public string ProviderPaymentId { get; set; } = string.Empty;   // Stripe: pi_... (the payment), else the session id
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "CAD";
    public decimal? Fee { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ClientInvoice ClientInvoice { get; set; } = null!;
}
