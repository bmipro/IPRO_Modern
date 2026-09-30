using IPRO.Entities;

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
// keep their own call (NewsLetterDispatcher.AppendUnsubscribeHtml) because their footer speaks to
// subscribers rather than clients. 533: both are SenderFooter's now, which adds what Canada's
// anti-spam law asks for: the adviser's business and mailing address, and iPro sending on its behalf.
public static class EmailUnsubscribeFooter
{
    // Sits BELOW the message shell, on the page background rather than inside the card, so it stays
    // legible whether the design above it is light or dark -- e-card designs are frequently dark and
    // a footer inheriting those colours would be invisible.
    public static string AppendHtml(string htmlBody, string? unsubscribeUrl, AgentUser? agent)
    {
        if (string.IsNullOrWhiteSpace(unsubscribeUrl)) return htmlBody;
        return SenderFooter.AppendHtml(htmlBody, agent, unsubscribeUrl, SenderFooterKind.Client, centered: true);
    }
}
