using Ganss.Xss;

namespace IPRO.Business.Services;

// One sanitizer for every piece of agent-authored HTML that other people will see (articles,
// newsletters, drip steps, e-letters). It ran on stock defaults until 2026-08-20 (A5-M-SANITIZER),
// which permitted two things an email or article never legitimately needs:
//
//   1. Working form controls (<form>, <input>, <button>, ...) -- a drip email could carry a
//      credential-harvesting form that submits anywhere.
//   2. Overlay positioning (style="position:fixed; z-index:9999") -- a block could visually cover
//      the page it is embedded in with a convincing fake.
//
// Everything else about the default whitelist is kept ON PURPOSE: newsletters and articles are
// styled with inline CSS (colors, fonts, spacing, tables), and stripping the style attribute
// wholesale would visibly break existing content. The subtractions below are the phishing vectors,
// not the formatting.
public static class HtmlContentSanitizer
{
    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        var s = new HtmlSanitizer();

        // Form controls: nothing we render agent HTML into ever needs them. Real IPRO forms are
        // built by the form builder and rendered by our own views, never embedded as raw HTML.
        foreach (var tag in new[] { "form", "input", "button", "select", "textarea", "option", "optgroup", "label", "fieldset", "legend", "datalist", "output" })
        {
            s.AllowedTags.Remove(tag);
        }
        s.AllowedAttributes.Remove("formaction");

        // Overlay positioning: position/z-index (and inset offsets) let a sanitized block escape
        // its own box and sit on top of the surrounding page. Normal content flows; ads don't.
        foreach (var prop in new[] { "position", "z-index", "top", "right", "bottom", "left", "inset", "pointer-events" })
        {
            s.AllowedCssProperties.Remove(prop);
        }

        return s;
    }

    public static string Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? string.Empty : Sanitizer.Sanitize(html);
}
