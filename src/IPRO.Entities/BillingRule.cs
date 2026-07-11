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
    public string PayPalMonthlyPlanId { get; set; } = string.Empty;
    public string PayPalAnnualPlanId { get; set; } = string.Empty;
    public int MaxClients { get; set; } = 500;
    public int MaxNewsletters { get; set; } = 12;
    public int? DefaultWebsiteTemplateId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<PackageFeature> Features { get; set; } = new List<PackageFeature>();
}
