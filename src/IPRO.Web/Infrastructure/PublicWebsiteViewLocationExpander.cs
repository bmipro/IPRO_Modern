using Microsoft.AspNetCore.Mvc.Razor;

namespace IPRO.Web.Infrastructure;

public class PublicWebsiteViewLocationExpander : IViewLocationExpander
{
    public void PopulateValues(ViewLocationExpanderContext context) { }

    public IEnumerable<string> ExpandViewLocations(ViewLocationExpanderContext context, IEnumerable<string> viewLocations) =>
        viewLocations.Concat(new[] { "/Views/PublicWebsite/{0}.cshtml" });
}
