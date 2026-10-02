using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Email;

// Tells the agent that one of their clients has unsubscribed.
//
// This lives in IPRO.Email rather than in EmailConsentService because the dependency only runs one
// way: IPRO.Email references IPRO.Business, so IPRO.Business cannot reach IEmailService. The consent
// service declares IUnsubscribeNotifier and this satisfies it.
//
// The body moved verbatim from EmailPreferencesController.NotifyAgentAsync. It used to fire only
// when someone clicked the unsubscribe link on the preferences page; now that every suppression
// path funnels through EmailConsentService.SuppressAllAsync, a spam complaint or an unsubscribe
// reported by SendGrid notifies the agent too -- which is the case an adviser most wants to know
// about and previously never heard.
public class UnsubscribeNotifier : IUnsubscribeNotifier
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;

    public UnsubscribeNotifier(IPRODbContext db, IEmailService email)
    {
        _db = db;
        _email = email;
    }

    public async Task NotifyAgentAsync(Client client)
    {
        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == client.AgentUserId);
        if (agent == null || string.IsNullOrWhiteSpace(agent.Email)) return;

        var clientName = $"{client.FirstName} {client.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(clientName)) clientName = client.Email;

        // 538: the same suppression has three causes, and until now the adviser was told one. The
        // owner's pilot of Amazon SES: an estimate to a mailbox that bounces and an invoice to one
        // that reports spam, and two notices saying each client "has unsubscribed from your emails".
        // The reason is on the row by the time a notifier runs (SuppressAllAsync saves first).
        var (subject, html) = EmailOptOut.ReasonOf(client) switch
        {
            EmailOptOutReason.Bounced => BouncedNotice(clientName, client.Email),
            EmailOptOutReason.Complaint => ComplaintNotice(clientName),
            _ => UnsubscribedNotice(clientName)
        };

        await _email.SendDetailedAsync(agent.Email, $"{agent.FirstName} {agent.LastName}".Trim(), subject, html);
    }

    private static (string Subject, string Html) UnsubscribedNotice(string clientName) =>
        ($"{clientName} unsubscribed from your emails",
         $"""
            <p>{System.Net.WebUtility.HtmlEncode(clientName)} has unsubscribed from your emails.</p>
            <p style="color:#475569;">They will no longer receive your newsletter, e-letters, polls
            or website follow-ups. If they chose to keep receiving birthday and anniversary
            greetings, those will still go out.</p>
            <p style="color:#475569;">You can still contact them directly — this only affects the
            marketing emails sent from your IPRO portal.</p>
            """);

    // Nobody decided anything: the address is wrong. What the adviser can do about it is the point.
    private static (string Subject, string Html) BouncedNotice(string clientName, string address) =>
        ($"An email to {clientName} bounced",
         $"""
            <p>An email to {System.Net.WebUtility.HtmlEncode(clientName)} at
            <strong>{System.Net.WebUtility.HtmlEncode(address)}</strong> bounced:
            the address does not exist or cannot receive mail.</p>
            <p style="color:#475569;">Your newsletter, e-letters, cards, polls and campaigns to this
            client are on hold, because mail to an address that bounces harms the delivery of all
            your other email.</p>
            <p style="color:#475569;">If the address is mistyped, open the client in your IPRO portal
            and correct it. Saving the corrected address switches their email back on.</p>
            """);

    private static (string Subject, string Html) ComplaintNotice(string clientName) =>
        ($"{clientName} reported one of your emails as spam",
         $"""
            <p>{System.Net.WebUtility.HtmlEncode(clientName)} marked one of your emails as spam.</p>
            <p style="color:#475569;">That is treated as a request to stop: they will no longer
            receive your newsletter, e-letters, cards, polls or campaigns, and only they can change
            that.</p>
            <p style="color:#475569;">You can still contact them directly — this only affects the
            marketing emails sent from your IPRO portal.</p>
            """);
}
