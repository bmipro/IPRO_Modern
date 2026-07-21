namespace IPRO.Web.Models;

public class PortalPreferencesViewModel
{
    public bool IsNewsletterSubscribed { get; set; }
    public List<IPRO.Entities.DripCampaignEnrollment> Enrollments { get; set; } = new();
}
