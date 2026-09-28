using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;
using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

// 527 (2026-09-28): what the client sees for a payment method the adviser entered -- the button's
// label and address, or the instruction for a method that is not a link (Interac e-Transfer).
public sealed record PayOption(string Method, string Label, string? Url, string? Instruction, string? Note);

// The rules for each method: what the adviser's entry is normalized to, and what a given invoice
// turns it into. Pure, so the tests pin it without a database.
public static class PaymentMethodLinks
{
    public const int ValueMaxLength = 500;
    public const int NoteMaxLength = 200;

    public static string Label(string method) => method switch
    {
        PaymentMethodKinds.PayPal => "Pay with PayPal",
        PaymentMethodKinds.Stripe => "Pay by card",
        PaymentMethodKinds.Square => "Pay with Square",
        PaymentMethodKinds.ETransfer => "Interac e-Transfer",
        _ => "Pay Now"
    };

    // What the adviser typed, normalized to what is stored; empty means "not offered" and is fine.
    public static bool TryNormalize(string method, string? raw, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return true;
        if (text.Length > ValueMaxLength)
        {
            error = "That is too long.";
            return false;
        }

        switch (method)
        {
            case PaymentMethodKinds.PayPal:
            {
                // "bob", "@bob", "paypal.me/bob", "https://www.paypal.me/bob/", "paypal.com/paypalme/bob" -> "bob"
                var handle = Regex.Replace(text, @"^https?://", "", RegexOptions.IgnoreCase);
                handle = Regex.Replace(handle, @"^(www\.)?paypal\.me/", "", RegexOptions.IgnoreCase);
                handle = Regex.Replace(handle, @"^(www\.)?paypal\.com/paypalme/", "", RegexOptions.IgnoreCase);
                handle = handle.TrimStart('@').Split('?', '#')[0].Trim('/');
                if (!Regex.IsMatch(handle, @"^[A-Za-z0-9]{1,50}$"))
                {
                    error = "Enter your PayPal.me name, like paypal.me/yourname.";
                    return false;
                }
                value = handle;
                return true;
            }
            case PaymentMethodKinds.ETransfer:
            {
                if (!MailAddress.TryCreate(text, out var address) || address.Address != text || !address.Host.Contains('.'))
                {
                    error = "Enter the email address your Interac e-Transfers go to.";
                    return false;
                }
                value = text.ToLowerInvariant();
                return true;
            }
            default:
            {
                var link = text.Contains("://") ? text : "https://" + text;
                if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Contains('.'))
                {
                    error = "Enter a full https:// link.";
                    return false;
                }
                value = link;
                return true;
            }
        }
    }

    // The address the client's Pay button opens; null for a method that is an instruction.
    public static string? PayUrl(string method, string value, decimal amount, string currency, string? documentNumber, string? clientEmail)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        switch (method)
        {
            case PaymentMethodKinds.PayPal:
                return PayPalMeLinkHelper.WithAmount($"https://www.paypal.me/{value}", amount, currency);
            case PaymentMethodKinds.Stripe:
            {
                // Stripe's Payment Links take the reference and the buyer's email on the address, so
                // the payment shows up in the adviser's Stripe dashboard under the invoice number.
                var reference = Regex.Replace(documentNumber ?? string.Empty, @"[^A-Za-z0-9_-]+", "-").Trim('-');
                if (reference.Length > 200) reference = reference[..200];
                var query = new List<string>();
                if (reference.Length > 0) query.Add("client_reference_id=" + Uri.EscapeDataString(reference));
                if (!string.IsNullOrWhiteSpace(clientEmail)) query.Add("prefilled_email=" + Uri.EscapeDataString(clientEmail.Trim()));
                if (query.Count == 0) return value;
                return value + (value.Contains('?') ? "&" : "?") + string.Join("&", query);
            }
            case PaymentMethodKinds.ETransfer:
                return null;
            default:
                return value;
        }
    }

    // The line for a method that is not a link.
    public static string? Instruction(string method, string value, decimal amount, string currency) =>
        method == PaymentMethodKinds.ETransfer && !string.IsNullOrWhiteSpace(value)
            ? $"Send an Interac e-Transfer of ${amount.ToString("N2", CultureInfo.InvariantCulture)} {CurrencyCode(currency)} to {value}."
            : null;

    // The options for one document: the adviser's methods in their fixed order; with none, the
    // single link from before 527 (the Profile's Pay Now link), exactly as it always showed.
    public static IReadOnlyList<PayOption> OptionsFor(IEnumerable<AgentPaymentMethod> methods, string? legacyLink, ClientInvoice invoice)
    {
        var byMethod = methods
            .Where(m => !string.IsNullOrWhiteSpace(m.Value))
            .GroupBy(m => m.Method, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var options = new List<PayOption>();
        foreach (var method in PaymentMethodKinds.All)
        {
            if (!byMethod.TryGetValue(method, out var entry)) continue;
            options.Add(new PayOption(
                method,
                Label(method),
                PayUrl(method, entry.Value, invoice.Total, invoice.Currency, invoice.DocumentNumber, invoice.Client?.Email),
                Instruction(method, entry.Value, invoice.Total, invoice.Currency),
                string.IsNullOrWhiteSpace(entry.Note) ? null : entry.Note.Trim()));
        }
        if (options.Count == 0 && !string.IsNullOrWhiteSpace(legacyLink))
        {
            options.Add(new PayOption(PaymentMethodKinds.Other, "Pay Now", PayPalMeLinkHelper.WithAmount(legacyLink, invoice.Total, invoice.Currency), null, null));
        }
        return options;
    }

    // What the Profile's single link from before 527 becomes on the Payments page: the form is
    // pre-filled with it the first time the adviser opens the page, and saving once keeps it there.
    public static (string Method, string Value)? FromLegacyLink(string? legacyLink)
    {
        if (string.IsNullOrWhiteSpace(legacyLink)) return null;
        var link = legacyLink.Trim();
        if (!Uri.TryCreate(link.Contains("://") ? link : "https://" + link, UriKind.Absolute, out var uri)) return (PaymentMethodKinds.Other, link);
        var host = uri.Host.ToLowerInvariant();
        if (host is "paypal.me" or "www.paypal.me")
        {
            var handle = uri.AbsolutePath.Trim('/').Split('/')[0];
            if (Regex.IsMatch(handle, @"^[A-Za-z0-9]{1,50}$")) return (PaymentMethodKinds.PayPal, handle);
        }
        if (host.EndsWith("stripe.com", StringComparison.Ordinal)) return (PaymentMethodKinds.Stripe, link);
        if (host.EndsWith("square.link", StringComparison.Ordinal) || host.EndsWith("square.site", StringComparison.Ordinal) || host.EndsWith("squareup.com", StringComparison.Ordinal))
            return (PaymentMethodKinds.Square, link);
        return (PaymentMethodKinds.Other, link);
    }

    private static string CurrencyCode(string? currency) => string.IsNullOrWhiteSpace(currency) ? "CAD" : currency.Trim().ToUpperInvariant();
}
