using IPRO.Entities;

namespace IPRO.Web.Models;

public class PublicWebsiteViewModel
{
    public AgentWebsite Website { get; set; } = null!;
    public List<WebsitePage> Pages { get; set; } = new();
    public WebsitePage? CurrentPage { get; set; }
}
