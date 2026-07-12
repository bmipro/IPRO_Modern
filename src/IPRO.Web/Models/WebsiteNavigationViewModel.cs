using IPRO.Entities;

namespace IPRO.Web.Models;

public class WebsiteNavigationViewModel
{
    public AgentWebsite Website { get; set; } = null!;
    public WebsiteHeaderSettings Header { get; set; } = new();
    public List<WebsitePage> Pages { get; set; } = new();
}
