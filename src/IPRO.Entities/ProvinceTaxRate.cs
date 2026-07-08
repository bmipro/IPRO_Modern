namespace IPRO.Entities;

public class ProvinceTaxRate
{
    public int Id { get; set; }
    public string ProvinceCode { get; set; } = string.Empty;
    public string ProvinceName { get; set; } = string.Empty;
    public string TaxLabel { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
