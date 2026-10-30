using System.Text;

namespace IPRO.Utility;

// 620: a person's name as the plain letters and digits a new account's user name and temporary site address are
// made of. An accented letter becomes its plain letter; it used to be dropped, which gave a French-speaking
// adviser a user name with holes in it (HlneCt for Helene Cote written with its accents). All else is left out.
public static class SignUpIdentifier
{
    // The letters that do not come apart into a plain letter and an accent.
    private static readonly Dictionary<char, string> Whole = new()
    {
        [(char)0x00C6] = "AE", [(char)0x00E6] = "ae", [(char)0x0152] = "OE", [(char)0x0153] = "oe",
        [(char)0x00D8] = "O", [(char)0x00F8] = "o", [(char)0x00DF] = "ss",
        [(char)0x0141] = "L", [(char)0x0142] = "l", [(char)0x0110] = "D", [(char)0x0111] = "d"
    };

    public static string From(string? name)
    {
        var plain = new StringBuilder();
        foreach (var letter in (name ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(letter)) plain.Append(letter);
            else if (Whole.TryGetValue(letter, out var letters)) plain.Append(letters);
        }
        return plain.ToString();
    }
}
