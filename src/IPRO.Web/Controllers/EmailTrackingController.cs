using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Web.Controllers;

// 488 (2026-09-14): the receiving end of the platform's own open and click tracking. See
// EmailTrackingLinks for why it exists and how the links are built.
//
//   GET /t/o/{kind}/{token}.gif   the pixel  -> stamps OpenedAt, answers with a 1x1 GIF, always
//   GET /t/c/{kind}/{token}?u&s   the redirect -> checks the signature, stamps ClickedAt, 302s to u
//
// Anonymous by nature (a mail client loads the pixel, a reader follows the link) and reserved on
// every host in Program.cs (IsNeverShadowedPrefix "t"), because these addresses sit in people's
// inboxes for years and must keep working whatever an agent later names a page.
//
// Recording goes through the SAME two recorders the provider webhooks feed, with the same event
// names ("open" / "click"), so Email Activity, the roll-ups and the SendGrid rollback path all stay
// one vocabulary. A hit on a token that matches nothing is not an error and is not reported to the
// caller: the pixel is served regardless, so the endpoint cannot be used to discover which tokens
// exist, and a broken-image glyph never appears in a card.
[AllowAnonymous]
[Route("t")]
public class EmailTrackingController : Controller
{
    // A 1x1 transparent GIF, 43 bytes. The smallest thing every mail client will load.
    private static readonly byte[] Gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    private readonly IPRODbContext _db;
    private readonly INewsLetterService _newsletters;
    private readonly IEmailDeliveryTracker _tracker;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailTrackingController> _logger;

    public EmailTrackingController(
        IPRODbContext db,
        INewsLetterService newsletters,
        IEmailDeliveryTracker tracker,
        IConfiguration configuration,
        ILogger<EmailTrackingController> logger)
    {
        _db = db;
        _newsletters = newsletters;
        _tracker = tracker;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet("o/{kind}/{token}.gif")]
    public async Task<IActionResult> Open(string kind, string token)
    {
        NeverCache();
        await RecordSafelyAsync(kind, token, "open", "platform pixel");
        return File(Gif, "image/gif");
    }

    [HttpGet("c/{kind}/{token}")]
    public async Task<IActionResult> Click(string kind, string token, string? u, string? s)
    {
        NeverCache();

        // Signed by the platform at send time, or it does not redirect: without this check the
        // endpoint would be an open redirector on app.iproadvisers.com. IsTrackable is the same
        // rule that decided what was rewritten, so a target that could never have been signed is
        // refused even before the signature is looked at.
        // 493: verified against the signing key AND the previous one (Email__TrackingSigningKeyPrevious),
        // so the key can be rotated without every link already in an inbox answering "not valid".
        var keys = EmailTrackingLinks.VerificationKeys(_configuration);
        if (string.IsNullOrWhiteSpace(u)
            || keys.Count == 0
            || !EmailTrackingLinks.IsTrackable(u)
            || !EmailTrackingLinks.VerifySignature(kind ?? string.Empty, token ?? string.Empty, u, s, keys))
        {
            return BadRequest("This link is not valid.");
        }

        await RecordSafelyAsync(kind, token, "click", "platform redirect");
        return Redirect(u);
    }

    // Mail clients and their image proxies cache aggressively; a cached pixel is an open that is
    // never seen. Likewise a cached redirect would skip the click.
    private void NeverCache()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";
    }

    // Recording must never break the response: the reader still gets the image or the page even
    // if the database is having a moment. The loss is one open or click, logged.
    private async Task RecordSafelyAsync(string? kind, string? token, string eventName, string reason)
    {
        // The column default is '' on rows that were never sent or pre-date 488. An empty token in
        // the URL must not match them -- the same trap EmailPreferencesController guards against.
        if (string.IsNullOrWhiteSpace(token)) return;

        try
        {
            await RecordAsync((kind ?? string.Empty).Trim().ToLowerInvariant(), token.Trim(), eventName, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Platform tracking could not record '{Event}' for a {Kind} token.", eventName, kind);
        }
    }

    // One lookup per kind, on the indexed TrackingToken column of that kind's table. The kind is
    // in the URL, so no cross-table search is needed (unlike the ACS webhook, which only has a
    // message id). The provider message id already on the row is passed through so the recorders
    // behave exactly as they do for a provider event.
    private async Task RecordAsync(string kind, string token, string eventName, string reason)
    {
        var now = DateTime.UtcNow;
        switch (kind)
        {
            case "newsletter":
            {
                var row = await _db.NewsLetterRecipients.AsNoTracking()
                    .Where(r => r.TrackingToken == token)
                    .Select(r => new { r.Id, r.SendGridMessageId, r.OpenedAt, r.ClickedAt }).FirstOrDefaultAsync();
                if (row != null && !AlreadyRecorded(eventName, row.OpenedAt, row.ClickedAt))
                    await _newsletters.RecordRecipientEventAsync(row.Id, eventName, NullIfEmpty(row.SendGridMessageId), reason, now);
                break;
            }
            case "drip":
            {
                var row = await _db.DripCampaignStepSends.AsNoTracking()
                    .Where(r => r.TrackingToken == token)
                    .Select(r => new { r.Id, r.SendGridMessageId, r.OpenedAt, r.ClickedAt }).FirstOrDefaultAsync();
                if (row != null && !AlreadyRecorded(eventName, row.OpenedAt, row.ClickedAt))
                    await _newsletters.RecordDripStepEventAsync(row.Id, eventName, NullIfEmpty(row.SendGridMessageId), reason, now);
                break;
            }
            case "ecard":
            {
                var row = await _db.ECardRecipients.AsNoTracking()
                    .Where(r => r.TrackingToken == token)
                    .Select(r => new { r.Id, r.SendGridMessageId, r.OpenedAt, r.ClickedAt }).FirstOrDefaultAsync();
                if (row != null && !AlreadyRecorded(eventName, row.OpenedAt, row.ClickedAt))
                    await _tracker.RecordAsync("ecard", row.Id, eventName, NullIfEmpty(row.SendGridMessageId), reason, now);
                break;
            }
            case "eletter":
            {
                var row = await _db.ELetterRecipients.AsNoTracking()
                    .Where(r => r.TrackingToken == token)
                    .Select(r => new { r.Id, r.SendGridMessageId, r.OpenedAt, r.ClickedAt }).FirstOrDefaultAsync();
                if (row != null && !AlreadyRecorded(eventName, row.OpenedAt, row.ClickedAt))
                    await _tracker.RecordAsync("eletter", row.Id, eventName, NullIfEmpty(row.SendGridMessageId), reason, now);
                break;
            }
            case "poll":
            {
                var row = await _db.PollRecipients.AsNoTracking()
                    .Where(r => r.TrackingToken == token)
                    .Select(r => new { r.Id, r.SendGridMessageId, r.OpenedAt, r.ClickedAt }).FirstOrDefaultAsync();
                if (row != null && !AlreadyRecorded(eventName, row.OpenedAt, row.ClickedAt))
                    await _tracker.RecordAsync("poll", row.Id, eventName, NullIfEmpty(row.SendGridMessageId), reason, now);
                break;
            }
            case "didyouknow":
            {
                var row = await _db.DidYouKnowEmailQueueItems.AsNoTracking()
                    .Where(r => r.TrackingToken == token)
                    .Select(r => new { r.Id, r.SendGridMessageId, r.OpenedAt, r.ClickedAt }).FirstOrDefaultAsync();
                if (row != null && !AlreadyRecorded(eventName, row.OpenedAt, row.ClickedAt))
                    await _tracker.RecordAsync("didyouknow", row.Id, eventName, NullIfEmpty(row.SendGridMessageId), reason, now);
                break;
            }
            // Anything else (a mistyped or invented kind) is served the image and recorded nowhere.
        }
    }

    // 493: opens and clicks are write-once milestones. A replayed pixel -- a mail client re-fetching
    // it, or anyone who has the URL -- costs the one indexed read above and nothing else: never the
    // recorder's read-modify-write of the row and the roll-up recount of the whole send again.
    private static bool AlreadyRecorded(string eventName, DateTime? openedAt, DateTime? clickedAt) =>
        eventName == "click" ? clickedAt != null : openedAt != null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
