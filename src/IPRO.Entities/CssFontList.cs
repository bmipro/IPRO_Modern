using System.Text.RegularExpressions;

namespace IPRO.Entities;

/// <summary>
/// 618 (found 2026-10-29 in 614's side-by-side). The three public-site shells wrote the design's font
/// list into their &lt;style&gt; through Razor, which encodes the quote marks
/// (<c>Georgia, &amp;#x27;Times New Roman&amp;#x27;, serif</c>). A style element is not decoded, so the
/// browser dropped the whole declaration and every site whose font list names a family with a space
/// showed Bootstrap's font instead: Classic's and Editorial's Georgia never appeared. The list is now
/// written as it is, after being held to the shape of a font list, so nothing else can reach the
/// style element: families separated by commas, each a bare name (a letter, then letters, digits,
/// spaces or hyphens) or a name in single or double quote marks (letters, digits, spaces, hyphens).
/// Anything else is replaced by <see cref="Fallback"/>.
/// </summary>
public static class CssFontList
{
    public const string Fallback = "Arial, Helvetica, sans-serif";

    private static readonly Regex Family = new(
        @"^\s*(?:[A-Za-z][A-Za-z0-9 \-]*|'[A-Za-z0-9 \-]+'|""[A-Za-z0-9 \-]+"")\s*$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string Safe(string? fontList)
    {
        if (string.IsNullOrWhiteSpace(fontList) || fontList.Length > 200) return Fallback;
        var families = fontList.Split(',');
        foreach (var family in families)
            if (!Family.IsMatch(family)) return Fallback;
        return string.Join(", ", families.Select(f => f.Trim()));
    }
}
