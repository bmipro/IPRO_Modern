using System.Text.RegularExpressions;

namespace IPRO.Entities;

/// <summary>
/// 618 (found 2026-10-29 in 614's side-by-side). The three public-site shells wrote the design's font
/// list into their &lt;style&gt; through Razor, which encodes the quote marks
/// (<c>Georgia, &amp;#x27;Times New Roman&amp;#x27;, serif</c>). A style element is not decoded, so the
/// browser dropped the whole declaration and every site whose font list names a family with a space
/// showed Bootstrap's font instead: Classic's and Editorial's Georgia never appeared. The list is now
/// written as it is, after being held to the shape of a font list, so nothing else can reach the
/// style element: families separated by commas, each a bare name (a letter, or a hyphen and a letter,
/// then letters, digits, spaces or hyphens) or a name in single or double quote marks (letters, digits,
/// spaces, hyphens). Anything else is replaced by <see cref="Fallback"/>.
/// </summary>
public static class CssFontList
{
    public const string Fallback = "Arial, Helvetica, sans-serif";

    // The owner, 2026-11-08: "keep Apple's font on Modern too". The Modern UI list (Modern Professional's
    // own, and the "Modern UI" choice in the font pickers) starts with Segoe UI, Windows' own interface
    // font. While the list never reached a page, Apple devices drew Bootstrap's font, which is Apple's;
    // Segoe UI is not on them, so the list alone would have moved them to Arial. Apple's interface font
    // goes in front of a list that starts with Segoe UI: Windows has neither name and draws exactly what
    // it draws now, and Apple devices keep their own font (-apple-system: Safari and Firefox;
    // BlinkMacSystemFont: Chrome and Edge).
    public static readonly string[] AppleInterfaceFonts = { "-apple-system", "BlinkMacSystemFont" };

    private static readonly Regex Family = new(
        @"^\s*(?:-?[A-Za-z][A-Za-z0-9 \-]*|'[A-Za-z0-9 \-]+'|""[A-Za-z0-9 \-]+"")\s*$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string Safe(string? fontList)
    {
        if (string.IsNullOrWhiteSpace(fontList) || fontList.Length > 200) return Fallback;
        var families = fontList.Split(',');
        foreach (var family in families)
            if (!Family.IsMatch(family)) return Fallback;
        var list = families.Select(f => f.Trim()).ToList();
        if (string.Equals(Name(list[0]), "Segoe UI", StringComparison.OrdinalIgnoreCase)
            && !list.Any(f => AppleInterfaceFonts.Contains(Name(f), StringComparer.OrdinalIgnoreCase)))
            list.InsertRange(0, AppleInterfaceFonts);
        return string.Join(", ", list);
    }

    // A family's name without its quote marks.
    private static string Name(string family) => family.Trim('\'', '"');
}
