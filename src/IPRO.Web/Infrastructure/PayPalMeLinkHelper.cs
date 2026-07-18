using System.Globalization;

namespace IPRO.Web.Infrastructure;

public static class PayPalMeLinkHelper
{
    public static string? WithAmount(string? paymentLink, decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(paymentLink)) return paymentLink;
        if (!Uri.TryCreate(paymentLink, UriKind.Absolute, out var uri)) return paymentLink;
        if (!uri.Host.Equals("paypal.me", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("www.paypal.me", StringComparison.OrdinalIgnoreCase))
        {
            return paymentLink;
        }

        var currencyCode = string.IsNullOrWhiteSpace(currency) ? "CAD" : currency.Trim().ToUpperInvariant();
        var basePath = paymentLink.TrimEnd('/');
        return $"{basePath}/{amount.ToString("0.00", CultureInfo.InvariantCulture)}{currencyCode}";
    }
}
