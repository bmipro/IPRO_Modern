using System.Net;

namespace IPRO.Business.Services;

// The visible unsubscribe line at the bottom of an email.
//
// Separate from the List-Unsubscribe header, and both are needed. The header is what Gmail and
// Yahoo read to offer their own Unsubscribe button next to the sender name -- but they show it at
// their discretion and routinely withhold it from low-volume senders, so a recipient can be looking
// at a message with a perfectly good header and no way to act on it. That is what happened on
// 2026-08-08: the header shipped, the owner opened a delivered card, and there was nothing to click.
//
// Shared by e-cards and e-letters so the wording and the styling cannot drift apart. Newsletters
// keep their own copy (NewsLetterDispatcher.AppendUnsubscribeHtml) because their footer also carries
// subscription context this one does not.
public static class EmailUnsubscribeFooter
{
    // Sits BELOW the message shell, on the page background rather than inside the card, so it stays
    // legible whether the design above it is light or dark -- e-card designs are frequently dark and
    // a footer inheriting those colours would be invisible.
    public static string AppendHtml(string htmlBody, string? unsubscribeUrl)
    {
        if (string.IsNullOrWhiteSpace(unsubscribeUrl)) return htmlBody;

        var encoded = WebUtility.HtmlEncode(unsubscribeUrl);
        var footer = $"""
            <div style="max-width:620px;margin:20px auto 0;padding:16px 12px 0;border-top:1px solid #dbe4f0;color:#64748b;font-family:Arial,Helvetica,sans-serif;font-size:12px;line-height:1.6;text-align:center;">
              You received this because you are a client of the sender.
              <br>
              <a href="{encoded}" style="color:#2563eb;text-decoration:underline;">Unsubscribe or change what you receive</a>
            </div>
            """;

        return $"{htmlBody}{Environment.NewLine}{footer}";
    }
}
