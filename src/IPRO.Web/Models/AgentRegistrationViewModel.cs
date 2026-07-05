using System.ComponentModel.DataAnnotations;

namespace IPRO.Web.Models;

public class AgentRegistrationViewModel
{
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
    public string Province { get; set; } = "Alberta";

    [Required]
    public string PostalCode { get; set; } = string.Empty;

    [Required]
    public string Country { get; set; } = "Canada";

    public string? TimeZone { get; set; } = "(GMT-05:00) Eastern Time (US & Canada)";

    [Required]
    public string Phone { get; set; } = string.Empty;

    public string? BusinessFax { get; set; }

    public string? CellPhone { get; set; }

    [Required]
    public string BusinessType { get; set; } = string.Empty;

    [Range(2, int.MaxValue, ErrorMessage = "Package is required.")]
    public int PackageId { get; set; }

    public string? PromotionCode { get; set; }
}
