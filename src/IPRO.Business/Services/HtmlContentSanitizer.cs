using System;
using System.Collections.Generic;
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
// CSS: ALLOW-list, not deny-list (M1, fixed 2026-08-27). The old approach removed eight named
// properties and the register's own test proved it useless -- transform + negative margin +
// viewport sizing rebuilt the same full-viewport phishing cover from properties still allowed.
// Now only the properties in FormattingCssProperties survive at all. The list is grounded in
// what this product's content actually is: the seeded templates are plain semantic HTML, the
// in-house editor emits little beyond text-align, and the rich sources are agents pasting
// marketing HTML through the editor's source toggle -- so typography, color, background, box,
// sizing and in-flow layout are allowed generously, and every mechanism that lets a block ESCAPE
// its place in the flow (position/inset, z-index, transform and friends, clip/filter, animation)
// is simply absent. Two value-level guards close the in-list residue: negative margins and
// text-indent (the drag-a-block-over-earlier-content primitive) and viewport units anywhere
// (width:100% is the legitimate spelling; 100vw is only ever the exploit's).
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

    // Everything a newsletter, article, e-card or e-letter legitimately formats with -- and
    // nothing that moves a box out of normal flow. Absence IS the security property here: a
    // property not in this list does not render, whatever its value.
    private static readonly string[] FormattingCssProperties =
    {
        // typography
        "color", "direction", "font", "font-family", "font-size", "font-style", "font-variant",
        "font-weight", "letter-spacing", "line-height", "quotes", "tab-size", "text-align",
        "text-align-last", "text-decoration", "text-decoration-color", "text-decoration-line",
        "text-decoration-style", "text-decoration-thickness", "text-indent", "text-overflow",
        "text-shadow", "text-transform", "unicode-bidi", "vertical-align", "white-space",
        "word-break", "word-spacing", "word-wrap", "overflow-wrap", "hyphens",
        // background & paint
        "background", "background-color", "background-image", "background-position",
        "background-repeat", "background-size", "background-clip", "background-origin", "opacity",
        // box: margin / padding / border
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "border", "border-width", "border-style", "border-color",
        "border-top", "border-top-width", "border-top-style", "border-top-color",
        "border-right", "border-right-width", "border-right-style", "border-right-color",
        "border-bottom", "border-bottom-width", "border-bottom-style", "border-bottom-color",
        "border-left", "border-left-width", "border-left-style", "border-left-color",
        "border-radius", "border-top-left-radius", "border-top-right-radius",
        "border-bottom-left-radius", "border-bottom-right-radius",
        "border-collapse", "border-spacing", "box-shadow", "box-sizing",
        // sizing
        "width", "min-width", "max-width", "height", "min-height", "max-height",
        "object-fit", "object-position", "aspect-ratio",
        // in-flow layout only
        "display", "float", "clear", "overflow", "overflow-x", "overflow-y",
        "gap", "column-gap", "row-gap", "align-items", "align-content", "align-self",
        "justify-content", "justify-items", "justify-self",
        "flex", "flex-basis", "flex-direction", "flex-grow", "flex-shrink", "flex-wrap", "order",
        "list-style", "list-style-type", "list-style-position", "list-style-image",
        "table-layout", "caption-side", "empty-cells"
    };

    // Viewport-relative units: no email or article has ever needed one; the overlay always does.
    private static readonly System.Text.RegularExpressions.Regex ViewportUnit = new(
        @"(^|[\s,(])[-+]?\d*\.?\d+(vw|vh|vmin|vmax|svw|svh|lvw|lvh|dvw|dvh)($|[\s,;)])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

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

        // The allow-list: whatever the library's defaults were, only these render.
        s.AllowedCssProperties.Clear();
        foreach (var prop in FormattingCssProperties)
        {
            s.AllowedCssProperties.Add(prop);
        }

        // Value-level residue, after the property filter has run: a negative margin or
        // text-indent drags a block over content that came before it, and a viewport unit sizes
        // it to the screen instead of its place in the layout. Both are dropped declaration-by-
        // declaration so the rest of the style survives.
        s.PostProcessNode += static (_, e) =>
        {
            if (e.Node is not AngleSharp.Dom.IElement el) return;
            var style = el.GetAttribute("style");
            if (string.IsNullOrWhiteSpace(style)) return;

            var kept = new List<string>();
            foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
                var colon = declaration.IndexOf(':');
                if (colon <= 0) continue;
                var property = declaration[..colon].Trim().ToLowerInvariant();
                var value = declaration[(colon + 1)..].Trim();

                if (property.StartsWith("margin", StringComparison.Ordinal) || property == "text-indent")
                {
                    // Any negative component kills the declaration: "margin: 10px -900px" is the
                    // exploit exactly as much as "margin-top: -900px".
                    if (HasNegativeComponent(value)) continue;
                }
                if (ViewportUnit.IsMatch(value)) continue;

                kept.Add(property + ": " + value);
            }

            if (kept.Count == 0) el.RemoveAttribute("style");
            else el.SetAttribute("style", string.Join("; ", kept));
        };
    }

    private static bool HasNegativeComponent(string value)
    {
        for (var i = 0; i < value.Length - 1; i++)
        {
            if (value[i] != '-') continue;
            var next = value[i + 1];
            var isNumberStart = char.IsDigit(next) || next == '.';
            var atTokenStart = i == 0 || value[i - 1] is ' ' or ',' or '(' or ':';
            if (isNumberStart && atTokenStart) return true;
        }
        return false;
    }

    public static string Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? string.Empty : UnwrapPass.Sanitize(XssPass.Sanitize(html));
}
