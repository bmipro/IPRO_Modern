using IPRO.Entities;

namespace IPRO.Admin.Models;

public class WebsiteTemplateUsageViewModel
{
    public WebsiteTemplate Template { get; set; } = new();
    public int WebsiteCount { get; set; }
    public int PublishedWebsiteCount { get; set; }
    public int PackageDefaultCount { get; set; }
    public List<string> AgentNames { get; set; } = new();
    public List<string> PackageNames { get; set; } = new();

    public bool IsInUse => WebsiteCount > 0 || PackageDefaultCount > 0;
}
