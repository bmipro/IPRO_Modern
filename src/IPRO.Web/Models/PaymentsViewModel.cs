using IPRO.Entities;

namespace IPRO.Web.Models;

// 527: the Payments page -- which processors are connected, whether the platform has Stripe set
// up at all, and the pasted Pay Now link that stays as the zero-setup fallback.
public class PaymentsViewModel
{
    public bool StripeConfigured { get; set; }
    public AgentPaymentConnection? Stripe { get; set; }
    public string? PaymentLink { get; set; }
}
