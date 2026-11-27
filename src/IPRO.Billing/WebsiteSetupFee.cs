namespace IPRO.Billing;

// 678 (audit 2026-11-24 C-2, the owner 2026-11-25: "Setup fee when a customer switches YES"). A professional who
// signs up keeping their own website pays no setup fee (646): there is no website for iPro to set up. Switching to
// an iPro website later used to cost nothing, so ticking "I already have a website" at sign-up and switching back
// after the first payment skipped the fee. Now the fee the checkout left out is recorded on their AgentOwnWebsites
// row (SetupFeeWaived), and switching while that subscription is active asks for it first: a one-off PayPal
// payment with tax, a paid invoice, then the switch.
//
// 683 (the audit of 2026-11-26, PAY-1): OrderInProgress is the PayPal order of a payment that is past the point where
// money may have moved (being captured, pending at PayPal, or paid and not yet invoiced). While it is set no new
// payment is started; the page offers to check that one instead.
public sealed record WebsiteSetupFeeQuote(decimal Fee, decimal Tax, decimal TaxRate, string TaxRegion, string? OrderInProgress = null)
{
    public decimal Total => Fee + Tax;
}

public enum WebsiteSetupFeeOutcome
{
    Paid,        // captured, invoiced, nothing owed any more: switch now
    NothingOwed, // no payment of this kind is under way for this order (already paid, or never started)
    Pending,     // PayPal is still processing it: not paid yet; checked again when they come back
    Refused,     // PayPal says nothing was taken; they may try again
    Unfinished   // 683: money may have moved and it could not be settled now (PayPal did not answer, or the invoice
                 // could not be written): nothing more is charged, and coming back finishes it
}

public interface IWebsiteSetupFee
{
    // What switching costs now, or null when it costs nothing.
    Task<WebsiteSetupFeeQuote?> QuoteAsync(int userId);

    // Starts the PayPal payment; ApprovalUrl is where to send them. Refused while one is in progress.
    Task<BillingChangeResult> BeginAsync(int userId, string returnUrl, string cancelUrl);

    // Their return from PayPal, or a later look at a payment in progress: captures it if that is still to do,
    // asks PayPal what happened if the answer was lost, and issues the paid invoice once.
    Task<WebsiteSetupFeeOutcome> CompleteAsync(int userId, string orderId);

    // They cancelled at PayPal: nothing was taken.
    Task CancelAsync(int userId, string? orderId);

    // 683 (PAY-2): a checkout that left the fee out and is still waiting for approval at PayPal is cancelled (the
    // approval link then no longer completes), so a free switch cannot be followed by approving a subscription that
    // was priced without the fee. True when one was cancelled.
    Task<bool> DropPendingCheckoutAsync(int userId);
}
