using System;
using System.IO;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 459 (2026-09-02) folded the registrar steps away once the adviser's PRIMARY domain was fully connected ("the
// information is there but not as taking half the page"). TODO 595 (2026-10-21) replaced them: the owner added a
// second domain for a customer that day and the new domain's CNAME, A and asuid records were folded away because the
// first one was done. Each domain now has its own guide (DomainGuide595Tests); what 459 asked for still holds per
// domain -- a live domain's guide is folded, one click away, and a domain being set up has its guide open.
public class DomainSetupPanelTests
{
    [Fact]
    public void Each_domain_has_its_own_guide_open_while_it_is_set_up_and_folded_once_it_is_live()
    {
        // 595: on the Domain page (Website/Domains); My Website keeps a summary card.
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Website\Domains.cshtml")).Replace("\r\n", "\n");
        Assert.Contains("var guideOpen = openDomainGuide == domain.Id || (openDomainGuide == null && !guide.Live && guide.Refusal == null);", view);
        Assert.Contains("@await Html.PartialAsync(\"_DomainGuide\", new IPRO.Web.Models.DomainGuideViewModel(domain, guide, guideOpen, openDomainGuide == domain.Id, guideSettings, temporaryDomain))", view);
        // The way back to a folded guide, worded for a live domain.
        Assert.Contains("data-guide-toggle=\"@domain.Id\" aria-controls=\"guide-@domain.Id\"", view);
        Assert.Contains("@(guide.Live ? \"Setup guide\" : \"Guide\")", view);

        // Nothing about one domain hides another's steps any more.
        Assert.DoesNotContain("registrarSteps", view);
        Assert.DoesNotContain("Show the setup steps", view);
        Assert.DoesNotContain("DomainSetupState", view);
        Assert.Contains("hidden=\"@(!Model.Open)\"", File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Website\_DomainGuide.cshtml")));
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
