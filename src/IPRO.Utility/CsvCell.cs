using System.Text.RegularExpressions;

namespace IPRO.Utility;

// 678 (audit 2026-11-24 E-3): a cell a spreadsheet would read as a formula. A visitor's name, phone or message on a
// public form reaches the Website Leads, Clients and invoice exports; one starting with "=", "+", "-" or "@" (or a tab
// or a carriage return) ran as a formula when the adviser opened the file in Excel or Sheets, and HYPERLINK or
// IMPORTXML can send other rows out. Such a cell gets a leading apostrophe, which spreadsheets show as text. A plain
// number or phone number ("-50.00", "+1 416-555-0100") is left as it is: nothing runs, and the figures stay figures.
public static class CsvCell
{
    private static readonly Regex NumberLike = new(@"\A[+-]?[0-9][0-9 .,()/-]*\z", RegexOptions.CultureInvariant);

    public static string Neutralize(string? value)
    {
        value ??= string.Empty;
        if (value.Length == 0) return value;
        var first = value[0];
        if (first is not ('=' or '+' or '-' or '@' or '\t' or '\r')) return value;
        return NumberLike.IsMatch(value) ? value : "'" + value;
    }

    // One CSV field: neutralized, then quoted when it holds a comma, a quote or a line break.
    public static string Escape(string? value)
    {
        var text = Neutralize(value);
        return text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }
}
