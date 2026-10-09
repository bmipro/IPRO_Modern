using System.Text.RegularExpressions;

namespace IPRO.Web.Infrastructure;

// 558 (2026-10-09). A block's button may open a page, dial a number or start an email (549). The
// bakery writes its number "(416)-886-0458"; "tel:(416)-886-0458" was not accepted because the rule
// wanted a digit straight after "tel:", so the link was dropped WITHOUT A WORD and every "Give us a
// call" button fell back to /contact. A number may now start with a bracket, a number or an address
// typed on its own becomes a tel: or mailto: link, and a link that is not kept is said so.
public static class ButtonLinks
{
    // A phone number as people write it: digits, spaces, brackets, dots, dashes; an optional +.
    private const string Number = @"\+?\(?[0-9][0-9 ().-]{2,30}";
    private static readonly Regex PhoneLink = new("^tel:" + Number + "$", RegexOptions.IgnoreCase);
    private static readonly Regex BarePhone = new("^" + Number + "$");
    private const string Address = @"[^\s@<>""'()]+@[^\s@<>""'()]+\.[^\s@<>""'()]+";
    private static readonly Regex MailLink = new("^mailto:" + Address + "$", RegexOptions.IgnoreCase);
    private static readonly Regex BareMail = new("^" + Address + "$");

    // "" when the value is not a link a button may carry. webAddress is the caller's rule for a page
    // path or an http(s) address.
    public static string Normalize(string? value, Func<string?, string> webAddress)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length == 0) return string.Empty;
        if (value.StartsWith('/')) return value;
        if (PhoneLink.IsMatch(value) || MailLink.IsMatch(value)) return value;
        // Typed on its own: a number with at least seven digits, or an email address.
        if (BarePhone.IsMatch(value) && value.Count(char.IsDigit) >= 7) return "tel:" + value;
        if (BareMail.IsMatch(value)) return "mailto:" + value;
        return webAddress(value);
    }

    // What the owner is told when a typed link was not kept.
    public static string NotKept(string typed) =>
        $"Saved, but the button link \"{typed.Trim()}\" was not kept, so the button opens your contact page. " +
        "A link can be a page (/menu), a web address (https://...), a phone number (tel:416-555-0199) or an email (mailto:you@example.com).";
}
