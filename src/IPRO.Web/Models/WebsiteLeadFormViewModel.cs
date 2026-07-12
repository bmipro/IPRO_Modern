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
    public string LastName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(200)]
    public string Email { get; set; } = string.Empty;

    [StringLength(40)]
    public string Phone { get; set; } = string.Empty;

    [StringLength(4000)]
    public string Message { get; set; } = string.Empty;

    [Range(typeof(bool), "true", "true", ErrorMessage = "Please confirm that we may use your information to respond.")]
    public bool ConsentGiven { get; set; }

    public string Website { get; set; } = string.Empty;
    public long FormStartedAt { get; set; }
}
