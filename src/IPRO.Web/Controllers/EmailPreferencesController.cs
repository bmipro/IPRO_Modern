using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

// Unsubscribe, and what the client can choose afterwards.
//
// Reached only from a token link in an email, so every action is AllowAnonymous and the token is the
// only credential. Routed under /email-preferences, which is in IsNeverShadowedPrefix: these links
// sit in inboxes for years and must keep resolving on an agent's custom domain, the same reason
// `invoice`, `poll` and `testimonial` are on that list.
//
// The two entry points are deliberately asymmetric:
//
//   POST (no human)  -- RFC 8058 one-click. Gmail and Yahoo fire this themselves the moment someone
//                       presses their client's built-in Unsubscribe button. Nobody sees a page, so it
//                       must suppress EVERYTHING immediately. It cannot ask a question.
//   GET  (a human)   -- the landing page. Confirms they are unsubscribed, then offers the one
//                       deliberate choice: keep receiving birthday and anniversary greetings.
//
// That asymmetry is the whole design. An unsubscribe that quietly kept sending some mail would be a
// CAN-SPAM exposure and, more immediately, generates the spam complaints that damage deliverability
// for every agent on the platform.
[AllowAnonymous]
[Route("email-preferences")]
public class EmailPreferencesController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailPreferencesController> _logger;

    public EmailPreferencesController(
        IPRODbContext db,
        IEmailService email,
        IConfiguration configuration,
        ILogger<EmailPreferencesController> logger)
    {
        _db = db;
        _email = email;
        _configuration = configuration;
        _logger = logger;
    }

    // RFC 8058 one-click target. Paired with the List-Unsubscribe-Post header that
    // SendGridEmailService already sends.
    //
    // IgnoreAntiforgeryToken because the caller is a mail provider, not a browser with a session --
    // there is no token to send and no cookie to protect. The URL token is the authorisation.
    //
    // Always returns 200, even for an unknown token. A provider retrying against a 4xx helps nobody,
    // and a different response for a valid vs invalid token would turn this into an oracle for
    // testing whether a token exists.
    [HttpPost("")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> OneClick(string token)
    {
        var client = await FindByTokenAsync(token);
        if (client == null)
        {
            _logger.LogWarning("One-click unsubscribe called with an unrecognised token.");
            return Ok();
        }

        await SuppressAllAsync(client, "one-click");
        return Ok();
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string token)
    {
        var client = await FindByTokenAsync(token);
        if (client == null)
        {
            ViewBag.Invalid = true;
            return View();
        }

        // Clicking the link in a mail client is itself an unsubscribe -- the person pressed
        // "unsubscribe", and making them press a second button on the page to make it stick is the
        // pattern that gets senders reported. Suppress first, then offer choices.
        if (!client.EmailOptOutAt.HasValue)
        {
            await SuppressAllAsync(client, "link");
        }

        await LoadViewDataAsync(client);
        return View();
    }

    // The one deliberate choice, made by a person who is looking at the page.
    [HttpPost("save")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Save(string token, bool allowGreetings = false, bool resubscribeAll = false)
    {
        var client = await FindByTokenAsync(token);
        if (client == null)
        {
            ViewBag.Invalid = true;
            return View(nameof(Index));
        }

        if (resubscribeAll)
        {
            client.EmailOptOutAt = null;
            client.GreetingsOptInAt = null;
            client.IsNewsletterSubscribed = true;
            ViewBag.Message = "You're subscribed again. You'll receive updates from your adviser as before.";
        }
        else
        {
            // Still unsubscribed; only the greetings exception moves.
            client.GreetingsOptInAt = allowGreetings ? DateTime.UtcNow : null;
            ViewBag.Message = allowGreetings
                ? "Saved. You'll still receive birthday and anniversary greetings, and nothing else."
                : "Saved. You won't receive any further emails.";
        }

        client.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await LoadViewDataAsync(client);
        return View(nameof(Index));
    }

    // ---- internals -----------------------------------------------------------------------------

    private async Task<Client?> FindByTokenAsync(string? token)
    {
        var trimmed = token?.Trim();
        // An empty token must never match the many clients whose token column is still the default
        // empty string -- that would unsubscribe an arbitrary client on a malformed link.
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        return await _db.Clients.FirstOrDefaultAsync(c => c.EmailPreferencesToken == trimmed);
    }

    // The broad suppression. Sets the global opt-out AND clears the pre-existing newsletter flag, so
    // one action produces one consistent result across both mechanisms rather than two that can
    // disagree -- see the note on Client.EmailOptOutAt.
    private async Task SuppressAllAsync(Client client, string source)
    {
        client.EmailOptOutAt = DateTime.UtcNow;
        client.GreetingsOptInAt = null;
        client.IsNewsletterSubscribed = false;
        client.UpdatedAt = DateTime.UtcNow;

        // Any queued Did You Know mail is already scheduled and would otherwise still go out after
        // they unsubscribed. The dispatcher re-checks consent, but retiring the rows here means the
        // Email Activity screen shows the truth instead of a queue that silently never sends.
        var queued = await _db.DidYouKnowEmailQueueItems
            .Where(q => q.ClientId == client.Id && q.SentAtUtc == null)
            .ToListAsync();
        foreach (var item in queued)
        {
            item.SentAtUtc = DateTime.UtcNow;
            item.Status = DidYouKnowQueueStatuses.Failed;
            item.FailureReason = "Recipient unsubscribed before this was sent.";
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Client {ClientId} unsubscribed from all email via {Source}; {Queued} queued item(s) retired.",
            client.Id, source, queued.Count);

        await NotifyAgentAsync(client);
    }

    // The agent is told, because a client going quiet is something an adviser wants to know about
    // rather than discover from a dwindling open rate. Best-effort: a failure here must never make
    // the unsubscribe itself fail, which is the one thing on this page that has to work.
    private async Task NotifyAgentAsync(Client client)
    {
        try
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not notify agent {AgentId} that client {ClientId} unsubscribed. The unsubscribe itself succeeded.",
                client.AgentUserId, client.Id);
        }
    }

    private async Task LoadViewDataAsync(Client client)
    {
        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == client.AgentUserId);
        var agentName = agent == null ? "your adviser" : $"{agent.FirstName} {agent.LastName}".Trim();

        ViewBag.Invalid = false;
        ViewBag.Token = client.EmailPreferencesToken;
        ViewBag.Email = client.Email;
        ViewBag.AgentName = string.IsNullOrWhiteSpace(agentName) ? "your adviser" : agentName;
        ViewBag.AgentCompany = agent?.CompanyName ?? string.Empty;
        ViewBag.IsOptedOut = client.EmailOptOutAt.HasValue;
        ViewBag.AllowGreetings = client.GreetingsOptInAt.HasValue;
    }
}
