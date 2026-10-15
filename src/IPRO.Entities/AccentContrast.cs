using System.Globalization;

namespace IPRO.Entities;

/// <summary>
/// 578: readable text with whatever colour a website owner picked (WCAG 1.4.3, 4.5:1 for text). The
/// audit of 2026-10-14 found 4 of the 14 colours iPro offers too light for white button text and small
/// coloured words (Modern Minimal #0EA5E9 2.8:1, Amber Gold 3.2:1, Sunset Orange 4.0:1, Teal 4.1:1),
/// and the free colour picker allows any colour. Each design takes four colours from the owner's one:
/// <list type="bullet">
/// <item><see cref="Fill"/>: buttons and solid coloured bands. The owner's colour itself when its text
/// reads; otherwise deepened just enough for white text.</item>
/// <item><see cref="OnFill"/>: the text on Fill. White, or near-black on a light colour.</item>
/// <item><see cref="Band"/>: where white text crosses a fade into the colour (hero and call-to-action
/// gradients); always dark enough for white text.</item>
/// <item><see cref="Ink"/>: the colour as small text on the design's light backgrounds (section labels,
/// the highlighted menu item, links); deepened until it reads on the palest tint it sits on (and the
/// faint cards laid on it) and on the page background.</item>
/// </list>
/// A colour whose text already reads with room to spare comes back unchanged in every role: of the 10
/// offered colours whose white text reads, only Forest Green's small text in the Editorial design (the
/// darkest tint) is a shade, 4%, deeper.
/// </summary>
public sealed record AccentContrast(string Fill, string OnFill, string Band, string Ink)
{
    public const double TextRatio = 4.5;
    public const string White = "#ffffff";
    /// <summary>Near-black text for light colours (Tailwind gray-900, the designs' darkest ink).</summary>
    public const string Dark = "#111827";

    /// <summary>The owner's choice for a light colour (sky blue, amber): keep it and put near-black text on
    /// it (true), or deepen it until white text reads (false).</summary>
    public const bool KeepLightColours = true;

    /// <summary>
    /// The four colours for <paramref name="accent"/> (#rrggbb) in a design whose palest tint under
    /// coloured text is <paramref name="tint"/> and whose page background (the owner's or the design's)
    /// is <paramref name="pageBackground"/>. <paramref name="darkTextOnLight"/> overrides
    /// <see cref="KeepLightColours"/> (the tests try both).
    /// </summary>
    public static AccentContrast For(string accent, string tint, string? pageBackground = null, bool darkTextOnLight = KeepLightColours)
    {
        var colour = Normalize(accent);
        var band = DeepenUntil(colour, White);
        string fill, onFill;
        if (Ratio(colour, White) >= TextRatio) (fill, onFill) = (colour, White);
        else if (darkTextOnLight && Ratio(colour, Dark) >= TextRatio) (fill, onFill) = (colour, Dark);
        else (fill, onFill) = (band, White);
        var ink = DeepenUntil(colour, Normalize(tint));
        // And on the faint cards laid on the tint (the Did You Know grid's 3% grey).
        ink = DeepenUntil(ink, Scale(Normalize(tint), 0.97));
        // Coloured text also sits straight on the page background in places (the "page not found" screen
        // of the Classic Sidebar design), so it must read there too when that background is a light one.
        if (IsHex(pageBackground) && Luminance(pageBackground!) > 0.5) ink = DeepenUntil(ink, pageBackground!);
        return new AccentContrast(fill, onFill, band, ink);
    }

    /// <summary>WCAG contrast ratio of two #rrggbb colours, 1 to 21.</summary>
    public static double Ratio(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static double Luminance(string hex)
    {
        var colour = Normalize(hex);
        double Channel(int at)
        {
            var c = int.Parse(colour.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    /// <summary>The colour darkened in steps of 1% (same hue) until text in it, or white text on it,
    /// reaches 4.5:1 against <paramref name="against"/>; unchanged when it already does.</summary>
    public static string DeepenUntil(string hex, string against)
    {
        var colour = Normalize(hex);
        for (var percent = 100; percent >= 0; percent--)
        {
            var candidate = Scale(colour, percent / 100.0);
            if (Ratio(candidate, against) >= TextRatio) return candidate;
        }
        return "#000000";
    }

    private static string Scale(string colour, double factor)
    {
        int Part(int at) => (int)Math.Round(int.Parse(colour.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) * factor, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"#{Part(1):x2}{Part(3):x2}{Part(5):x2}");
    }

    private static bool IsHex(string? hex)
    {
        var value = (hex ?? string.Empty).Trim();
        return value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
    }

    private static string Normalize(string? hex) => IsHex(hex) ? hex!.Trim().ToLowerInvariant() : "#1457d9";
}
