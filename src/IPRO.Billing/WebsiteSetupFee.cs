namespace IPRO.Billing;

// 678 (audit 2026-11-24 C-2, the owner 2026-11-25: "Setup fee when a customer switches YES"). A professional who
// signs up keeping their own website pays no setup fee (646): there is no website for iPro to set up. Switching to
// an iPro website later used to cost nothing, so ticking "I already have a website" at sign-up and switching back
// after the first payment skipped the fee. Now the fee the checkout left out is recorded on their AgentOwnWebsites
// row (SetupFeeWaived), and switching while that subscription is active asks for it first: a one-off PayPal
// payment with tax, a paid invoice, then the switch.
public sealed record WebsiteSetupFeeQuote(decimal Fee, decimal Tax, decimal TaxRate, string TaxRegion)
{
    public decimal Total => Fee + Tax;
}

public enum WebsiteSetupFeeOutcome
{
    Paid,        // captured, invoiced, nothing owed any more: switch now
    NothingOwed, // no payment of this kind is under way for this order (already paid, or never started)
    Pending,     // PayPal is still processing it: not paid yet, finished by hand once PayPal completes it
    Refused      // not paid; they may try again
}

public interface IWebsiteSetupFee
{
    // What switching costs now, or null when it costs nothing.
    Task<WebsiteSetupFeeQuote?> QuoteAsync(int userId);

    // Starts the PayPal payment; ApprovalUrl is where to send them.
    Task<BillingChangeResult> BeginAsync(int userId, string returnUrl, string cancelUrl);

    // Their return from PayPal: captures the payment and issues the paid invoice.
    Task<WebsiteSetupFeeOutcome> CompleteAsync(int userId, string orderId);

    // They cancelled at PayPal: nothing was taken.
    Task CancelAsync(int userId, string? orderId);
}
