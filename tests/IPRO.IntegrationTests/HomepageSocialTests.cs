using System;
using System.IO;
using Xunit;

namespace IPRO.IntegrationTests;

// Owner request 2026-08-28: the company's Facebook and YouTube profiles belong on the marketing
// homepage. Source-walk pin (the invoice-print-button pattern) so the links never silently
// vanish in a homepage rework -- the footer is one dense line and easy to lose in a merge.
public class HomepageSocialTests
{
    [Fact]
    public void Homepage_footer_links_facebook_and_youtube()
    {
        var razor = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));

        Assert.Contains("https://www.facebook.com/p/iPRO-100071151034796/", razor);
        // @@ is Razor's escape for a literal @ -- the page renders youtube.com/@AllAdvisers.
        Assert.Contains("https://www.youtube.com/@@AllAdvisers", razor);

        // Both open in a new tab without handing the homepage a window.opener.
        var footerStart = razor.IndexOf("class=\"i2-footer-social\"", StringComparison.Ordinal);
        Assert.True(footerStart >= 0, "the social row is gone from the footer");
        var block = razor.Substring(footerStart, Math.Min(1600, razor.Length - footerStart));
        Assert.Equal(2, CountOf(block, "rel=\"noopener\""));
        Assert.Equal(2, CountOf(block, "target=\"_blank\""));
        Assert.Contains("aria-label=\"IPRO Advisers on Facebook\"", block);
        Assert.Contains("aria-label=\"IPRO Advisers on YouTube\"", block);
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) count++;
        return count;
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
