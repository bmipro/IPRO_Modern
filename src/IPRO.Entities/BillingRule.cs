namespace IPRO.Entities;

public class BillingRule
{
    public int Id { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal MonthlyPrice { get; set; }
    public decimal QuarterlyPrice { get; set; }
    public decimal AnnualPrice { get; set; }
    public decimal SetupFee { get; set; }
    // Setup-fee waiver, controlled per package from Super Admin -> Packages -> Edit. Set on the
    // packages you want people to choose (e.g. waive on Gold and Platinum but not Silver, so the
    // saving is a reason to move up a tier). SetupFeeWaivedUntil is optional: null means the waiver
    // runs until it is switched off, otherwise it lapses on its own at that instant.
    public bool SetupFeeWaived { get; set; }
    public DateTime? SetupFeeWaivedUntil { get; set; }
    public string PayPalMonthlyPlanId { get; set; } = string.Empty;
    public string PayPalAnnualPlanId { get; set; } = string.Empty;
    public int MaxClients { get; set; } = 500;
    public int MaxNewsletters { get; set; } = 12;
    public int? DefaultWebsiteTemplateId { get; set; }
    public bool IsActive { get; set; } = true;
    // Excluded from every agent-facing package list (public signup picker, upgrade/downgrade
    // picker) but still selectable by a direct billingRuleId POST -- used for QA-only sandbox
    // packages (e.g. daily-billing test plans) that a real visitor must never be able to browse to.
    public bool IsHiddenTestPackage { get; set; }
    public bool IsTrialPackage { get; set; }
    public int? TrialDurationDays { get; set; }
    // Comma-separated days-before-expiry to send a reminder, e.g. "7,3,1". Only meaningful when IsTrialPackage.
    public string? TrialReminderDayOffsets { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<PackageFeature> Features { get; set; } = new List<PackageFeature>();

    // The ONE definition of whether the setup fee is currently waived. The public pricing page, the
    // registration screen and PayPalBillingService all call this -- deliberately, so the advertised
    // fee and the charged fee cannot drift apart. Never re-implement this test at a call site.
    public bool IsSetupFeeWaivedOn(DateTime utcNow) =>
        SetupFeeWaived && (SetupFeeWaivedUntil == null || SetupFeeWaivedUntil.Value > utcNow);

    // What the customer is actually charged up front, once the waiver is applied.
    public decimal EffectiveSetupFee(DateTime utcNow) =>
        IsSetupFeeWaivedOn(utcNow) ? 0m : SetupFee;
}
