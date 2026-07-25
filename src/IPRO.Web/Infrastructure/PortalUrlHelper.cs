using IPRO.Utility;

namespace IPRO.Web.Infrastructure;

public static class PortalUrlHelper
{
    public static string GetAgentPortalBaseUrl(IConfiguration configuration) =>
        WebAppUrlHelper.GetWebAppBaseUrl(configuration);
}
