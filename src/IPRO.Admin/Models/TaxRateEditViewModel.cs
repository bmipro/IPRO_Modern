using System.ComponentModel.DataAnnotations;

namespace IPRO.Admin.Models;

public class TaxRateEditViewModel
{
    public List<TaxRateRowViewModel> Rates { get; set; } = new();
}

public class TaxRateRowViewModel
{
    public int Id { get; set; }

    [Required]
    public string ProvinceCode { get; set; } = string.Empty;

    [Required]
    public string ProvinceName { get; set; } = string.Empty;

    [Required]
    public string TaxLabel { get; set; } = string.Empty;

    [Range(0, 100)]
    public decimal RatePercent { get; set; }

    public bool IsActive { get; set; } = true;
}
