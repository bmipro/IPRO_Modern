using IPRO.Entities;

namespace IPRO.Web.Models;

// 527 (2026-09-28): the Payments page -- what each service gave the adviser, entered here and shown
// on their invoices as a Pay button per method. The Stripe Connect card (slice 1, dormant) shows
// only when the platform has keys. The Profile's single link from before is offered for saving
// here the first time.
public class PaymentsForm
{
    public string? PayPal { get; set; }
    public string? StripeLink { get; set; }
    public string? SquareLink { get; set; }
    public string? ETransferEmail { get; set; }
    public string? ETransferNote { get; set; }
    public string? OtherLink { get; set; }
}

public class PaymentsViewModel
{
    public PaymentsForm Form { get; set; } = new();
    public bool HasSavedMethods { get; set; }
    public string? LegacyLink { get; set; }
    public bool StripeConfigured { get; set; }
    public AgentPaymentConnection? Stripe { get; set; }
}
