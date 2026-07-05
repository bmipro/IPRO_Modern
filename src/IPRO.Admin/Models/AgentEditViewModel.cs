namespace IPRO.Admin.Models;

public class AgentEditViewModel
{
    public int Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string CompanyAddress { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Province { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string BusinessFax { get; set; } = string.Empty;
    public string CellPhone { get; set; } = string.Empty;
    public string BusinessType { get; set; } = string.Empty;
    public string DomainName { get; set; } = string.Empty;
    public int PackageId { get; set; }
    public string PromotionCode { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool MustChangePassword { get; set; }
}
