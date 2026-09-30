using System;
using System.IO;
using IPRO.Email;
using Xunit;

namespace IPRO.IntegrationTests;

// 526 (2026-09-27): the two spots 525 left out -- "Powered by iPro" under every client-portal page,
// and "Sent with iPro" in the newsletter footer (which the drip campaign emails share). Both go
// through PoweredBy, the one seam a white-label switch will later close.
public class PoweredBy526Tests
{
    [Fact]
    public void The_two_spots_are_wired()
    {
        var layout = Read(@"src\IPRO.Web\Views\Shared\_ClientPortalLayout.cshtml");
        Assert.Contains("id=\"portal-footer\"", layout);
        Assert.Contains("PoweredBy.BrandUrl(", layout);

        // 533: the newsletter's footer is SenderFooter's now; the brand line lives there.
        Assert.Contains("SenderFooter.", Read(@"src\IPRO.Email\NewsLetterDispatcher.cs"));
        var footer = Read(@"src\IPRO.Entities\SenderFooter.cs");
        Assert.Contains("Sent with", footer);
        Assert.Contains("PoweredBy.BrandUrl(", footer);
    }

    [Fact]
    public void The_newsletter_footer_keeps_its_unsubscribe_line_and_adds_the_brand()
    {
        var html = NewsLetterDispatcher.AppendUnsubscribeHtml("<p>Body</p>", "https://x.test/Newsletter/Unsubscribe?token=a&b", new IPRO.Entities.AgentUser { BusinessType = "Accountants" });

        Assert.StartsWith("<p>Body</p>", html);
        Assert.Contains("Unsubscribe from future newsletters", html);
        Assert.Contains("href=\"https://x.test/Newsletter/Unsubscribe?token=a&amp;b\"", html);   // still encoded
        Assert.Contains("Sent with <a href=\"https://www.iproaccountants.com/\"", html);
        Assert.Contains(">iPro</a>", html);

        var generic = NewsLetterDispatcher.AppendUnsubscribeHtml("<p>Body</p>", "https://x.test/u", null);
        Assert.Contains("https://www.iproadvisers.com/", generic);

        // No business on file: iPro still says it sent it, and names no one it sent it for.
        var text = NewsLetterDispatcher.AppendUnsubscribeText("Hello", "https://x.test/u", new IPRO.Entities.AgentUser { BusinessType = "Mortgage" });
        Assert.Contains("Unsubscribe from future newsletters:", text);
        Assert.Contains("Sent with iPro: https://www.ipromortgages.com/", text);
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
