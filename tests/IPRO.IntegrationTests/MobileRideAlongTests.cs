using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace IPRO.IntegrationTests;

// Mobile-readiness sweep items 6-11 (2026-08-31) -- the cheap ride-alongs shipped after the five
// task-blockers. Individually small; collectively the difference between "works on a phone" and
// "pleasant on a phone". Every test observed RED against the pre-fix views.
public class MobileRideAlongTests
{
    // ---- 6. a long word must not put a published agent site into horizontal scroll -----------
    //
    // The three managed-page renderers set fixed hero h1 sizes (64/52/76px desktop, 44/40/48px
    // mobile) with no overflow-wrap. Headings are AGENT-AUTHORED free text: "Recommendations" at
    // 40px Georgia measures wider than the ~327px a phone gives it, and one long word tips the
    // whole page into page-level horizontal scroll. _ManagedPageStyles already solved this for
    // .managed-title with clamp(); the renderers never picked it up.

    public static readonly (string File, string MobileH1)[] Renderers =
    {
        (@"src\IPRO.Web\Views\PublicWebsite\_ModernManagedPage.cshtml", ".modern-page .mp-hero h1"),
        (@"src\IPRO.Web\Views\PublicWebsite\_ClassicManagedPage.cshtml", ".classic-managed .cp-hero h1"),
        (@"src\IPRO.Web\Views\PublicWebsite\_EditorialManagedPage.cshtml", ".editorial-page .ep-hero h1"),
    };

    public static TheoryData<string, string> RendererCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var (file, sel) in Renderers) data.Add(file, sel);
        return data;
    }

    [Theory]
    [MemberData(nameof(RendererCases))]
    public void Hero_headings_break_long_words_and_scale_down(string file, string selector)
    {
        var css = File.ReadAllText(FindRepoFile(file));

        // The base rule carries the safety net for arbitrary agent-authored words...
        var baseRule = FirstRule(css, selector);
        Assert.Contains("overflow-wrap:break-word", baseRule.Replace(" ", ""));

        // ...and the mobile size is fluid, not a fixed pixel count that a narrow phone can't fit.
        var mobileRules = AllRules(css, selector).Skip(1).ToArray();
        Assert.True(mobileRules.Any(r => r.Contains("clamp(")),
            $"{selector} still uses a fixed mobile font-size; a long heading overflows at 360px");
    }

    // ---- 7. the follow-up filters and add-form fit a phone -----------------------------------

    [Fact]
    public void The_followup_filter_group_wraps_instead_of_overflowing()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Clients\FollowUps.cshtml"));
        // A btn-group is ONE inline-flex unit -- the outer container's flex-wrap does not help;
        // the group itself must be allowed to wrap. Five buttons at 360px overflow otherwise.
        var group = Regex.Match(view, "<div class=\"btn-group[^\"]*\"[^>]*aria-label=\"Follow-up filters\"");
        Assert.True(group.Success, "the filter group moved; this pin needs updating");
        Assert.Contains("flex-wrap", group.Value);
    }

    [Fact]
    public void The_followup_add_inputs_shrink_with_the_screen()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Clients\FollowUps.cshtml"));
        // Fixed widths (220+160+240px + a button) exceed 360px and forced sideways scroll.
        Assert.DoesNotContain("width:220px", view.Replace(" ", ""));
        Assert.DoesNotContain("width:160px", view.Replace(" ", ""));
        Assert.DoesNotContain("width:240px", view.Replace(" ", ""));
        Assert.Contains("max-width", view);
    }

    // ---- 8. the overlay header must not sit on the hero at phone width -----------------------
    //
    // header-overlay makes the public-site header position:absolute over the hero. At <=800px the
    // header stacks vertically and grows tall, covering the H1 of a shipped default template.

    [Fact]
    public void The_overlay_header_returns_to_the_flow_on_phones()
    {
        var css = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\PublicWebsite\_ManagedPageStyles.cshtml"));
        var mobile = MediaBlock(css, "max-width: 800px");
        Assert.Matches(new Regex(@"\.header-overlay\s+\.public-site-header\s*\{[^}]*position:\s*static"), mobile);
    }

    // ---- 9. completing a follow-up asks first ------------------------------------------------
    //
    // The complete button sits 4px from delete at 31px (22px on the calendar), and completing
    // mutated silently -- a mis-tap on a phone changed data with no way to notice. Every
    // CompleteFollowUp submit now runs through the shared js-confirm-submit handler (the CSP-safe
    // pattern this layout already uses for deletes).

    public static readonly string[] CompleteFollowUpViews =
    {
        @"src\IPRO.Web\Views\Clients\Calendar.cshtml",
        @"src\IPRO.Web\Views\Clients\Details.cshtml",
        @"src\IPRO.Web\Views\Clients\FollowUpQueue.cshtml",
        @"src\IPRO.Web\Views\Clients\FollowUps.cshtml",
        @"src\IPRO.Web\Views\Dashboard\Index.cshtml",
    };

    public static TheoryData<string> CompleteFollowUpCases()
    {
        var data = new TheoryData<string>();
        foreach (var v in CompleteFollowUpViews) data.Add(v);
        return data;
    }

    [Theory]
    [MemberData(nameof(CompleteFollowUpCases))]
    public void Every_complete_button_confirms_before_mutating(string file)
    {
        var view = File.ReadAllText(FindRepoFile(file));
        foreach (Match form in Regex.Matches(view, "asp-action=\"CompleteFollowUp\"[\\s\\S]{0,600}?</form>"))
        {
            Assert.Contains("js-confirm-submit", form.Value);
            Assert.Contains("data-confirm-message", form.Value);
        }
        // And the view really does have at least one such form -- an empty loop proves nothing.
        Assert.Contains("CompleteFollowUp", view);
    }

    // ---- 10. the page's main action comes first on a phone -----------------------------------
    //
    // Client Details stacked its whole left column (contact card, portal, testimonial, documents)
    // above the Follow-ups panel, burying the page's primary action under ~1,500px of scroll.

    [Fact]
    public void Client_details_shows_followups_before_the_contact_card_on_phones()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Clients\Details.cshtml"));
        Assert.Matches(new Regex(@"col-md-4[^""]*order-2[^""]*order-md-1|col-md-4[^""]*order-md-1[^""]*order-2"), view);
        Assert.Matches(new Regex(@"col-md-8[^""]*order-1[^""]*order-md-2|col-md-8[^""]*order-md-2[^""]*order-1"), view);
    }

    [Fact]
    public void Document_filenames_wrap_instead_of_widening_the_page()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Clients\Details.cshtml"));
        var docRow = view[view.IndexOf("DownloadPortalDocument", StringComparison.Ordinal)..];
        var li = view[..view.IndexOf("DownloadPortalDocument", StringComparison.Ordinal)];
        var liStart = li.LastIndexOf("<li", StringComparison.Ordinal);
        Assert.Contains("text-break", view[liStart..(liStart + 400)]);
    }

    // ---- 11. the two free Admin fixes --------------------------------------------------------
    //
    // Admin's mobile shell is DELIBERATELY out of scope before launch (TODO 436). These two ride
    // along because they are one-liners: dvh so the sidebar's Logout is not under a phone toolbar,
    // and flex-wrap so the 7-button Agents/Details toolbar stops pushing Delete off-screen (the
    // same header on Revenue and Refunds already wraps; this one was the outlier).

    [Fact]
    public void Admin_sidebar_is_sized_to_the_visible_viewport()
    {
        var layout = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Shared\_Layout.cshtml"));
        var rule = FirstRule(layout, ".admin-sidebar");
        var flat = rule.Replace(" ", "");
        Assert.Contains("height:100vh", flat);
        Assert.Contains("height:100dvh", flat);
        Assert.True(flat.IndexOf("height:100vh", StringComparison.Ordinal) < flat.IndexOf("height:100dvh", StringComparison.Ordinal));
    }

    [Fact]
    public void Agent_details_toolbar_wraps_like_its_siblings()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Agents\Details.cshtml"));
        var header = view[..view.IndexOf("breadcrumb", StringComparison.Ordinal)];
        Assert.Contains("flex-wrap", header);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static string FirstRule(string css, string selector) =>
        AllRules(css, selector).FirstOrDefault()
        ?? throw new InvalidOperationException($"no CSS rule found for {selector}");

    private static string[] AllRules(string css, string selector) =>
        Regex.Matches(css, Regex.Escape(selector) + @"\s*\{[^}]*\}")
            .Select(m => m.Value)
            .ToArray();

    private static string MediaBlock(string css, string query)
    {
        var i = css.IndexOf(query, StringComparison.Ordinal);
        Assert.True(i > 0, $"no media block found for {query}");
        var open = css.IndexOf('{', i);
        var depth = 0;
        for (var j = open; j < css.Length; j++)
        {
            if (css[j] == '{') depth++;
            else if (css[j] == '}') { depth--; if (depth == 0) return css[open..j]; }
        }
        throw new InvalidOperationException($"unterminated media block for {query}");
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
