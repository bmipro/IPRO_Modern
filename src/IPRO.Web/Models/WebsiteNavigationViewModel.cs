using IPRO.Entities;

namespace IPRO.Web.Models;

public class WebsiteNavigationViewModel
{
    public AgentWebsite Website { get; set; } = null!;
    public WebsiteHeaderSettings Header { get; set; } = new();
    public List<WebsitePage> Pages { get; set; } = new();
    // 528: whether the adviser's package includes the announcement bar (null = not looked up).
    public IPRO.Business.Interfaces.PackageFeatureAccess? AnnouncementAccess { get; set; }
    // 529: whether the package includes the pop-up, and the pictures of the website's media library it may use.
    public IPRO.Business.Interfaces.PackageFeatureAccess? PopupAccess { get; set; }
    public List<WebsiteMediaAsset> Pictures { get; set; } = new();
}
