using System;
using System.IO;
using Xunit;

namespace IPRO.IntegrationTests;

// 611 (found 2026-10-28 in the owner's 585 try). A staging adviser's addresses are on production's domain
// (<name>.247advisers.com), so "Open Website" on staging's My Website opened the LIVE server. On staging it
// now opens the portal's own preview of the site's home page, and the domain guide does not link the
// temporary address; production is unchanged.
public class StagingOpenWebsite611Tests
{
    [Fact]
    public void My_Website_opens_the_portals_preview_on_staging_and_the_public_address_elsewhere()
    {
        var view = Read(@"src\IPRO.Web\Views\Website\Index.cshtml");
        var staging = view.IndexOf("    @if (isStaging)\n", StringComparison.Ordinal);
        var preview = view.IndexOf("<a href=\"/portal/WebsitePages/Preview/@previewPageId\" target=\"_blank\"", StringComparison.Ordinal);
        var live = view.IndexOf("    else if (!string.IsNullOrWhiteSpace(liveDomain))\n    {\n      <a href=\"https://@liveDomain\" target=\"_blank\"", StringComparison.Ordinal);
        Assert.True(staging > 0 && preview > staging && live > preview, "staging's preview link must come first, the public address only otherwise");
        Assert.Contains("Open Website (preview)</a>", view);
        Assert.Contains("    var isStaging = ViewBag.IsStaging == true;\n", view);
    }

    [Fact]
    public void The_controller_says_whether_this_is_staging_and_which_page_to_preview()
    {
        var controller = Read(@"src\IPRO.Web\Controllers\WebsiteController.cs");
        Assert.Contains("        var isStaging = StagingGuard.IsStaging(_configuration);\n        ViewBag.IsStaging = isStaging;\n", controller);
        Assert.Contains(".Where(p => p.AgentWebsite.AgentUserId == AgentId)\n                .OrderByDescending(p => p.IsHomePage)", controller);
    }

    [Fact]
    public void The_domain_guide_names_the_temporary_address_without_a_link_on_staging()
    {
        var guide = Read(@"src\IPRO.Web\Views\Website\_DomainGuide.cshtml");
        Assert.Contains("Your website stays live at @if (ViewBag.IsStaging == true) {<span class=\"font-monospace\">@Model.TemporaryDomain</span>} else {<a href=\"https://@Model.TemporaryDomain\" target=\"_blank\" rel=\"noopener\">@Model.TemporaryDomain</a>} the whole time.", guide);
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
