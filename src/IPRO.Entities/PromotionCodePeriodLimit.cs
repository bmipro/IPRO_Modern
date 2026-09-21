namespace IPRO.Entities;

// 508 (2026-09-21): a promotion code that works with ONE billing period only. A code's "cycle" is
// whatever period the customer picks, so "100% off, 1 cycle" -- the free first month the owner
// wants to send prospects -- was also a free first YEAR for anyone who chose annual billing.
// No row means the code works with both periods, which is every code that existed before this.
//
// A table of its own and NOT a column on PromotionCodes, for AgentFollowUpReminder's reason (498):
// StartupGuard.RunStepAsync swallows a failed schema repair, and a column the database does not
// have breaks EVERY query on its table -- here, every registration that types a code.
public class PromotionCodePeriodLimit
{
    public int PromotionCodeId { get; set; }
    public BillingPeriod Period { get; set; }
    public PromotionCode PromotionCode { get; set; } = null!;
}
