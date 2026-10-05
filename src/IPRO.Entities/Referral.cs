namespace IPRO.Entities;

// 532 (2026-10-05): Refer a Friend -- Give $50, Get $50. The owner's design of 2026-09-29 (DOCS/TODO.md 532): any
// adviser refers anyone; the friend gets $50 off at checkout, the referrer $50 (plus its tax) back as a refund once
// the friend has stayed (the second monthly payment, or 30 days into an annual plan). Nothing here moves money:
// an earned reward waits in SuperAdmin -> Refunds like every other refund (DOCS/22).

// The program's switch and its two amounts, one row (Id = 1), edited in SuperAdmin -> Referrals. Off until the
// owner turns it on. A change applies to new referrals; a promised amount stays on its Referral row.
public class ReferralProgramSettings
{
    public const decimal DefaultAmount = 50m;

    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public decimal FriendGiftAmount { get; set; } = DefaultAmount;
    public decimal ReferrerRewardAmount { get; set; } = DefaultAmount;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// One permanent code per adviser, made the first time they open Refer a Friend, shared by every friend. Never a row
// in Promotion Codes (that list stays the owner's campaigns), and never equal to one.
public class ReferralCode
{
    public int AgentUserId { get; set; }
    public string Code { get; set; } = string.Empty;
    public bool IsPaused { get; set; }
    public DateTime? PausedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser AgentUser { get; set; } = null!;
}

public static class ReferralStages
{
    public const string SignedUp = "signed-up";     // registered with the code; not subscribed yet
    public const string Joined = "joined";          // their subscription started; the reward is on its way
    public const string Earned = "earned";          // the friend stayed; the refund waits in SuperAdmin -> Refunds
    public const string Paid = "paid";              // refunded at PayPal and marked so; credit note issued
    public const string NotEarned = "not-earned";   // the friend left before the reward was earned
    public const string Voided = "voided";          // SuperAdmin voided it, or the same PayPal payer blocked it

    public static readonly string[] All = { SignedUp, Joined, Earned, Paid, NotEarned, Voided };

    public static string Label(string? stage) => stage switch
    {
        SignedUp => "Signed up",
        Joined => "Joined",
        Earned => "Earned",
        Paid => "Paid",
        NotEarned => "Not earned",
        Voided => "Voided",
        _ => stage ?? string.Empty
    };
}

// The ledger: one row per friend who signed up with a code. AgentUserId is the REFERRER (the row is theirs, and it
// is a money record: retained with the invoices when an adviser is deleted -- AgentDataEraser's FinancialMap). The
// friend's name and business are copied so the row stays readable whatever happens to the friend's account.
public class Referral
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int FriendAgentUserId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string FriendName { get; set; } = string.Empty;
    public string FriendBusiness { get; set; } = string.Empty;
    public string FriendEmail { get; set; } = string.Empty;

    // What was promised at sign-up; later changes to the program's amounts never touch these.
    public decimal GiftAmount { get; set; }
    public decimal RewardAmount { get; set; }

    public string Stage { get; set; } = ReferralStages.SignedUp;
    public DateTime SignedUpAt { get; set; } = DateTime.UtcNow;

    // The friend's gift as priced into their checkout (setup fee first, then the first and second cycle).
    public int? GiftBillingId { get; set; }
    public decimal GiftSetupDiscount { get; set; }
    public decimal GiftCycle1Discount { get; set; }
    public decimal GiftCycle2Discount { get; set; }

    public DateTime? JoinedAt { get; set; }
    public BillingPeriod? FriendPeriod { get; set; }
    public DateTime? ExpectedEarnAt { get; set; }
    public DateTime? JoinedEmailSentAt { get; set; }
    public DateTime? PayerCheckedAt { get; set; }

    public DateTime? EarnedAt { get; set; }
    public decimal RewardNet { get; set; }
    public decimal RewardTax { get; set; }
    public decimal RewardGross { get; set; }
    public decimal RewardTaxRate { get; set; }
    public string RewardTaxRegion { get; set; } = string.Empty;
    // Where the refund goes: "<sale id>=<amount>;..." across the referrer's own payments inside PayPal's window.
    public string RefundPlan { get; set; } = string.Empty;
    public DateTime? RefundWindowEndsAt { get; set; }

    public string RefundTransactionId { get; set; } = string.Empty;
    public DateTime? PaidAt { get; set; }
    public DateTime? PaidEmailSentAt { get; set; }
    public string CreditNoteNumber { get; set; } = string.Empty;

    public DateTime? ClosedAt { get; set; }          // not earned or voided
    public string ClosedReason { get; set; } = string.Empty;
    public string VoidedBy { get; set; } = string.Empty;

    // An exception for SuperAdmin's "Needs attention" count; empty when there is none.
    public string Attention { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// The PayPal plans a gift needs (discounted first cycles, then the regular price), made once per shape and reused.
public class ReferralPayPalPlan
{
    public int Id { get; set; }
    public int BillingRuleId { get; set; }
    public BillingPeriod Period { get; set; }
    public decimal Cycle1Price { get; set; }
    public decimal? Cycle2Price { get; set; }
    public decimal RegularPrice { get; set; }
    public string PayPalPlanId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
