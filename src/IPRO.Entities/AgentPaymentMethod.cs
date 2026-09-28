using System;

namespace IPRO.Entities;

// 527 (2026-09-28): the ways an adviser accepts payment from their clients, entered as what each
// service gives them -- a PayPal.me name, a Stripe Payment Link, a Square link, an Interac
// e-Transfer email, any other link. Every invoice shows a Pay button per method. Nothing here needs
// an account on iPro's side; the money and the receipt stay between the client and the service.
public static class PaymentMethodKinds
{
    public const string PayPal = "paypal";       // a PayPal.me name; the exact total is appended
    public const string Stripe = "stripe";       // a Stripe Payment Link; the invoice number rides along
    public const string Square = "square";       // a Square payment link, opened as created
    public const string ETransfer = "etransfer"; // an Interac e-Transfer email: an instruction, not a link
    public const string Other = "other";         // any other https link, shown as Pay Now
    public static readonly string[] All = { PayPal, Stripe, Square, ETransfer, Other };
}

// One row per adviser and method (unique); a method with no value has no row. Its own table, not
// columns on AgentUsers, for AgentFollowUpReminder's reason (498).
public class AgentPaymentMethod
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string Method { get; set; } = string.Empty;   // a PaymentMethodKinds value
    public string Value { get; set; } = string.Empty;    // the name, the link or the email, normalized
    public string Note { get; set; } = string.Empty;     // e-Transfer: one line for the client (auto-deposit, say); optional
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser AgentUser { get; set; } = null!;
}
