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

    [Range(1, int.MaxValue, ErrorMessage = "Package is required.")]
    public int PackageId { get; set; }

    // Self-signups choose their own password at registration (signup v2, 2026-08-13). This replaced
    // the temp-password ceremony: the account is created with this password, the welcome email
    // carries no credentials, and the agent goes straight from Register into PayPal checkout.
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;

    // "Monthly" or "Annually" -- chosen alongside the plan so checkout can begin immediately after
    // registration instead of re-asking on the Billing page.
    public string BillingPeriodChoice { get; set; } = "Monthly";

    // True when the visitor arrived from a pricing card (?package=): the form shows a locked plan
    // summary instead of the dropdown, and keeps doing so across validation-failure re-renders.
    public bool PlanLocked { get; set; }

    public string? PromotionCode { get; set; }

    // Set only when arriving via an invitation link (?trialCode=...); claims a trial package.
    public string? TrialCode { get; set; }
}
