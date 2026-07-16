using IPRO.Entities;

namespace IPRO.Web.Models;

public class WebsiteFooterViewModel
{
    public AgentWebsite Website { get; set; } = null!;
    public WebsiteFooterSettings Footer { get; set; } = new();
}
