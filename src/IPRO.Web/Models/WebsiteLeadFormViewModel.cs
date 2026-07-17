using System.ComponentModel.DataAnnotations;

namespace IPRO.Web.Models;

public class WebsiteLeadFormViewModel
{
    public int? PageId { get; set; }
    public string SubmissionType { get; set; } = IPRO.Entities.WebsiteLeadTypes.Contact;
    public string ReturnPath { get; set; } = "/";

    [Required, StringLength(80)]
    public string FirstName { get; set; } = string.Empty;

    [StringLength(80)]
    public string? LastName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(200)]
    public string Email { get; set; } = string.Empty;

    [StringLength(40)]
    public string? Phone { get; set; } = string.Empty;

    [StringLength(4000)]
    public string? Message { get; set; } = string.Empty;

    public bool ConsentGiven { get; set; }

    public string? HoneypotField { get; set; } = string.Empty;
    public long FormStartedAt { get; set; }
    public string? CaptchaToken { get; set; } = string.Empty;
    public string? CaptchaAnswer { get; set; } = string.Empty;
}
