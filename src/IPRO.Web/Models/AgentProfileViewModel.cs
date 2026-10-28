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

    // 570: printed on the business's invoices and estimates. Free text: businesses write it in more
    // than one way, and some carry a second number (Quebec's QST) beside it.
    [StringLength(60)]
    public string? TaxRegistrationNumber { get; set; }

    [Required]
    public string City { get; set; } = string.Empty;

    [Required]
    public string Province { get; set; } = string.Empty;

    [Required]
    public string PostalCode { get; set; } = string.Empty;

    [Required]
    public string Country { get; set; } = string.Empty;

    public string? TimeZone { get; set; }

    // 498: "Email me my follow-ups each morning" (AgentFollowUpReminder; no row means on).
    public bool FollowUpReminderEmails { get; set; } = true;

    [Required]
    public string Phone { get; set; } = string.Empty;

    public string? BusinessFax { get; set; }
    public string? CellPhone { get; set; }

    [Required]
    public string BusinessType { get; set; } = string.Empty;

    public string? PromotionCode { get; set; }
    public string? DefaultPaymentLink { get; set; }
    public string? PhotoUrl { get; set; }

    // 593: needed only to change the login email (the address password resets and emailed codes go to).
    public string? CurrentPassword { get; set; }
    public string? TwoStepCode { get; set; }
}
