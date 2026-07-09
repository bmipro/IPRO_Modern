using System.Security.Claims;
using System.Net;
using System.Text.Json;
using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Web.Controllers;

[Authorize]
public class NewsletterController : Controller
{
    private readonly INewsLetterService _newsletters;
    private readonly IClientService _clients;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IUnitOfWork _uow;
    private readonly NewsLetterDispatcher _dispatcher;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public NewsletterController(INewsLetterService newsletters, IClientService clients, IPackageEntitlementService entitlements, IUnitOfWork uow, NewsLetterDispatcher dispatcher) { _newsletters = newsletters; _clients = clients; _entitlements = entitlements; _uow = uow; _dispatcher = dispatcher; }

    public async Task<IActionResult> Index() { var gate = await RequireNewsletterAccessAsync(); if (gate != null) return gate; await LoadAgentTimeZoneAsync(); return View(await _newsletters.GetByAgentAsync(AgentId)); }
    public async Task<IActionResult> Create()
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        await LoadNewsletterContextAsync();
        return View(new NewsLetter());
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(NewsLetter model)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        PrepareNewsletterForValidation(model);
        ValidateNewsletter(model);
        if (!ModelState.IsValid)
        {
            await LoadNewsletterContextAsync();
            return View(model);
        }

        var nl = await _newsletters.CreateAsync(model);
        TempData["Success"] = "Newsletter draft saved.";
        return RedirectToAction(nameof(Preview), new { id = nl.Id });
    }
    public async Task<IActionResult> Edit(int id)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        var nl = await _newsletters.GetByIdAsync(id);
        if (nl == null || nl.AgentUserId != AgentId) return NotFound();

        await LoadNewsletterContextAsync();
        ViewBag.Articles = await _newsletters.GetArticlesAsync(id);
        ViewBag.Recipients = await _newsletters.GetRecipientsAsync(id);
        return View(nl);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(NewsLetter model)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        var existing = await _newsletters.GetByIdAsync(model.Id);
        if (existing == null || existing.AgentUserId != AgentId) return NotFound();

        PrepareNewsletterForValidation(model);
        ValidateNewsletter(model);
        if (!ModelState.IsValid)
        {
            await LoadNewsletterContextAsync();
            ViewBag.Articles = await _newsletters.GetArticlesAsync(model.Id);
            return View(model);
        }

        existing.Subject = model.Subject;
        existing.HtmlBody = model.HtmlBody;
        existing.TextBody = model.TextBody;
        await _newsletters.UpdateAsync(existing);
        TempData["Success"] = "Newsletter updated.";
        return RedirectToAction(nameof(Preview), new { id = existing.Id });
    }
    public async Task<IActionResult> Preview(int id)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        var nl = await _newsletters.GetByIdAsync(id);
        if (nl == null || nl.AgentUserId != AgentId) return NotFound();

        await LoadNewsletterContextAsync();
        ViewBag.Articles = await _newsletters.GetArticlesAsync(id);
        var sends = (await _newsletters.GetSendsAsync(id)).OrderByDescending(s => s.ScheduledAt).ToList();
        ViewBag.Sends = sends;
        ViewBag.Recipients = sends.Any()
            ? await _newsletters.GetRecipientsForSendAsync(sends.First().Id)
            : Enumerable.Empty<NewsLetterRecipient>();
        return View(nl);
    }
    public async Task<IActionResult> Send(int id)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        var nl = await _newsletters.GetByIdAsync(id);
        if (nl == null || nl.AgentUserId != AgentId) return NotFound();

        await LoadSendContextAsync();
        return View(new NewsLetterSendViewModel
        {
            NewsLetterId = nl.Id,
            Subject = nl.Subject,
            SendNow = true,
            ScheduledAt = ViewBag.AgentNow is DateTime now ? now.AddMinutes(5) : DateTime.Now.AddMinutes(5)
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(NewsLetterSendViewModel model)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        var nl = await _newsletters.GetByIdAsync(model.NewsLetterId);
        if (nl == null || nl.AgentUserId != AgentId) return NotFound();

        model.Subject = nl.Subject;
        ValidateSendRequest(model);
        if (!ModelState.IsValid)
        {
            await LoadSendContextAsync();
            return View(model);
        }

        var agentTimeZone = await GetAgentTimeZoneAsync();
        var localSendAt = model.SendNow
            ? AgentTimeZoneHelper.FromUtc(DateTime.UtcNow.AddMinutes(1), agentTimeZone)
            : model.ScheduledAt!.Value;

        var send = await _newsletters.ScheduleSendAsync(
            model.NewsLetterId,
            AgentId,
            AgentTimeZoneHelper.ToUtc(localSendAt, agentTimeZone),
            model.AudienceType,
            model.ClientCategoryId,
            model.ClientId);

        if (send != null && model.SendNow)
        {
            await _dispatcher.DispatchSendAsync(send.Id);
        }

        TempData["Success"] = send == null
            ? "Newsletter could not be scheduled."
            : model.SendNow
                ? "Newsletter sent."
                : $"Newsletter send scheduled for {localSendAt:MMM d, yyyy h:mm tt} {GetShortTimeZoneLabel(agentTimeZone)}.";
        return RedirectToAction(nameof(Preview), new { id = model.NewsLetterId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Schedule(int id, DateTime scheduledAt)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        var nl = await _newsletters.GetByIdAsync(id);
        if (nl == null || nl.AgentUserId != AgentId) return NotFound();

        var agentTimeZone = await GetAgentTimeZoneAsync();
        var send = await _newsletters.ScheduleSendAsync(id, AgentId, AgentTimeZoneHelper.ToUtc(scheduledAt, agentTimeZone));
        TempData["Success"] = send == null
            ? "Newsletter could not be scheduled."
            : $"Newsletter send scheduled for {scheduledAt:MMM d, yyyy h:mm tt} {GetShortTimeZoneLabel(agentTimeZone)}.";
        return RedirectToAction(nameof(Preview), new { id });
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddArticle(NewsLetterArticle article) { var gate = await RequireNewsletterAccessAsync(); if (gate != null) return gate; await _newsletters.AddArticleAsync(article); return RedirectToAction(nameof(Edit), new { id = article.NewsLetterId }); }
    public async Task<IActionResult> Subscribers() { var gate = await RequireNewsletterAccessAsync(); return gate ?? View(await _clients.GetNewsletterSubscribersAsync(AgentId)); }

    [AllowAnonymous]
    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> SendGridEvents([FromBody] JsonElement events)
    {
        if (events.ValueKind != JsonValueKind.Array)
        {
            return BadRequest();
        }

        foreach (var item in events.EnumerateArray())
        {
            var recipientId = ReadCustomInt(item, "newsletter_recipient_id");
            if (recipientId <= 0) continue;

            var eventName = ReadString(item, "event");
            var providerMessageId = ReadString(item, "sg_message_id");
            var reason = ReadString(item, "reason");
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = ReadString(item, "response");
            }

            var occurredAt = ReadUnixTimestamp(item, "timestamp") ?? DateTime.UtcNow;
            await _newsletters.RecordRecipientEventAsync(recipientId, eventName, providerMessageId, reason, occurredAt);
        }

        return Ok();
    }

    private async Task LoadNewsletterContextAsync()
    {
        var subscribers = await _clients.GetNewsletterSubscribersAsync(AgentId);
        ViewBag.SubscriberCount = subscribers.Count();
        await LoadAgentTimeZoneAsync();
    }

    private async Task LoadSendContextAsync()
    {
        await LoadNewsletterContextAsync();
        ViewBag.AccountTypes = (await _uow.ClientCategories.FindAsync(c => c.AgentUserId == AgentId))
            .OrderBy(c => c.Name)
            .ToList();
        ViewBag.Clients = (await _uow.Clients.FindAsync(c => c.AgentUserId == AgentId && !string.IsNullOrWhiteSpace(c.Email)))
            .OrderBy(c => c.LastName)
            .ThenBy(c => c.FirstName)
            .ToList();
    }

    private async Task LoadAgentTimeZoneAsync()
    {
        var timeZone = await GetAgentTimeZoneAsync();
        ViewBag.AgentTimeZone = timeZone;
        ViewBag.AgentTimeZoneLabel = GetShortTimeZoneLabel(timeZone);
        ViewBag.AgentNow = AgentTimeZoneHelper.FromUtc(DateTime.UtcNow, timeZone);
    }

    private async Task<string> GetAgentTimeZoneAsync()
    {
        var agent = await _uow.AgentUsers.GetByIdAsync(AgentId);
        return AgentTimeZoneHelper.Normalize(agent?.TimeZone);
    }

    private async Task<IActionResult?> RequireNewsletterAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.Newsletters);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }

    private void PrepareNewsletterForValidation(NewsLetter model)
    {
        model.AgentUserId = AgentId;
        model.Subject = model.Subject?.Trim() ?? "";
        model.HtmlBody = model.HtmlBody?.Trim() ?? "";
        model.TextBody = model.TextBody?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(model.HtmlBody) && !string.IsNullOrWhiteSpace(model.TextBody))
        {
            model.HtmlBody = ConvertPlainTextToHtml(model.TextBody);
        }

        foreach (var key in new[]
        {
            nameof(NewsLetter.HtmlBody),
            nameof(NewsLetter.TextBody),
            nameof(NewsLetter.AgentUser),
            nameof(NewsLetter.Articles),
            nameof(NewsLetter.Sends),
            nameof(NewsLetter.AgentUserId),
            nameof(NewsLetter.Status),
            nameof(NewsLetter.ScheduledAt),
            nameof(NewsLetter.SentAt)
        })
        {
            ModelState.Remove(key);
        }
    }

    private void ValidateNewsletter(NewsLetter model)
    {
        if (string.IsNullOrWhiteSpace(model.Subject))
        {
            ModelState.AddModelError(nameof(NewsLetter.Subject), "Subject line is required.");
        }

        if (string.IsNullOrWhiteSpace(model.HtmlBody) && string.IsNullOrWhiteSpace(model.TextBody))
        {
            ModelState.AddModelError(nameof(NewsLetter.HtmlBody), "Newsletter body or plain text version is required.");
        }
    }

    private void ValidateSendRequest(NewsLetterSendViewModel model)
    {
        if (model.AudienceType == NewsLetterAudienceType.AccountType && !model.ClientCategoryId.HasValue)
        {
            ModelState.AddModelError(nameof(model.ClientCategoryId), "Choose an account type.");
        }

        if (model.AudienceType == NewsLetterAudienceType.IndividualClient && !model.ClientId.HasValue)
        {
            ModelState.AddModelError(nameof(model.ClientId), "Choose a client.");
        }

        if (!model.SendNow && !model.ScheduledAt.HasValue)
        {
            ModelState.AddModelError(nameof(model.ScheduledAt), "Choose a send date and time.");
        }
    }

    private static string ConvertPlainTextToHtml(string text)
    {
        var paragraphs = text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.TrimEntries);

        return string.Join(
            Environment.NewLine,
            paragraphs.Select(line =>
                string.IsNullOrWhiteSpace(line)
                    ? "<br>"
                    : $"<p>{WebUtility.HtmlEncode(line)}</p>"));
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : string.Empty;
    }

    private static int ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static int ReadCustomInt(JsonElement element, string name)
    {
        var direct = ReadInt(element, name);
        if (direct > 0) return direct;

        foreach (var containerName in new[] { "custom_args", "unique_args", "smtp-id" })
        {
            if (!element.TryGetProperty(containerName, out var container) || container.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var nested = ReadInt(container, name);
            if (nested > 0) return nested;
        }

        return 0;
    }

    private static DateTime? ReadUnixTimestamp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }

        return long.TryParse(value.ToString(), out var parsed)
            ? DateTimeOffset.FromUnixTimeSeconds(parsed).UtcDateTime
            : null;
    }

    private static string GetShortTimeZoneLabel(string timeZone) => timeZone switch
    {
        "(GMT-06:00) Central Time (US & Canada)" => "Central",
        "(GMT-07:00) Mountain Time (US & Canada)" => "Mountain",
        "(GMT-08:00) Pacific Time (US & Canada)" => "Pacific",
        "(GMT-04:00) Atlantic Time (Canada)" => "Atlantic",
        "(GMT-03:30) Newfoundland" => "Newfoundland",
        _ => "Eastern"
    };
}
