using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace IPRO.IntegrationTests;

// Mobile-readiness sweep items 3-5 (2026-08-31): the PUBLIC surfaces, where a phone visitor is
// most likely and least forgiving. Items 1-2 fixed the portal shell for advisers; these three fix
// the pages a prospect meets before they are a customer. Every test observed RED first.
public class MobilePublicSurfaceTests
{
    // Every public page that puts a form in front of a visitor or a signed-out agent.
    public static readonly string[] PublicFormViews =
    {
        @"src\IPRO.Web\Views\Account\Register.cshtml",
        @"src\IPRO.Web\Views\Account\ChangePassword.cshtml",
        @"src\IPRO.Web\Views\Account\ForgotPassword.cshtml",
        @"src\IPRO.Web\Views\Account\ResetPassword.cshtml",
        @"src\IPRO.Web\Views\PublicWebsite\_WebsiteLeadForm.cshtml",
        @"src\IPRO.Web\Views\PublicWebsite\_WebsiteCustomForm.cshtml",
        @"src\IPRO.Web\Views\PublicWebsite\_TestimonialForm.cshtml",
        @"src\IPRO.Web\Views\PublicWebsite\_CalculatorBlock.cshtml",
    };

    // ---- 3. iOS must not zoom when a visitor taps a field -----------------------------------
    //
    // Mobile Safari zooms the page on focus whenever the focused control's font-size is under
    // 16px, and does not zoom back out. Every public form was at 15px -- the 11-field signup form,
    // the agent-site lead form, testimonials and the calculators, i.e. exactly the forms the
    // business converts on. A visitor filling in signup on a phone got a zoomed, horizontally
    // scrolling page from the first field onward.

    [Theory]
    [MemberData(nameof(PublicFormViewCases))]
    public void Public_form_controls_are_at_least_16px(string relativePath)
    {
        var css = File.ReadAllText(FindRepoFile(relativePath));

        // Scoped to rules that style a TEXT-ENTRY control, because that is precisely what triggers
        // the zoom: focusing an input/textarea/select under 16px. A 15px submit BUTTON or a 15px
        // paragraph of helper text is harmless and must not be flagged -- an over-broad pin here
        // would push pointless churn into unrelated styling.
        foreach (var rule in RulesMentioning(css, "input", "textarea", "select"))
        {
            Assert.DoesNotMatch(new Regex(@"font\s*:\s*\d+\s+1[0-5]px"), rule);   // shorthand
            Assert.DoesNotMatch(new Regex(@"font-size\s*:\s*1[0-5]px"), rule);    // longhand
        }
    }

    public static TheoryData<string> PublicFormViewCases()
    {
        var data = new TheoryData<string>();
        foreach (var v in PublicFormViews) data.Add(v);
        return data;
    }

    [Fact]
    public void The_signup_page_body_font_does_not_shrink_its_inputs()
    {
        // Register's inputs use font:inherit, so the BODY size is the control size. 15px there
        // zoomed the longest, highest-value form in the product.
        var css = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Account\Register.cshtml"));
        var bodyRule = RuleFor(css, "html,body");
        Assert.DoesNotContain("font-size:15px", bodyRule.Replace(" ", ""));
        Assert.Contains("font-size:16px", bodyRule.Replace(" ", ""));
    }

    // ---- 4. the hero widget works on a phone ------------------------------------------------
    //
    // Below 421px -- the Samsung S10 at 360px, most Androids, iPhone SE/mini -- the widget's whole
    // tab rail was display:none with nothing replacing it, so SIX of the seven product screenshots
    // were unreachable. The one that did show is a 1204x929 desktop capture rendered into a
    // ~321x366 box: measured live at 0.394 scale, with a third of its width cropped away by
    // object-fit:cover, putting 14px portal text at ~5.5px. The hero's only proof element was an
    // illegible smear on the majority of phones.

    [Fact]
    public void The_tab_rail_survives_on_narrow_phones()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));
        var narrow = MediaBlock(view, "max-width: 420px");
        Assert.DoesNotMatch(new Regex(@"\.i2-command-nav\s*\{[^}]*display:\s*none"), narrow);
        // It becomes a horizontal rail rather than a vertical column at that width.
        Assert.Contains("i2-command-tabs", narrow);
    }

    [Fact]
    public void The_screenshot_is_shown_whole_rather_than_cropped()
    {
        // object-fit:cover fills the box by cropping; on a phone that discarded a third of every
        // capture. contain keeps the whole screenshot legible-as-possible instead.
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));
        var narrow = MediaBlock(view, "max-width: 420px");
        Assert.Matches(new Regex(@"\.i2-shot\s*\{[^}]*object-fit:\s*contain"), narrow);
    }

    // ---- 5. a customer on a phone can find Sign in ------------------------------------------
    //
    // @media (max-width: 920px) hid every header anchor that is not .i2-action, and Sign in was a
    // bare <a>. An existing customer arriving on a phone had to scroll the entire marketing page
    // to the footer to find the way in.

    [Fact]
    public void Sign_in_stays_visible_on_a_phone()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Home\Index.cshtml"));

        // The blanket hide is still there for the other nav anchors...
        var nav920 = MediaBlock(view, "max-width: 920px");
        Assert.Contains(".i2-nav a:not(.i2-action)", nav920);

        // ...and Sign in is exempted EXPLICITLY. Note why this is not asserted by looking for
        // "i2-action" on the link: "i2-action-outline" contains that substring, so a link carrying
        // only the outline class would satisfy a naive Contains check while still being hidden by
        // :not(.i2-action), which matches on the class token, not the substring. The pin has to be
        // the un-hide rule itself.
        Assert.Matches(new Regex(@"\.i2-nav a\.i2-nav-signin\s*\{[^}]*display:\s*(?!none)"), nav920);

        var navMarkup = view[view.IndexOf("class=\"i2-nav\"", StringComparison.Ordinal)..];
        navMarkup = navMarkup[..navMarkup.IndexOf("</nav>", StringComparison.Ordinal)];
        var signIn = navMarkup.Split("<a ").FirstOrDefault(a => a.Contains("Sign in", StringComparison.Ordinal));
        Assert.NotNull(signIn);
        Assert.Contains("i2-nav-signin", signIn!);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static string MediaBlock(string css, string query)
    {
        var i = css.IndexOf(query, StringComparison.Ordinal);
        Assert.True(i > 0, $"no media block found for {query}");
        var open = css.IndexOf('{', i);
        var depth = 0;
        for (var j = open; j < css.Length; j++)
        {
            if (css[j] == '{') depth++;
            else if (css[j] == '}')
            {
                depth--;
                if (depth == 0) return css[open..j];
            }
        }
        throw new InvalidOperationException($"unterminated media block for {query}");
    }

    private static string RuleFor(string css, string selector)
    {
        var i = css.IndexOf(selector + "{", StringComparison.Ordinal);
        if (i < 0) i = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(i >= 0, $"no CSS rule found for {selector}");
        var open = css.IndexOf('{', i);
        var close = css.IndexOf('}', open);
        return css[open..close];
    }

    // Every CSS rule whose selector mentions one of the given control names.
    private static string[] RulesMentioning(string css, params string[] controls) =>
        Regex.Matches(css, @"[^{}]+\{[^{}]*\}")
            .Select(m => m.Value)
            .Where(rule => controls.Any(c => rule[..Math.Max(0, rule.IndexOf('{'))].Contains(c, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
