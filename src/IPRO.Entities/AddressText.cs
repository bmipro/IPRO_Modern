using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace IPRO.Entities;

// 516 (2026-09-22): one way to write the city line of an address on invoices and estimates --
// "City, Province PostalCode" -- and the tidy-up of bill-to snapshots frozen before 516, which put
// the city and "Province PostalCode" on separate lines. The owner, on the first real invoice of the
// 514 design: "put the province and Postal code in front of the city to make it look nicer".
public static class AddressText
{
    public static string CityLine(string? city, string? province, string? postalCode)
    {
        var c = (city ?? string.Empty).Trim();
        var provincePostal = string.Join(" ", new[] { province, postalCode }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        if (c.Length == 0) return provincePostal;
        if (provincePostal.Length == 0) return c;
        // The comma separates the city from its province; a city with only a postal code reads "Toronto M4N 3P6".
        return c + (string.IsNullOrWhiteSpace(province) ? " " : ", ") + provincePostal;
    }

    // A snapshot line that is a city followed by one that is a province, a province and a postal
    // code, or a postal code alone becomes one line. Anything else is left exactly as stored: the
    // snapshot is the record, and only a shape this certain is joined.
    public static IReadOnlyList<string> JoinCityAndProvinceLines(IReadOnlyList<string> lines)
    {
        var result = new List<string>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = (lines[i] ?? string.Empty).Trim();
            if (i + 1 < lines.Count)
            {
                var next = (lines[i + 1] ?? string.Empty).Trim();
                if (LooksLikeCity(line) && LooksLikeProvinceAndPostal(next))
                {
                    result.Add(line + ", " + next);
                    i++;
                    continue;
                }
            }
            result.Add(line);
        }
        return result;
    }

    private static readonly Regex PostalTail = new(@"(^|\s)(?<postal>[A-Za-z]\d[A-Za-z][ -]?\d[A-Za-z]\d|\d{5}(-\d{4})?)$", RegexOptions.Compiled);

    private static readonly HashSet<string> Provinces = new(StringComparer.OrdinalIgnoreCase)
    {
        "AB", "BC", "MB", "NB", "NL", "NS", "NT", "NU", "ON", "PE", "QC", "SK", "YT",
        "Alberta", "British Columbia", "Manitoba", "New Brunswick", "Newfoundland and Labrador", "Newfoundland",
        "Nova Scotia", "Northwest Territories", "Nunavut", "Ontario", "Prince Edward Island", "Quebec", "Québec",
        "Saskatchewan", "Yukon"
    };

    private static bool LooksLikeCity(string line) =>
        line.Length > 0 && !line.Any(char.IsDigit) && !line.Contains(',');

    private static bool LooksLikeProvinceAndPostal(string line)
    {
        if (line.Length == 0 || line.Contains(',')) return false;
        var match = PostalTail.Match(line);
        var province = match.Success ? line[..match.Index].Trim() : line;
        if (match.Success && province.Length == 0) return true;                       // a postal code alone
        if (Provinces.Contains(province)) return true;                                 // "Ontario", "Ontario M4N 2Z3"
        return match.Success && !province.Any(char.IsDigit) && province.Length <= 30;  // "NY 10118": a region before a postal code
    }
}
