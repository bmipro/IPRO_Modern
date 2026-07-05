namespace IPRO.Web.Models;

public class RegistrationWelcomeModel
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string TemporaryPassword { get; set; } = string.Empty;
    public string SetupDomain { get; set; } = string.Empty;
    public string TrainingEmail { get; set; } = "training@IProAdvisers.com";
    public string WebsiteUrl { get; set; } = "www.iProAdvisers.com";
}
