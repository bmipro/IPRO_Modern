using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace IPRO.IntegrationTests.Accessibility;

// 637 (2026-11-21): accessibility phases 2 and 3 of the October audit (DOCS/ACCESSIBILITY_AUDIT_2026-10-14.md in
// the notes): the adviser portal, the client portal, the sign-in pages and iPro's marketing pages. The audit counted
// about 210 portal fields whose label a screen reader never heard; every one was tied to its field. These tests read
// the markup of every portal view, so a new screen that repeats the old mistakes fails here before anyone hears it.
// What they read is the source; PortalScreens637Tests renders screens and checks them with axe in Edge.
public class PortalMarkup637Tests
{
    private static readonly Regex LabelElement = new(@"<label\b([^>]*)>(.*?)</label>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex FieldElement = new(@"<(input|select|textarea)\b((?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex ButtonOrLink = new(@"<(button|a)\b((?:[^>""']|""[^""]*""|'[^']*')*)>(.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly string[] NotFields = { "hidden", "submit", "button", "reset", "image" };

    // A field named by a label in ANOTHER view: the invoice client box is a partial, labelled by its callers'
    // <label for="invoiceClient"> (ClientInvoices/Edit and RecurringInvoices/Edit).
    private static readonly HashSet<string> LabelledByCaller = new(StringComparer.Ordinal) { "Shared/_InvoiceClientSelect.cshtml" };

    [Fact]
    public void Every_label_in_the_portals_is_tied_to_its_field_or_captions_a_group()
    {
        var untied = new List<string>();
        foreach (var (rel, source) in PortalViews())
        {
            foreach (Match label in LabelElement.Matches(source))
            {
                var attrs = label.Groups[1].Value;
                if (Attr(attrs, "for") != null || Attr(attrs, "asp-for") != null) continue;
                if (Attr(attrs, "id") != null) continue;              // a caption a group points at with aria-labelledby
                if (FieldElement.IsMatch(label.Groups[2].Value)) continue; // the field is inside the label
                untied.Add($"{rel}:{Line(source, label.Index)}: {Text(label.Groups[2].Value)}");
            }
        }
        Assert.True(untied.Count == 0, $"{untied.Count} labels a screen reader does not tie to any field:\n" + string.Join("\n", untied));
    }

    [Fact]
    public void Every_field_in_the_portals_has_a_name_a_screen_reader_says()
    {
        var unnamed = new List<string>();
        foreach (var (rel, source) in PortalViews())
        {
            if (LabelledByCaller.Contains(rel)) continue;
            var labelTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match label in LabelElement.Matches(source))
                if ((Attr(label.Groups[1].Value, "for") ?? Attr(label.Groups[1].Value, "asp-for")) is { } target) labelTargets.Add(target);

            foreach (Match field in FieldElement.Matches(source))
            {
                var attrs = field.Groups[2].Value;
                if (field.Groups[1].Value.Equals("input", StringComparison.OrdinalIgnoreCase)
                    && NotFields.Contains((Attr(attrs, "type") ?? string.Empty).ToLowerInvariant())) continue;
                if (Regex.IsMatch(Attr(attrs, "class") ?? string.Empty, @"(^|\s)d-none(\s|$)")) continue; // never shown
                if (Attr(attrs, "aria-label") != null || Attr(attrs, "aria-labelledby") != null || Attr(attrs, "title") != null) continue;
                var before = source[..field.Index];
                if (before.LastIndexOf("<label", StringComparison.OrdinalIgnoreCase) > before.LastIndexOf("</label>", StringComparison.OrdinalIgnoreCase)) continue;
                var id = Attr(attrs, "id") ?? Attr(attrs, "asp-for");
                if (id != null && labelTargets.Contains(id)) continue;
                unnamed.Add($"{rel}:{Line(source, field.Index)}: <{field.Groups[1].Value} {Regex.Replace(attrs, @"\s+", " ").Trim()[..Math.Min(90, Regex.Replace(attrs, @"\s+", " ").Trim().Length)]}>");
            }
        }
        Assert.True(unnamed.Count == 0, $"{unnamed.Count} fields with no name a screen reader can say:\n" + string.Join("\n", unnamed));
    }

    [Fact]
    public void Every_button_or_link_that_shows_only_an_icon_has_a_name()
    {
        var nameless = new List<string>();
        foreach (var (rel, source) in PortalViews())
        {
            foreach (Match element in ButtonOrLink.Matches(source))
            {
                var attrs = element.Groups[2].Value;
                var inner = element.Groups[3].Value;
                if (Attr(attrs, "aria-label") != null || Attr(attrs, "aria-labelledby") != null || Attr(attrs, "title") != null) continue;
                if (inner.Contains("visually-hidden", StringComparison.Ordinal)) continue;
                if (Regex.IsMatch(inner, @"<img\b[^>]*\balt\s*=\s*""[^""]+""")) continue;
                if (Text(Regex.Replace(inner, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline)).Length > 0) continue;
                nameless.Add($"{rel}:{Line(source, element.Index)}: <{element.Groups[1].Value} {Regex.Replace(attrs, @"\s+", " ").Trim()}>");
            }
        }
        Assert.True(nameless.Count == 0, $"{nameless.Count} buttons or links a screen reader announces with no name:\n" + string.Join("\n", nameless));
    }

    [Fact]
    public void A_label_inside_a_repeated_block_points_at_a_field_of_that_block()
    {
        // The labels tied in 637 to fields of the page editor's blocks, a campaign's steps, the menu's pages and the
        // account types carry the item's id, so each one names its own field and not the first block's.
        var editor = Read(@"src\IPRO.Web\Views\WebsitePages\Edit.cshtml");
        Assert.Contains("<label for=\"f-heading-@block.Id\"", editor);
        Assert.Contains("<input id=\"f-heading-@block.Id\"", editor);
        Assert.Contains("role=\"group\" aria-labelledby=\"dykArticles-@(block.Id)-label\"", editor);
        Assert.Contains("<div role=\"group\" aria-labelledby=\"agentShow-@(block.Id)-label\">", editor);
        Assert.Contains("for=\"f-delayDays-@i\"", Read(@"src\IPRO.Web\Views\Campaigns\Details.cshtml"));
        Assert.Contains("for=\"f-navigationLabel-@menuPage.Id\"", Read(@"src\IPRO.Web\Views\WebsitePages\Navigation.cshtml"));
        Assert.Contains("for=\"f-name-2-@type.Id\"", Read(@"src\IPRO.Web\Views\Clients\AccountTypes.cshtml"));
    }

    [Fact]
    public void The_adviser_portal_has_a_skip_link_a_main_region_a_heading_and_spoken_messages()
    {
        var layout = Read(@"src\IPRO.Web\Views\Shared\_Layout.cshtml");
        var body = layout[layout.IndexOf("<body>", StringComparison.Ordinal)..];
        // The skip link is the first thing in the page a keyboard reaches.
        Assert.True(body.IndexOf("<a class=\"skip-link\" href=\"#main-content\">Skip to main content</a>", StringComparison.Ordinal)
                    < body.IndexOf("<aside", StringComparison.Ordinal));
        Assert.Contains("<main id=\"main-content\" tabindex=\"-1\"", layout);
        Assert.Contains("@RenderBody()\n</main>", layout);
        Assert.Contains("{ <h1 class=\"visually-hidden\">@pageTitle</h1> }", layout);
        Assert.Contains("<nav class=\"agent-sidebar-nav\" aria-label=\"Main menu\">", layout);
        Assert.Contains("<div id=\"portalAnnouncer\" class=\"visually-hidden\" role=\"status\" aria-live=\"polite\"></div>", layout);
        Assert.Contains("link.setAttribute('aria-current', 'page')", layout);
        Assert.Contains("announcer.textContent = messages.join(' ')", layout);
        // Field edges at 3:1 or better (Bootstrap's #dee2e6 is 1.3:1).
        Assert.Contains(".form-control, .form-select, .form-check-input { border-color:#8a94a0; }", layout);
        // Every portal colour reads at 4.5:1 as text on the page and behind white text (Sunset Orange was 4.0:1).
        Assert.Contains("var effectiveAccent = IPRO.Entities.AccentContrast.For(", layout);
        foreach (var swatch in new[] { "#1457d9", "#d9541f", "#1f7a4d", "#4b5563", "#7a1f3d", "#5b2f9e" })
        {
            var ink = IPRO.Entities.AccentContrast.For(swatch, "#f8fafc", "#ffffff", darkTextOnLight: false).Ink;
            Assert.True(Contrast(ink, "#f8fafc") >= 4.5 && Contrast(ink, "#ffffff") >= 4.5, swatch);
        }
        // On a phone, opening the menu moves focus into it, and closing it brings focus back to the Menu button.
        Assert.Contains("document.getElementById('sidebarClose')?.focus();", layout);
        Assert.Contains("if (wasOpen && agentSidebar.contains(document.activeElement)) sidebarToggle?.focus();", layout);
        // The views that write their own level-1 heading say so, and get no second one.
        foreach (var view in new[] { "Index", "RecoveryCodes", "Setup" })
            Assert.Contains("ViewData[\"OwnHeading\"] = true;", Read($@"src\IPRO.Web\Views\TwoStep\{view}.cshtml"));
    }

    [Fact]
    public void The_client_portal_menu_reads_white_on_green_and_the_page_has_the_same_landmarks()
    {
        var layout = Read(@"src\IPRO.Web\Views\Shared\_ClientPortalLayout.cshtml");
        Assert.Contains(".client-portal-nav .nav-link, .client-portal-nav .nav-link:focus { color:#fff; }", layout);
        Assert.Contains("style=\"background:#0f7a52;\" aria-label=\"Main menu\"", layout);
        Assert.Contains("<a class=\"skip-link\" href=\"#main-content\">Skip to main content</a>", layout);
        Assert.Contains("<main id=\"main-content\" tabindex=\"-1\" class=\"container-fluid py-4\">", layout);
        Assert.Contains("<h1 class=\"visually-hidden\">@pageTitle</h1>", layout);
        Assert.Contains("role=\"status\" aria-live=\"polite\"", layout);
        Assert.Contains("link.setAttribute('aria-current', 'page');", layout);
        Assert.DoesNotContain("opacity:.85", layout); // the footer line was faint grey made fainter
        // White on the portal's green is 5.4:1; the pale green it replaced was 2.72:1.
        Assert.True(Contrast("#ffffff", "#0f7a52") >= 4.5);
    }

    [Theory]
    [InlineData(@"Account\Login.cshtml")]
    [InlineData(@"Account\Register.cshtml")]
    [InlineData(@"Account\ForgotPassword.cshtml")]
    [InlineData(@"Account\ForgotPasswordConfirmation.cshtml")]
    [InlineData(@"Account\ResetPassword.cshtml")]
    [InlineData(@"Account\ChangePassword.cshtml")]
    [InlineData(@"ClientPortalAccount\Login.cshtml")]
    [InlineData(@"ClientPortalAccount\Activate.cshtml")]
    [InlineData(@"ClientPortalAccount\ChooseAccount.cshtml")]
    [InlineData(@"ClientPortalAccount\AccessDenied.cshtml")]
    public void Every_sign_in_page_says_it_is_in_English(string view)
    {
        var source = Read(@"src\IPRO.Web\Views\" + view);
        Assert.Contains("<html lang=\"en\">", source);
        Assert.DoesNotContain("<html>", source);
    }

    [Fact]
    public void Sign_in_and_password_fields_tell_a_password_manager_what_they_are()
    {
        Assert.Contains("name=\"username\" autocomplete=\"username\"", Read(@"src\IPRO.Web\Views\Account\Login.cshtml"));
        Assert.Contains("name=\"password\" autocomplete=\"current-password\"", Read(@"src\IPRO.Web\Views\Account\Login.cshtml"));
        Assert.Contains("name=\"email\" autocomplete=\"username\"", Read(@"src\IPRO.Web\Views\ClientPortalAccount\Login.cshtml"));
        Assert.Contains("name=\"password\" autocomplete=\"new-password\"", Read(@"src\IPRO.Web\Views\ClientPortalAccount\Activate.cshtml"));
        Assert.Contains("name=\"newPassword\" autocomplete=\"new-password\"", Read(@"src\IPRO.Web\Views\Account\ResetPassword.cshtml"));
        Assert.Contains("name=\"currentPassword\" autocomplete=\"current-password\"", Read(@"src\IPRO.Web\Views\Account\ChangePassword.cshtml"));
        var register = Read(@"src\IPRO.Web\Views\Account\Register.cshtml");
        foreach (var hint in new[] { "given-name", "family-name", "email", "new-password", "organization", "street-address", "address-level2", "postal-code", "tel" })
            Assert.Contains($"autocomplete=\"{hint}\"", register);
    }

    [Fact]
    public void The_marketing_oranges_carry_white_text_at_4_5_to_1()
    {
        // www.iProAdvisers.com's coral (was #d06a35, 3.6:1) and the accountant and mortgage pages' orange (was #f36b21, 3.0:1).
        Assert.Contains("--ipro2-coral: light-dark(#ae5628, #f09461);", Read(@"src\IPRO.Web\Views\Home\Index.cshtml"));
        Assert.Contains(".i2-action { background: var(--ipro2-coral); color: light-dark(#ffffff, #1d1208); }", Read(@"src\IPRO.Web\Views\Home\Index.cshtml"));
        var landing = Read(@"src\IPRO.Web\wwwroot\css\landing-page.css");
        Assert.Contains("--orange: #c2410c;", landing);
        Assert.Contains("--orange-dark: #9a3412;", landing);
        Assert.True(Contrast("#ffffff", "#ae5628") >= 4.5);
        Assert.True(Contrast("#ffffff", "#c2410c") >= 4.5);
        Assert.True(Contrast("#1d1208", "#f09461") >= 4.5);  // the dark-mode coral carries dark text
        // The sign-in page's tagline (was #a9812f, 3.6:1).
        Assert.Contains("color:#80621f;", Read(@"src\IPRO.Web\Views\Account\Login.cshtml"));
        Assert.True(Contrast("#80621f", "#ffffff") >= 4.5);
    }

    [Fact]
    public void The_rich_text_editor_has_a_name_and_heading_buttons()
    {
        var editor = Read(@"src\IPRO.Web\Views\Shared\_RichEditor.cshtml");
        Assert.Contains("role=\"textbox\" aria-multiline=\"true\" aria-labelledby=\"@labelId\" aria-label=\"@(labelId == null ? label : null)\"", editor);
        Assert.Contains("data-command=\"formatBlock\" data-value=\"h2\" title=\"Heading\" aria-label=\"Heading\"", editor);
        Assert.Contains("data-command=\"formatBlock\" data-value=\"h3\" title=\"Subheading\" aria-label=\"Subheading\"", editor);
        Assert.Contains("document.execCommand(command, false, button.dataset.value);", editor);
        // Each caller's visible label names its editor.
        Assert.Contains("{ \"LabelId\", \"articleContent-label\" }", Read(@"src\IPRO.Web\Views\Articles\Create.cshtml"));
        Assert.Contains("{ \"LabelId\", \"nlBody-label\" }", Read(@"src\IPRO.Web\Views\Newsletter\Create.cshtml"));
        Assert.Contains("{ \"LabelId\", $\"stepBody{step.Id}-label\" }", Read(@"src\IPRO.Web\Views\Campaigns\Details.cshtml"));
    }

    // ---- harness -----------------------------------------------------------------------------------------------

    internal static IEnumerable<(string Rel, string Source)> PortalViews()
    {
        var views = Path.Combine(PublicSitePages.RepoRoot(), "src", "IPRO.Web", "Views");
        foreach (var file in Directory.EnumerateFiles(views, "*.cshtml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(views, file).Replace('\\', '/');
            if (rel.StartsWith("PublicWebsite/", StringComparison.Ordinal)) continue; // customer websites: 578's own tests
            yield return (rel, File.ReadAllText(file).Replace("\r\n", "\n"));
        }
    }

    private static string? Attr(string attrs, string name)
    {
        var m = Regex.Match(attrs, @"(?<![\w-])" + Regex.Escape(name) + @"\s*=\s*(""[^""]*""|'[^']*')", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value[1..^1] : null;
    }

    private static string Text(string html) => Regex.Replace(Regex.Replace(html, "<[^>]+>", string.Empty), @"\s+", " ").Trim();

    private static int Line(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(PublicSitePages.RepoRoot(), relative)).Replace("\r\n", "\n");

    internal static double Contrast(string a, string b)
    {
        static double Luminance(string hex)
        {
            static double Channel(int v) { var c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            var h = hex.TrimStart('#');
            return 0.2126 * Channel(Convert.ToInt32(h[..2], 16)) + 0.7152 * Channel(Convert.ToInt32(h[2..4], 16)) + 0.0722 * Channel(Convert.ToInt32(h[4..6], 16));
        }
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
