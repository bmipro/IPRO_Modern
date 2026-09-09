using System;
using System.IO;
using System.Linq;
using IPRO.Web.Infrastructure;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 461 (2026-09-02). Four agent-facing guides sat on disk for weeks and were never listed in
// the in-app Help Documentation: nothing tied "a DOCS file exists" to "the agent can read it".
// Now: every article in the index must load from the embedded resources, and every agent-facing
// guide on disk must be in the index. Adding a guide means adding it here, or this fails.
public class HelpDocsTests
{
    // The agent-facing set. Owner/ops documents (07 SuperAdmin, 08 registration internals,
    // 09 troubleshooting, 14 release checklist, 16 local dev, 18 Azure, 20 certificates,
    // 22 prepaid-value policy) are deliberately not here.
    private static readonly string[] AgentFacingGuides =
    {
        "01_AGENT_ACCOUNT_AND_DASHBOARD.md", "02_CLIENTS_AND_FOLLOWUPS.md", "03_NEWSLETTERS_AND_CAMPAIGNS.md",
        "04_WEBSITE_BUILDER.md", "05_DOMAINS_AND_LEADS.md", "06_BILLING_AND_INVOICES.md",
        "10_CLIENT_INVOICING.md", "11_CLIENT_PORTAL.md", "12_AGENT_DOCUMENT_LIBRARY.md",
        "13_SOCIAL_MEDIA_POSTS.md", "15_TESTIMONIALS.md", "16_POLLS_AND_SURVEYS.md",
        "17_FORMS.md", "18_ECARDS.md", "19_ELETTERS.md", "21_AGENT_GOOGLE_VISIBILITY.md",
        // 461 (second half): the eight guides written 2026-09-02.
        "23_EMAIL_ACTIVITY.md", "24_MARKETING_CALENDAR.md", "25_AI_DAILY_ASSISTANT.md", "26_DID_YOU_KNOW.md",
        "27_SUPPORT_TICKETS.md", "28_CALENDAR_AND_GOOGLE_CALENDAR.md", "29_ARTICLES.md", "30_IMAGE_LIBRARY.md",
        // 469: the gap the help-icon map surfaced.
        "31_TEAM_MEMBERS.md",
    };

    [Fact]
    public void Every_agent_facing_guide_on_disk_is_in_the_help_index()
    {
        var listed = HelpDocsService.GetArticles().Select(a => a.ResourceFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = AgentFacingGuides.Where(g => !listed.Contains(g)).ToList();
        Assert.True(missing.Count == 0, "Guides written but not listed in HelpDocsService:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void Every_listed_article_exists_on_disk_and_loads_from_the_embedded_resources()
    {
        foreach (var article in HelpDocsService.GetArticles())
        {
            Assert.True(File.Exists(FindRepoFile(Path.Combine("DOCS", article.ResourceFileName))),
                $"{article.Slug}: DOCS/{article.ResourceFileName} does not exist");

            var html = HelpDocsService.GetArticleHtml(article.Slug);
            Assert.False(string.IsNullOrWhiteSpace(html), $"{article.Slug}: no HTML");
            Assert.DoesNotContain("could not be loaded", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<h1", html!, StringComparison.OrdinalIgnoreCase);
        }
    }

    // TODO 466 (2026-09-08). The owner could not find the new "Social Links Block" section: it is
    // the 24th heading of the longest guide and the article page rendered the whole guide with no
    // way to see its shape. Every article now opens with "In this guide", a list of links to its
    // sections, whenever it has three or more.

    [Fact]
    public void A_guide_with_three_or_more_sections_opens_with_a_list_of_them()
    {
        const string md = "# Title\n\nIntro.\n\n## First Thing\n\ntext\n\n## Second Thing\n\ntext\n\n## Social Links Block\n\ntext\n";
        var html = HelpDocsService.RenderArticle(md);

        // The list sits after the title and before the first section...
        var toc = html.IndexOf("class=\"help-toc\"", StringComparison.Ordinal);
        var firstH2 = html.IndexOf("<h2", StringComparison.Ordinal);
        Assert.True(toc >= 0, "no table of contents rendered");
        Assert.True(html.IndexOf("</h1>", StringComparison.Ordinal) < toc && toc < firstH2, "the list must sit between the title and the first section");

        // ...and every link lands on the section's own heading.
        Assert.Contains("href=\"#social-links-block\">Social Links Block</a>", html);
        Assert.Contains("<h2 id=\"social-links-block\">", html);
        Assert.Contains("href=\"#first-thing\">First Thing</a>", html);
        Assert.Contains("In this guide", html);
    }

    [Fact]
    public void A_short_guide_gets_no_list()
    {
        const string md = "# Title\n\n## Only One\n\ntext\n\n## And Two\n\ntext\n";
        var html = HelpDocsService.RenderArticle(md);
        Assert.DoesNotContain("help-toc", html);
        Assert.Contains("<h2 id=\"only-one\">", html); // headings still get their anchors
    }

    [Fact]
    public void The_website_builder_guide_lists_the_social_links_section()
    {
        var html = HelpDocsService.GetArticleHtml("website-builder");
        Assert.NotNull(html);
        Assert.Contains("class=\"help-toc\"", html!);
        Assert.Contains("href=\"#social-links-block\">Social Links Block</a>", html);
    }

    [Fact]
    public void Slugs_are_unique_and_url_safe()
    {
        var slugs = HelpDocsService.GetArticles().Select(a => a.Slug).ToList();
        Assert.Equal(slugs.Count, slugs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(slugs, s => Assert.Matches("^[a-z0-9-]+$", s));
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
