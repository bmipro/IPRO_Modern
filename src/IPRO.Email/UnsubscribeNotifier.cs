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

        var html = $"""
            <p>{System.Net.WebUtility.HtmlEncode(clientName)} has unsubscribed from your emails.</p>
            <p style="color:#475569;">They will no longer receive your newsletter, e-letters, polls
            or website follow-ups. If they chose to keep receiving birthday and anniversary
            greetings, those will still go out.</p>
            <p style="color:#475569;">You can still contact them directly — this only affects the
            marketing emails sent from your IPRO portal.</p>
            """;

        await _email.SendDetailedAsync(agent.Email, $"{agent.FirstName} {agent.LastName}".Trim(),
            $"{clientName} unsubscribed from your emails", html);
    }
}
