using System.Text.RegularExpressions;

namespace IPRO.Email;

// 511 (2026-09-21): the owner's rule for a brand domain in anything a person READS -- on a page, in
// an email, on an invoice: the capitals separate the words, and the www. stays. www.iProAdvisers.com,
// billing@iProAdvisers.com, www.iProAccountants.com, www.iProMortgages.com.
//
// For DISPLAY only. Never pass the result to anything a machine reads: the From and Reply-To
// addresses (the mail provider matches the sender against its own list), a mailto: link, a canonical
// tag, a setting. Host names are case-insensitive, so a link to the configured address whose TEXT has
// the capitals is the right pair.
public static partial class BrandText
{
    public static string WithCapitals(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : BrandDomain().Replace(text, match => "iPro" + char.ToUpperInvariant(match.Groups[1].Value[0]) + match.Groups[1].Value[1..].ToLowerInvariant() + ".com");

    // A whole host label, whatever its case: "notiproadvisers.community" is not the brand.
    [GeneratedRegex(@"(?<![A-Za-z0-9-])ipro(advisers|accountants|mortgages)\.com(?![A-Za-z0-9-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BrandDomain();
}
