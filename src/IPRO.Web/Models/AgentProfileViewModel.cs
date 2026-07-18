using System.ComponentModel.DataAnnotations;

namespace IPRO.Web.Models;

public class AgentProfileViewModel
{
    public int Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string DomainName { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;

    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    public string? Designation { get; set; }

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string CompanyName { get; set; } = string.Empty;

    public string? CompanyAddress { get; set; }

    [Required]
    public string City { get; set; } = string.Empty;

    [Required]
    public string Province { get; set; } = string.Empty;

    [Required]
    public string PostalCode { get; set; } = string.Empty;

    [Required]
    public string Country { get; set; } = string.Empty;

    public string? TimeZone { get; set; }

    [Required]
    public string Phone { get; set; } = string.Empty;

    public string? BusinessFax { get; set; }
    public string? CellPhone { get; set; }

    [Required]
    public string BusinessType { get; set; } = string.Empty;

    public string? PromotionCode { get; set; }
    public string? DefaultPaymentLink { get; set; }
}
