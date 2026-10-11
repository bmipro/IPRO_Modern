using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Email;
using Microsoft.Extensions.Options;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

// One screen for every email the agent sends, whatever sent it.
//
// The alternative was a Send History + Delivery Tracking page per feature -- the Newsletter Preview
// page cloned into E-Cards, E-Letters, Polls and Did You Know. That is four near-identical pages to
// build and five to maintain, and the fifth sender added later would silently have no tracking at
// all, which is exactly how E-Cards ended up shipping with none.
//
// Here a sender appears on this screen as soon as it contributes rows to LoadSendsAsync. Feature
// pages keep their own lists; this is the place to answer "did it arrive?".
//
// Deliberately NOT gated on a package feature: an agent must always be able to see what was sent
// from their own account, including from a feature their package no longer includes. The per-type
// feature pages keep their own gates.
[Authorize]
public class EmailActivityController : Controller
{
    private readonly IPRODbContext _db;
    private readonly EmailSettings _email;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public EmailActivityController(IPRODbContext db, IOptions<EmailSettings> email)
    {
        _db = db;
        _email = email.Value;
    }

    // 565 (2026-10-11): the list opens on the last 30 days, 25 to a page. A tab holds what needs
    // attention, and a search finds an email by its subject or by who received it. The rules are in
    // EmailActivityListing; this method gathers the rows for it.
    public async Task<IActionResult> Index(string type = "all", string period = EmailActivityListing.DefaultPeriod, string? q = null, int page = 1)
    {
        var chosenPeriod = EmailActivityListing.PeriodFor(period);
        var since = EmailActivityListing.Since(chosenPeriod, DateTime.UtcNow);
        // Built once per request (493). The tab badges count the period and the search, never the tab,
        // so they do not change as the tabs are clicked.
        var inPeriod = EmailActivityListing.InPeriod(await LoadSendsAsync(since), since);
        var search = EmailActivityListing.CleanQuery(q);
        var reached = search.Length == 0 ? null : await EmailActivityQueries.RecipientMatchesAsync(_db, AgentId, search, inPeriod);
        var listing = EmailActivityListing.Build(inPeriod, type, chosenPeriod, search, page, reached);
        var rows = listing.Rows;

        ViewBag.Listing = listing;
        ViewBag.Counts = listing.Counts;
        ViewBag.AgentTimeZone = await GetAgentTimeZoneAsync();
        ViewBag.Filter = listing.Type;
        // 444: lets the screen say "not tracked" instead of an ambiguous dash while nothing is
        // injecting open/click tracking. 488: the platform's own pixel and redirect count too; the
        // view keeps saying "not tracked" for invoice rows, which are not instrumented.
        ViewBag.EngagementTracking = _email.EngagementTrackingEnabled || _email.PlatformTrackingEnabled;

        return View(rows);
    }

    // Both forms. An attribute route REPLACES the conventional one, so without the prefixed twin
    // this 404s at /portal/EmailActivity/Details/... where every portal link points.
    // See INVARIANTS.md rule 2 and NewsletterController.CreateFromTemplate.
    [HttpGet("EmailActivity/Details/{type}/{id:int}")]
    [HttpGet("portal/EmailActivity/Details/{type}/{id:int}")]
    public async Task<IActionResult> Details(string type, int id)
    {
        var normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();

        var send = (await LoadSendsAsync()).FirstOrDefault(r => r.TypeKey == normalizedType && r.Id == id);
        // Not found OR not this agent's -- LoadSendsAsync is already scoped to AgentId, so a send
        // belonging to someone else simply is not in the list. No separate ownership check needed,
        // and no way to probe another agent's ids.
        if (send == null) return NotFound();

        ViewBag.Send = send;
        // 565: "Back" returns to the list as it was left (its period, tab, search and page).
        ViewBag.BackAddress = EmailActivityListing.BackAddress(Request.Headers.Referer.ToString(), Request.Host.Host);
        ViewBag.AgentTimeZone = await GetAgentTimeZoneAsync();
        // Invoice mail is not instrumented (488): the invoice page records its own views instead.
        ViewBag.EngagementTracking = _email.EngagementTrackingEnabled
            || (_email.PlatformTrackingEnabled && normalizedType != "invoice");
        // 562: an invoice email shows whether the client viewed the invoice itself.
        if (normalizedType == "invoice")
            ViewBag.InvoiceView = await IPRO.Web.Infrastructure.EmailActivityQueries.InvoiceViewAsync(_db, AgentId, id);
        return View(await LoadRecipientsAsync(normalizedType, id));
    }

    // ---- data ------------------------------------------------------------------------------

    // since (565): the start of the period the list is showing, or null for every send (the detail
    // page looks a send up whatever its age). The senders with one row per send are cut down in the
    // database; campaign steps and Did You Know are grouped from their recipients, so they arrive
    // whole and EmailActivityListing.InPeriod applies the same rule to every row afterwards.
    private async Task<List<EmailActivityRow>> LoadSendsAsync(DateTime? since = null)
    {
        var rows = new List<EmailActivityRow>();

        // Newsletters. Counts come off NewsLetterSend, which the dispatcher maintains, but
        // Delivered is derived from the recipient rows because there is no TotalDelivered column.
        var newsletterSends = await _db.NewsLetterSends
            .AsNoTracking()
            .Where(s => s.AgentUserId == AgentId)
            .Where(s => since == null || (s.SentAt ?? s.ScheduledAt) >= since)
            .Select(s => new
            {
                s.Id,
                s.NewsLetterId,
                Subject = s.NewsLetter.Subject,
                s.AudienceLabel,
                Status = s.Status.ToString(),
                s.ScheduledAt,
                s.SentAt,
                s.TotalRecipients,
                s.TotalSent,
                s.TotalOpened,
                Delivered = _db.NewsLetterRecipients.Count(r => r.NewsLetterSendId == s.Id && r.DeliveredAt != null),
                Failed = _db.NewsLetterRecipients.Count(r =>
                    r.NewsLetterSendId == s.Id &&
                    (r.Status == NewsLetterRecipientStatus.Failed ||
                     r.Status == NewsLetterRecipientStatus.Bounced ||
                     r.Status == NewsLetterRecipientStatus.Dropped))
            })
            .ToListAsync();

        rows.AddRange(newsletterSends.Select(s => new EmailActivityRow(
            "Newsletter", "newsletter", s.Id, s.Subject, s.AudienceLabel, s.Status,
            s.SentAt ?? s.ScheduledAt, s.TotalRecipients, s.TotalSent, s.Delivered, s.TotalOpened, s.Failed)));

        // E-Cards.
        var cards = await _db.ECards
            .AsNoTracking()
            .Where(c => c.AgentUserId == AgentId)
            .Where(c => since == null || (c.SentAt ?? c.ScheduledAt) >= since)
            .Select(c => new
            {
                c.Id,
                c.Subject,
                c.Occasion,
                c.Status,
                c.ScheduledAt,
                c.SentAt,
                c.TotalRecipients,
                c.TotalSent,
                Delivered = _db.ECardRecipients.Count(r => r.ECardId == c.Id && r.DeliveredAt != null),
                Opened = _db.ECardRecipients.Count(r => r.ECardId == c.Id && r.OpenedAt != null),
                Failed = _db.ECardRecipients.Count(r => r.ECardId == c.Id && r.Status == ECardRecipientStatuses.Failed)
            })
            .ToListAsync();

        rows.AddRange(cards.Select(c => new EmailActivityRow(
            "E-Card", "ecard", c.Id, c.Subject, c.Occasion, c.Status,
            c.SentAt ?? c.ScheduledAt, c.TotalRecipients, c.TotalSent, c.Delivered, c.Opened, c.Failed)));

        // E-Letters.
        var letters = await _db.ELetters
            .AsNoTracking()
            .Where(l => l.AgentUserId == AgentId)
            .Where(l => since == null || (l.SentAt ?? l.ScheduledAt) >= since)
            .Select(l => new
            {
                l.Id,
                l.Subject,
                l.TemplateKey,
                l.Status,
                l.ScheduledAt,
                l.SentAt,
                l.TotalRecipients,
                l.TotalSent,
                Delivered = _db.ELetterRecipients.Count(r => r.ELetterId == l.Id && r.DeliveredAt != null),
                Opened = _db.ELetterRecipients.Count(r => r.ELetterId == l.Id && r.OpenedAt != null),
                Failed = _db.ELetterRecipients.Count(r => r.ELetterId == l.Id && r.Status == ELetterRecipientStatuses.Failed)
            })
            .ToListAsync();

        rows.AddRange(letters.Select(l => new EmailActivityRow(
            "E-Letter", "eletter", l.Id, l.Subject, l.TemplateKey, l.Status,
            l.SentAt ?? l.ScheduledAt, l.TotalRecipients, l.TotalSent, l.Delivered, l.Opened, l.Failed)));

        // Polls.
        var pollSends = await _db.PollSends
            .AsNoTracking()
            .Where(s => s.AgentUserId == AgentId)
            .Where(s => since == null || (s.SentAt ?? s.ScheduledAt) >= since)
            .Select(s => new
            {
                s.Id,
                Subject = _db.PollSurveys.Where(p => p.Id == s.PollSurveyId).Select(p => p.Subject).FirstOrDefault() ?? "Poll",
                s.AudienceLabel,
                Status = s.Status.ToString(),
                s.ScheduledAt,
                s.SentAt,
                s.TotalRecipients,
                s.TotalSent,
                s.TotalFailed,
                Delivered = _db.PollRecipients.Count(r => r.PollSendId == s.Id && r.DeliveredAt != null),
                Opened = _db.PollRecipients.Count(r => r.PollSendId == s.Id && r.OpenedAt != null)
            })
            .ToListAsync();

        rows.AddRange(pollSends.Select(s => new EmailActivityRow(
            "Poll", "poll", s.Id, s.Subject, s.AudienceLabel, s.Status,
            s.SentAt ?? s.ScheduledAt, s.TotalRecipients, s.TotalSent, s.Delivered, s.Opened, s.TotalFailed)));

        // Did You Know. Unlike the others there is no parent "send" row -- the queue holds one item
        // per article per client, staggered over time. Group by article so the agent sees one line
        // per article rather than a hundred, which matches how the other senders read.
        var dykItems = await _db.DidYouKnowEmailQueueItems
            .AsNoTracking()
            .Where(q => _db.Clients.Any(c => c.Id == q.ClientId && c.AgentUserId == AgentId))
            .Select(q => new
            {
                q.ArticleId,
                q.Status,
                q.SentAtUtc,
                q.DeliveredAt,
                q.OpenedAt
            })
            .ToListAsync();

        var articleTitles = await _db.Articles
            .AsNoTracking()
            .Where(a => a.AgentUserId == AgentId)
            .ToDictionaryAsync(a => a.Id, a => a.Title);

        rows.AddRange(dykItems
            .GroupBy(q => q.ArticleId)
            .Select(g => new EmailActivityRow(
                "Did You Know", "didyouknow", g.Key,
                articleTitles.TryGetValue(g.Key, out var title) ? title : $"Article #{g.Key}",
                "Website follow-up",
                g.All(q => q.SentAtUtc != null) ? "Sent" : "Sending",
                g.Max(q => q.SentAtUtc),
                g.Count(),
                g.Count(q => q.Status == DidYouKnowQueueStatuses.Sent),
                g.Count(q => q.DeliveredAt != null),
                g.Count(q => q.OpenedAt != null),
                g.Count(q => q.Status == DidYouKnowQueueStatuses.Failed))));

        rows.AddRange(await EmailActivityQueries.DripStepRowsAsync(_db, AgentId));
        rows.AddRange(await EmailActivityQueries.InvoiceRowsAsync(_db, AgentId, since)); // 452

        return rows.OrderByDescending(r => r.When ?? DateTime.MinValue).ToList();
    }

    private async Task<List<EmailRecipientRow>> LoadRecipientsAsync(string type, int id) => type switch
    {
        "drip" => await EmailActivityQueries.DripStepRecipientsAsync(_db, AgentId, id),
        "invoice" => await EmailActivityQueries.InvoiceRecipientsAsync(_db, AgentId, id), // 452
        "newsletter" => await _db.NewsLetterRecipients
            .AsNoTracking()
            .Where(r => r.NewsLetterSendId == id)
            .OrderBy(r => r.RecipientName)
            .Select(r => new EmailRecipientRow(
                r.RecipientName, r.Email, r.Status.ToString(),
                r.SentAt, r.DeliveredAt, r.OpenedAt, r.ClickedAt, r.FailureReason))
            .ToListAsync(),

        "ecard" => await _db.ECardRecipients
            .AsNoTracking()
            .Where(r => r.ECardId == id)
            .OrderBy(r => r.RecipientName)
            .Select(r => new EmailRecipientRow(
                r.RecipientName, r.Email, r.Status,
                r.SentAt, r.DeliveredAt, r.OpenedAt, r.ClickedAt, r.FailureReason))
            .ToListAsync(),

        "eletter" => await _db.ELetterRecipients
            .AsNoTracking()
            .Where(r => r.ELetterId == id)
            .OrderBy(r => r.RecipientName)
            .Select(r => new EmailRecipientRow(
                r.RecipientName, r.Email, r.Status,
                r.SentAt, r.DeliveredAt, r.OpenedAt, r.ClickedAt, r.FailureReason))
            .ToListAsync(),

        "poll" => await _db.PollRecipients
            .AsNoTracking()
            .Where(r => r.PollSendId == id)
            .OrderBy(r => r.RecipientName)
            .Select(r => new EmailRecipientRow(
                r.RecipientName, r.Email, r.Status.ToString(),
                r.SentAt, r.DeliveredAt, r.OpenedAt, r.ClickedAt, r.FailureReason))
            .ToListAsync(),

        // id is the ArticleId here, matching the grouping in LoadSendsAsync.
        "didyouknow" => await _db.DidYouKnowEmailQueueItems
            .AsNoTracking()
            .Where(q => q.ArticleId == id && _db.Clients.Any(c => c.Id == q.ClientId && c.AgentUserId == AgentId))
            .Join(_db.Clients, q => q.ClientId, c => c.Id, (q, c) => new EmailRecipientRow(
                ((c.FirstName + " " + c.LastName).Trim() == "" ? c.Email : (c.FirstName + " " + c.LastName).Trim()),
                c.Email, q.Status,
                q.SentAtUtc, q.DeliveredAt, q.OpenedAt, q.ClickedAt, q.FailureReason))
            .ToListAsync(),

        _ => new List<EmailRecipientRow>()
    };

    private async Task<string> GetAgentTimeZoneAsync()
    {
        var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == AgentId);
        return AgentTimeZoneHelper.Normalize(agent?.TimeZone);
    }
}

// One row per send, whatever produced it. Detail carries whichever secondary label makes sense for
// that sender (occasion for a card, template for a letter, audience for a newsletter or poll).
public record EmailActivityRow(
    string TypeLabel,
    string TypeKey,
    int Id,
    string Subject,
    string Detail,
    string Status,
    DateTime? When,
    int Recipients,
    int Sent,
    int Delivered,
    int Opened,
    int Failed)
{
    // 493: a send the dispatcher paused (no send slot inside the gate's bound, or a provider hiccup)
    // goes back to Scheduled with its running total. Half-way through is not "not started".
    public string DisplayStatus => Status == "Scheduled" && Sent > 0 ? "In progress" : Status;
}

public record EmailRecipientRow(
    string Name,
    string Email,
    string Status,
    DateTime? SentAt,
    DateTime? DeliveredAt,
    DateTime? OpenedAt,
    DateTime? ClickedAt,
    string Issue);
