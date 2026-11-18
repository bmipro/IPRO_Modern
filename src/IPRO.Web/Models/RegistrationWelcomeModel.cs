namespace IPRO.Web.Models;

public class RegistrationWelcomeModel
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string TemporaryPassword { get; set; } = string.Empty;
    public string SetupDomain { get; set; } = string.Empty;
    public string TrainingEmail { get; set; } = "training@iProAdvisers.com";
    public string WebsiteUrl { get; set; } = "www.iProAdvisers.com";
    // 646: they keep their own website: the email speaks of the contact form for it, not of a temporary site.
    public bool KeepsOwnWebsite { get; set; }
    public string PortalSignInUrl { get; set; } = "https://app.iProAdvisers.com/Account/Login";
}
