using System.ComponentModel.DataAnnotations;

namespace IPRO.Web.Models;

public class PortalProfileViewModel
{
    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    // 678 (audit 2026-11-24 A-2): needed only to change the email, which is the portal sign-in.
    public string? CurrentPassword { get; set; }

    public string? Phone { get; set; }
    public string? CellPhone { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
}
