using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using IPRO.Web.Infrastructure;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 468 (2026-09-09). The owner: a help icon at the top right of every portal page that opens
// the guide for that page. The guides and their section anchors already exist (461, 466); the icon
// only needs to know which page it is on. HelpLinks is that map. These tests make it complete and
// honest: every agent-facing screen has a target, every target exists, every anchor lands.
public class HelpLinksTests
{
    // Controllers that are not agent-portal screens: the client's side of the portal, the public
    // website, sign-in, webhooks, status pages, prospect previews, token-link endpoints.
    private static readonly HashSet<string> NotAgentScreens = new(StringComparer.OrdinalIgnoreCase)
    {
        "AzureEmailEvents", "ClientDocument", "ClientPortal", "ClientPortalAccount", "ClientPortalAppointments",
        "ClientPortalDocuments", "ClientPortalMessages", "ClientPortalPreferences", "ClientPortalProfile",
        "EmailPreferences", "Error", "Home", "PollVote", "Preview", "PublicWebsite", "TestimonialRequest",
    };

    [Fact]
    public void Every_agent_facing_screen_has_a_help_target()
    {
        var controllersDir = FindRepoFile(@"src\IPRO.Web\Controllers");
        var screens = Directory.GetFiles(controllersDir, "*Controller.cs")
            .Select(f => Path.GetFileName(f).Replace("Controller.cs", ""))
            .Where(c => !NotAgentScreens.Contains(c))
            .OrderBy(c => c)
            .ToList();
        Assert.NotEmpty(screens);

        var unmapped = screens.Where(c => !HelpLinks.HasTarget(c)).ToList();
        Assert.True(unmapped.Count == 0,
            "Agent-facing screens with no help target (add them to HelpLinks, or to NotAgentScreens here if they are not screens):\n  "
            + string.Join("\n  ", unmapped));
    }

    [Fact]
    public void Every_target_points_at_a_guide_that_exists_and_a_section_that_exists()
    {
        foreach (var (controller, action, link) in HelpLinks.All())
        {
            if (link.Url == HelpLinks.IndexUrl) continue;

            var m = Regex.Match(link.Url, @"^/portal/Support/Article/([a-z0-9-]+)(?:#([a-z0-9-]+))?$");
            Assert.True(m.Success, $"{controller}/{action}: '{link.Url}' is not a help-article URL");

            var slug = m.Groups[1].Value;
            var article = HelpDocsService.FindArticle(slug);
            Assert.True(article != null, $"{controller}/{action}: no help article with slug '{slug}'");

            if (m.Groups[2].Success)
            {
                var html = HelpDocsService.GetArticleHtml(slug) ?? string.Empty;
                Assert.True(html.Contains($"id=\"{m.Groups[2].Value}\"", StringComparison.Ordinal),
                    $"{controller}/{action}: '{article!.Title}' has no section with id '{m.Groups[2].Value}'");
            }
            Assert.False(string.IsNullOrWhiteSpace(link.Label));
        }
    }

    [Fact]
    public void An_action_can_refine_the_target_and_an_unknown_page_falls_back_to_the_index()
    {
        // The page editor's menu screen opens the menu section, not the top of the guide.
        Assert.Contains("#add-a-page-to-the-menu", HelpLinks.For("WebsitePages", "Navigation").Url);
        // Any other page-editor action opens the guide at its page section.
        Assert.StartsWith("/portal/Support/Article/website-builder", HelpLinks.For("WebsitePages", "Edit").Url);
        // Something nobody mapped opens the Support index rather than a broken link.
        Assert.Equal(HelpLinks.IndexUrl, HelpLinks.For("NoSuchScreen", "Index").Url);
        Assert.Equal(HelpLinks.IndexUrl, HelpLinks.For(null, null).Url);
        // Case does not matter: routing hands us "clients" as often as "Clients".
        Assert.Equal(HelpLinks.For("Clients", "Index").Url, HelpLinks.For("clients", "index").Url);
    }

    [Fact]
    public void The_portal_layout_shows_the_icon_on_every_page()
    {
        var layout = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Shared\_Layout.cshtml"));
        Assert.Contains("HelpLinks.For(", layout);
        var icon = Regex.Match(layout, @"<a[^>]*class=""[^""]*help-for-page[^""]*""[^>]*>", RegexOptions.Singleline);
        Assert.True(icon.Success, "the topbar must carry the help-for-page link");
        Assert.Contains("target=\"_blank\"", icon.Value);
        Assert.Contains("rel=\"noopener\"", icon.Value);
        Assert.Contains("aria-label=", icon.Value);
        // It sits in the topbar, which is rendered for every signed-in agent page.
        Assert.True(layout.IndexOf("agent-topbar", StringComparison.Ordinal) < icon.Index);
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
