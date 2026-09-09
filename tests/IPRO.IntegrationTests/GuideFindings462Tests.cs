using System;
using System.IO;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 462 (a), (d), (e) (2026-09-09): the last three small findings from writing the guides.
//
// (a) The Did You Know block editor's help text said a visitor "sees the full content of every
//     checked article immediately". The build does not do that: the submission queues one email
//     per article, a few minutes apart, drained by DidYouKnowEmailDispatchJob. The sentence now
//     says what happens.
// (d) Delete on the Articles list was permanent and asked no confirmation. It now uses the
//     js-confirm-submit pattern every other destructive portal button uses (CSP drops inline
//     onsubmit=confirm()).
// (e) DOCS/04 says the Blog block is Platinum and Broker. The code agrees -- adding a Blog block
//     is gated on the ManagedBlog entitlement at the POST, not only in the picker -- so this pin
//     keeps the doc and the gate from drifting apart. It was green from the start; (a) and (d)
//     were observed red.
public class GuideFindings462Tests
{
    [Fact]
    public void The_did_you_know_help_text_says_the_articles_are_emailed()
    {
        var editor = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\WebsitePages\Edit.cshtml"));
        Assert.DoesNotContain("sees the full content of every checked article immediately", editor);
        Assert.Contains("is emailed the full content of every checked article", editor);
    }

    [Fact]
    public void Deleting_an_article_asks_for_confirmation()
    {
        var list = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Articles\Index.cshtml"));
        var deleteForm = list.IndexOf("asp-action=\"Delete\"", StringComparison.Ordinal);
        Assert.True(deleteForm >= 0, "the Delete form was not found on the Articles list");
        var button = list.IndexOf("<button", deleteForm, StringComparison.Ordinal);
        var buttonEnd = list.IndexOf(">", button, StringComparison.Ordinal);
        var tag = list[button..buttonEnd];
        Assert.Contains("js-confirm-submit", tag);
        Assert.Contains("data-confirm-message=", tag);
    }

    [Fact]
    public void The_blog_block_is_gated_on_the_entitlement_the_guide_names()
    {
        var controller = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\WebsitePagesController.cs"));
        var blogGate = controller.IndexOf("if (blockType == WebsiteBlockTypes.Blog)", StringComparison.Ordinal);
        Assert.True(blogGate >= 0, "the Blog block gate was not found in the add-block action");
        Assert.Contains("PackageFeatureCodes.ManagedBlog", controller[blogGate..(blogGate + 400)]);

        var guide = File.ReadAllText(FindRepoFile(@"DOCS\04_WEBSITE_BUILDER.md"));
        Assert.Contains("The **Blog** block (Platinum and Broker packages)", guide);
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
