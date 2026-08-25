namespace IPRO.Entities;

// Wave-2 D (billing audit 2026-08-25): the mutual-exclusion fence for cancellation outcomes.
// Three doors (self-cancel, the CANCELLED/EXPIRED webhook, the hourly reconcile) can race to turn
// one subscription's death into money-and-access truth; whichever door INSERTS this row first --
// inside the same transaction as the outcome it mints -- owns the cancellation, and the loser's
// duplicate-key failure rolls its whole mint back. A status-flip claim was rejected for this job
// because it breaks crash-atomicity: a claim that commits ahead of a mint that then fails leaves
// a Cancelled row with no outcome and no retry. This row commits WITH the outcome or not at all.
//
// No FK on purpose: the row is a fence, not data. It carries no PII, billing ids are never
// reused, and the eraser has nothing to scrub here.
public class BillingCancellationClaim
{
    public int BillingId { get; set; }          // primary key — one outcome per billing, ever
    public DateTime ClaimedAt { get; set; }
    public string Trigger { get; set; } = string.Empty;
}
