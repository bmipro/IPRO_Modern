using IPRO.Entities;

namespace IPRO.Admin.Models;

public class PayPalSetupViewModel
{
    public bool IsSandbox { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string ClientIdPreview { get; set; } = string.Empty;
    public bool HasClientId { get; set; }
    public bool HasClientSecret { get; set; }
    public string WebhookIdPreview { get; set; } = string.Empty;
    public bool HasWebhookId { get; set; }
    public string ReturnUrl { get; set; } = string.Empty;
    public string CancelUrl { get; set; } = string.Empty;
    public string ExpectedWebhookUrl { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = string.Empty;
    public int ActivePackageCount { get; set; }
    public int MissingMonthlyPlanCount { get; set; }
    public int MissingAnnualPlanCount { get; set; }
    public List<PayPalPackagePlanStatusViewModel> Packages { get; set; } = new();
    public List<PayPalSettingStatusViewModel> Settings { get; set; } = new();
}

public class PayPalSettingStatusViewModel
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsConfigured { get; set; }
    public string HelpText { get; set; } = string.Empty;
}

public class PayPalPackagePlanStatusViewModel
{
    public int Id { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public decimal MonthlyPrice { get; set; }
    public decimal AnnualPrice { get; set; }
    public decimal SetupFee { get; set; }
    public string MonthlyPlanId { get; set; } = string.Empty;
    public string AnnualPlanId { get; set; } = string.Empty;
    public bool NeedsMonthlyPlan => IsActive && MonthlyPrice > 0 && string.IsNullOrWhiteSpace(MonthlyPlanId);
    public bool NeedsAnnualPlan => IsActive && AnnualPrice > 0 && string.IsNullOrWhiteSpace(AnnualPlanId);

    public static PayPalPackagePlanStatusViewModel FromRule(BillingRule rule) => new()
    {
        Id = rule.Id,
        PackageName = rule.PackageName,
        IsActive = rule.IsActive,
        MonthlyPrice = rule.MonthlyPrice,
        AnnualPrice = rule.AnnualPrice,
        SetupFee = rule.SetupFee,
        MonthlyPlanId = rule.PayPalMonthlyPlanId,
        AnnualPlanId = rule.PayPalAnnualPlanId
    };
}
