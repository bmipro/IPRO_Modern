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

    // The suppression itself now lives in EmailConsentService so that SendGrid's spamreport and
    // unsubscribe events reach the SAME code this page does. This page used to own it privately,
    // which is why a spam complaint suppressed nothing (JOBS-4).
    private readonly IEmailConsentService _consent;

    public EmailPreferencesController(
        IPRODbContext db,
        IEmailService email,
        IConfiguration configuration,
        ILogger<EmailPreferencesController> logger,
        IEmailConsentService consent)
    {
        _consent = consent;
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

        await _consent.SuppressAllAsync(client, "one-click");
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
            await _consent.SuppressAllAsync(client, "link");
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
            await _consent.ResubscribeAsync(client);
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

    // SuppressAllAsync and NotifyAgentAsync used to live here as private methods. They moved to
    // EmailConsentService / UnsubscribeNotifier so that EVERY suppression path shares them -- this
    // page, SendGrid spamreport events, and SendGrid unsubscribe events. While they were private to
    // this controller, a spam complaint set nothing at all and the client kept receiving e-cards,
    // e-letters, polls and Did You Know mail (JOBS-4, 2026-08-14 ultra-audit).

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
