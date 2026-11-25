using System.Net;
using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

// 678 (audit 2026-11-24 A-1): where an adviser's clients send money is changed on the Payments page, and every
// invoice already sent shows the new destination the next time it is opened. Only the owner can change it now
// (OwnerOnlyAreas); this email tells the owner each time it happens, so a change they did not make (someone with
// their password) is seen the same day instead of when a client's payment goes missing.
public static class PaymentChangeNotice
{
    public sealed record Line(string Label, string? Before, string? After);

    public static string Label(string method) => method switch
    {
        PaymentMethodKinds.PayPal => "PayPal.me",
        PaymentMethodKinds.Stripe => "Stripe payment link",
        PaymentMethodKinds.Square => "Square payment link",
        PaymentMethodKinds.ETransfer => "Interac e-Transfer email",
        PaymentMethodKinds.Other => "Other payment link",
        _ => method
    };

    // What changed between the saved rows and the values just written (an empty value is "removed").
    public static List<Line> Compare(IReadOnlyDictionary<string, (string Value, string Note)> before,
                                     IReadOnlyDictionary<string, (string Value, string Note)> after)
    {
        var lines = new List<Line>();
        foreach (var method in PaymentMethodKinds.All)
        {
            before.TryGetValue(method, out var b);
            after.TryGetValue(method, out var a);
            var bv = b.Value ?? string.Empty;
            var av = a.Value ?? string.Empty;
            if (!string.Equals(bv, av, StringComparison.Ordinal))
            {
                lines.Add(new Line(Label(method), bv.Length == 0 ? null : bv, av.Length == 0 ? null : av));
            }
            else if (method == PaymentMethodKinds.ETransfer && av.Length > 0
                     && !string.Equals(b.Note ?? string.Empty, a.Note ?? string.Empty, StringComparison.Ordinal))
            {
                lines.Add(new Line("Interac e-Transfer note", NullIfEmpty(b.Note), NullIfEmpty(a.Note)));
            }
        }
        return lines;
    }

    public const string Subject = "Your iPro payment details were changed";

    public static string Html(string firstName, IReadOnlyList<Line> lines, string whenText, string paymentsUrl)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var rows = string.Concat(lines.Select(l =>
            $"<tr><td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb\">{E(l.Label)}</td>" +
            $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;color:#6b7280\">{(l.Before == null ? "(none)" : E(l.Before))}</td>" +
            $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb\"><strong>{(l.After == null ? "(removed)" : E(l.After))}</strong></td></tr>"));
        var greeting = string.IsNullOrWhiteSpace(firstName) ? "Hello," : $"Hello {E(firstName.Trim())},";
        return $@"<div style=""font-family:Arial,Helvetica,sans-serif;font-size:15px;color:#111827;line-height:1.5"">
<p>{greeting}</p>
<p>The payment details on your iPro account were changed {E(whenText)}. Every invoice you have sent shows the new details the next time a client opens it.</p>
<table style=""border-collapse:collapse;margin:12px 0"">
<tr><th style=""text-align:left;padding:6px 10px"">What</th><th style=""text-align:left;padding:6px 10px"">Before</th><th style=""text-align:left;padding:6px 10px"">Now</th></tr>
{rows}
</table>
<p>If you made this change, there is nothing to do.</p>
<p><strong>If you did not,</strong> sign in, put your details back on the <a href=""{E(paymentsUrl)}"">Payments page</a>, and change your password straight away.</p>
<p style=""color:#6b7280;font-size:13px"">iPro sends this email every time payment details change, so a change you did not make is seen the same day.</p>
</div>";
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
