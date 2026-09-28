using System;

namespace IPRO.Entities;

public static class PaymentProviders
{
    public const string Stripe = "stripe";
    public const string PayPal = "paypal";
    public const string Square = "square";
}

// 527 (2026-09-27): a payment processor the adviser connected so their clients can pay invoices by
// card and the invoice settles itself. One row per adviser and provider; a disconnect closes the
// row (IsActive false, DisconnectedAt set) and a reconnect reopens it, so a payment that Stripe
// reports for an old session still finds the account it came from.
//
// Stripe stores no secret here: with a Standard connected account, iPro's own key acts for the
// adviser with a Stripe-Account header, and an account id is not a secret. EncryptedTokens exists
// for PayPal and Square, whose connections do hand back tokens; it stays empty for Stripe.
//
// Its own table, not columns on AgentUsers, for AgentFollowUpReminder's reason (498).
public class AgentPaymentConnection
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string Provider { get; set; } = string.Empty;            // a PaymentProviders value
    public string ExternalAccountId { get; set; } = string.Empty;   // Stripe: acct_...
    public string DisplayName { get; set; } = string.Empty;         // the account's own name, when the processor tells us
    public bool IsLive { get; set; }
    public bool IsActive { get; set; } = true;
    public string EncryptedTokens { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DisconnectedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser AgentUser { get; set; } = null!;
}
