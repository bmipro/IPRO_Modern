using Ganss.Xss;

namespace IPRO.Business.Services;

// One sanitizer for every piece of agent-authored HTML that other people will see (articles,
// newsletters, drip steps, e-letters). It ran on stock defaults until 2026-08-20 (A5-M-SANITIZER),
// which permitted working form controls -- a drip email could carry a credential-harvesting form
// that submits anywhere.
//
// TWO PASSES, and the order matters (2026-08-20 audit, finding H1):
//
//   Pass 1 -- XSS. Stock HtmlSanitizer behaviour, KeepChildNodes = false. A dangerous element is
//            deleted ALONG WITH its contents, which is exactly right for <script>/<style>: their
//            bodies are code, not prose, and must not survive as visible text.
//   Pass 2 -- unwrap. KeepChildNodes = true, removing only the interactive form machinery. By now
//            nothing dangerous is left, so unwrapping is safe -- and it PRESERVES the words inside.
//
// Why two passes instead of one: a single pass with KeepChildNodes = true leaked script bodies as
// text ("alert(1)" rendering in the article); a single pass with KeepChildNodes = false silently
// DESTROYED agent prose -- a pasted <button>Book a call</button> became empty and
// <form><p>real text</p></form> lost the paragraph. Sanitisation runs on WRITE, including a re-save
// of existing article content, so that loss was permanent and invisible. Both regressions are
// pinned by tests in MediumSweepTests.
//
// Everything else about the default whitelist is kept ON PURPOSE: newsletters and articles are
// styled with inline CSS (colors, fonts, spacing, tables), and stripping the style attribute
// wholesale would visibly break existing content.
//
// KNOWN GAP (audit M1, wave 2): the CSS removals below are a deny-list and do not actually prevent
// overlays -- transform + negative margin + viewport sizing rebuilds the same primitive. Closing it
// properly needs an ALLOW-list of CSS properties, chosen carefully so existing newsletter
// formatting does not break. Tracked in DOCS/AUDIT_2026-08-20_POST_SWEEP.md.
public static class HtmlContentSanitizer
{
    // Form controls: nothing we render agent HTML into ever needs them. Real IPRO forms are built
    // by the form builder and rendered by our own views, never embedded as raw HTML.
    //
    // ALL of them are removed, including the text-bearing ones -- because pass 2 UNWRAPS rather
    // than deletes, so <button>Book a call</button> becomes the words "Book a call" with no
    // element left behind. That satisfies both rules at once: A5-M-SANITIZER's "no form control
    // survives" AND H1's "no agent prose is destroyed".
    //
    // An earlier version of the H1 fix kept <button> allowed and merely stripped its attributes.
    // That preserved the prose but left an inert control in the output, which broke the
    // A5-M-SANITIZER assertion -- caught by the full suite. Unwrapping is strictly better than
    // either: nothing interactive remains, and nothing readable is lost.
    private static readonly string[] InteractiveTags =
        { "form", "input", "button", "select", "textarea", "option", "optgroup", "label", "fieldset", "legend", "datalist", "output" };

    // Attributes that make a control act, stripped from whatever survives.
    private static readonly string[] ActionAttributes = { "formaction", "formmethod", "formtarget", "action", "method" };

    // Overlay positioning: a block escaping its own box to sit on top of the surrounding page.
    // Incomplete on purpose -- see the KNOWN GAP note above.
    private static readonly string[] OverlayCssProperties = { "position", "z-index", "top", "right", "bottom", "left", "inset", "pointer-events" };

    // Declared AFTER the arrays on purpose: C# initialises static fields in declaration order, so
    // building the sanitizers first left every array above null and threw in the type initializer.
    private static readonly HtmlSanitizer XssPass = CreateXssPass();
    private static readonly HtmlSanitizer UnwrapPass = CreateUnwrapPass();

    private static HtmlSanitizer CreateXssPass()
    {
        var s = new HtmlSanitizer();
        // KeepChildNodes stays FALSE here: script/style bodies must die with their tag.
        Harden(s);
        return s;
    }

    private static HtmlSanitizer CreateUnwrapPass()
    {
        var s = new HtmlSanitizer { KeepChildNodes = true };
        foreach (var tag in InteractiveTags)
        {
            s.AllowedTags.Remove(tag);
        }
        Harden(s);
        return s;
    }

    private static void Harden(HtmlSanitizer s)
    {
        foreach (var attr in ActionAttributes)
        {
            s.AllowedAttributes.Remove(attr);
        }
        foreach (var prop in OverlayCssProperties)
        {
            s.AllowedCssProperties.Remove(prop);
        }
    }

    public static string Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? string.Empty : UnwrapPass.Sanitize(XssPass.Sanitize(html));
}
