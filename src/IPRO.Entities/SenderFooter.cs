using System.Collections.Generic;
using System.Net;

namespace IPRO.Entities;

// Who a marketing email is for: people subscribed to the adviser's updates (newsletters and the drip
// campaigns that share their footer), or the adviser's own clients (cards, letters, polls, Did You
// Know, testimonial requests).
public enum SenderFooterKind
{
    Newsletter,
    Client
}

// 533 (2026-09-29): Canada's anti-spam law (CASL) asks every commercial email to name who sent it and
// on whose behalf, to give a mailing address, and to offer a way out. iPro's marketing emails named
// the adviser and a phone or email, but carried no mailing address, and the newsletter's footer called
// the sender "your IPRO adviser" -- the wording the owner had removed from client emails that morning
// (530). Every marketing email now closes with this one footer, so the wording cannot drift between
// senders: why the person is receiving it, the business and its address, the way out, and "Sent with
// iPro on behalf of" the business (PoweredBy, the seam a white-label switch will close).
public static class SenderFooter
{
    private const string Muted = "#64748b";
    private const string LinkColor = "#2563eb";

    // The footer block. `centered` sits it on the page background below a card or letter shell (their
    // designs are often dark, and a footer inside them would inherit the colours); the default follows
    // the body of a newsletter.
    public static string Html(AgentUser? agent, string? unsubscribeUrl, SenderFooterKind kind, bool centered = false)
    {
        var business = AdviserSender.BusinessName(agent);
        var lines = new List<string> { WebUtility.HtmlEncode(Reason(business, kind)) };

        var identity = Identity(agent, business);
        if (identity.Length > 0) lines.Add(WebUtility.HtmlEncode(identity));

        if (!string.IsNullOrWhiteSpace(unsubscribeUrl))
            lines.Add($"""<a href="{WebUtility.HtmlEncode(unsubscribeUrl)}" style="color:{LinkColor};">{UnsubscribeLabel(kind)}</a>""");

        var onBehalf = business.Length == 0 ? "" : $" on behalf of {WebUtility.HtmlEncode(business)}";
        var sentWith = $"""<span style="display:inline-block;margin-top:10px;">Sent with <a href="{PoweredBy.BrandUrl(agent?.BusinessType)}" style="color:{Muted};font-weight:600;text-decoration:none;">{PoweredBy.Label}</a>{onBehalf}</span>""";

        var style = centered
            ? $"max-width:620px;margin:20px auto 0;padding:16px 12px 0;border-top:1px solid #dbe4f0;color:{Muted};font-family:Arial,Helvetica,sans-serif;font-size:12px;line-height:1.6;text-align:center;"
            : $"margin-top:32px;padding-top:16px;border-top:1px solid #dbe4f0;color:{Muted};font-family:Arial,sans-serif;font-size:12px;line-height:1.5;";

        return $"""
            <div style="{style}">
              {string.Join("\n  <br>\n  ", lines)}
              <br>
              {sentWith}
            </div>
            """;
    }

    public static string AppendHtml(string htmlBody, AgentUser? agent, string? unsubscribeUrl, SenderFooterKind kind, bool centered = false) =>
        $"{htmlBody}{System.Environment.NewLine}{Html(agent, unsubscribeUrl, kind, centered)}";

    // The plain-text part's footer: the same lines, the link on its own line.
    public static string Text(AgentUser? agent, string? unsubscribeUrl, SenderFooterKind kind)
    {
        var business = AdviserSender.BusinessName(agent);
        var lines = new List<string> { "---", Reason(business, kind) };

        var identity = Identity(agent, business);
        if (identity.Length > 0) lines.Add(identity);

        if (!string.IsNullOrWhiteSpace(unsubscribeUrl))
        {
            lines.Add($"{UnsubscribeLabel(kind)}:");
            lines.Add(unsubscribeUrl);
        }

        lines.Add(string.Empty);
        var onBehalf = business.Length == 0 ? "" : $" on behalf of {business}";
        lines.Add($"Sent with {PoweredBy.Label}{onBehalf}: {PoweredBy.BrandUrl(agent?.BusinessType)}");
        return string.Join("\n", lines);
    }

    private static string Reason(string business, SenderFooterKind kind) => kind == SenderFooterKind.Newsletter
        ? $"You are receiving this email because you are subscribed to updates from {(business.Length == 0 ? "this sender" : business)}."
        : $"You received this because you are a client of {(business.Length == 0 ? "the sender" : business)}.";

    private static string UnsubscribeLabel(SenderFooterKind kind) => kind == SenderFooterKind.Newsletter
        ? "Unsubscribe from future newsletters"
        : "Unsubscribe or change what you receive";

    // "Global Business Solution, 123 Main Street, Toronto ON M5V 1A1, Canada" -- or whichever half
    // is on file.
    private static string Identity(AgentUser? agent, string business)
    {
        var address = AdviserSender.MailingAddress(agent);
        if (business.Length == 0) return address;
        return address.Length == 0 ? business : $"{business}, {address}";
    }
}
