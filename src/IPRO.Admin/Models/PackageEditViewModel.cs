using System.ComponentModel.DataAnnotations;

namespace IPRO.Admin.Models;

public class PackageEditViewModel
{
    public int Id { get; set; }

    [Required]
    public string PackageName { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Required]
    public decimal MonthlyPrice { get; set; }

    public decimal SetupFee { get; set; }
    public decimal? QuarterlyPrice { get; set; }
    public decimal? AnnualPrice { get; set; }
    public string? PayPalMonthlyPlanId { get; set; }
    public string? PayPalAnnualPlanId { get; set; }
    public int MaxClients { get; set; } = 500;
    public int? MaxNewsletters { get; set; } = 12;
    public int? DefaultWebsiteTemplateId { get; set; }
    public bool IsActive { get; set; } = true;
    public List<PackageFeatureEditViewModel> Features { get; set; } = new();
}

public class PackageFeatureEditViewModel
{
    public int Id { get; set; }
    public string FeatureCode { get; set; } = string.Empty;
    public string FeatureName { get; set; } = string.Empty;
    public bool IsIncluded { get; set; }
    public int? LimitValue { get; set; }
    public string? LimitLabel { get; set; }
    public int SortOrder { get; set; }
}

public class PackageListViewModel
{
    public int Id { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal MonthlyPrice { get; set; }
    public decimal AnnualPrice { get; set; }
    public decimal SetupFee { get; set; }
    public string ContactsLimit { get; set; } = string.Empty;
    public string DomainsLimit { get; set; } = string.Empty;
    public string DefaultWebsiteTemplateName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
