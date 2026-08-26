using System.Security.Claims;
using System.Net;
using System.Text.Json;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class NewsletterController : Controller
{
    private readonly INewsLetterService _newsletters;
    private readonly IClientService _clients;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IUnitOfWork _uow;
    private readonly IPRODbContext _db;
    private readonly NewsLetterDispatcher _dispatcher;
    private readonly IEmailService _email;
    private readonly IAiSuggestionService _aiSuggestions;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NewsletterController> _logger;
    // Handles SendGrid events for E-Cards, E-Letters, Polls and Did You Know. The webhook endpoint
    // lives on this controller for historical reasons -- newsletters were the first sender to have
    // one -- so every other sender's events arrive here too.
    private readonly IEmailDeliveryTracker _deliveryTracker;
    private readonly IEmailConsentService _consent;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public NewsletterController(INewsLetterService newsletters, IClientService clients, IPackageEntitlementService entitlements, IUnitOfWork uow, IPRODbContext db, NewsLetterDispatcher dispatcher, IEmailService email, IAiSuggestionService aiSuggestions, IEmailDeliveryTracker deliveryTracker, IEmailConsentService consent, IConfiguration configuration, ILogger<NewsletterController> logger) { _newsletters = newsletters; _clients = clients; _entitlements = entitlements; _uow = uow; _db = db; _dispatcher = dispatcher; _email = email; _aiSuggestions = aiSuggestions; _deliveryTracker = deliveryTracker; _consent = consent; _configuration = configuration; _logger = logger; }

    public async Task<IActionResult> Index() { var gate = await RequireNewsletterAccessAsync(); if (gate != null) return gate; await LoadAgentTimeZoneAsync(); return View(await _newsletters.GetByAgentAsync(AgentId)); }
    public async Task<IActionResult> Create()
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        await LoadNewsletterContextAsync();
        await LoadTemplatePickerAsync();
        ViewBag.AiAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.AiDailyAssistant);
        return View(new NewsLetter());
    }
    // BOTH routes, deliberately. An attribute route REPLACES the conventional one, so an action
    // declaring only the bare path is unreachable under /portal -- which is where every portal link
    // now points. "Use this template" 404'd for exactly this reason (2026-08-08). The bare form is
    // kept because it may sit in older links. Same pattern as ClientsController.FollowUpQueue.
    [HttpGet("Newsletter/CreateFromTemplate/{templateId}")]
    [HttpGet("portal/Newsletter/CreateFromTemplate/{templateId}")]
    public async Task<IActionResult> CreateFromTemplate(int templateId)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        var template = await _db.NewsLetterTemplates.FirstOrDefaultAsync(t => t.Id == templateId && t.IsActive);
        if (template == null) return NotFound();

        await LoadNewsletterContextAsync();
        await LoadTemplatePickerAsync();
        return View("Create", new NewsLetter
        {
            Subject = template.Subject,
            HtmlBody = template.HtmlBody,
            TextBody = template.TextBody
        });
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
            await LoadTemplatePickerAsync();
            return View(model);
        }

        var nl = await _newsletters.CreateAsync(model);
        TempData["Success"] = "Newsletter draft saved.";
        return RedirectToAction(nameof(Preview), new { id = nl.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DraftWithAi(string topic)
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.AiDailyAssistant);
        if (!access.IsIncluded)
        {
            return Json(new { success = false, error = access.UpgradeMessage });
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            return Json(new { success = false, error = "Enter a topic first, then draft with AI." });
        }

        var result = await _aiSuggestions.DraftNewsletterAsync(topic.Trim());
        if (result.InputTokens > 0 || result.OutputTokens > 0)
        {
            await AiUsageRecorder.RecordAsync(_db, 1, result.InputTokens, result.OutputTokens);
            await _db.SaveChangesAsync();
        }

        if (string.IsNullOrWhiteSpace(result.BodyHtml))
        {
            return Json(new { success = false, error = "AI drafting isn't available right now — try again in a moment, or write the newsletter yourself." });
        }

        return Json(new { success = true, subject = result.Subject ?? "", body = result.BodyHtml });
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
        ViewBag.AvailableArticles = await _db.Articles
            .Where(a => a.AgentUserId == AgentId)
            .OrderByDescending(a => a.UpdatedAt)
            .ToListAsync();
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
        existing.BannerUrl = model.BannerUrl;
        existing.Edition = model.Edition?.Trim();
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
        var previewArticles = await _newsletters.GetArticlesAsync(id);
        ViewBag.Articles = previewArticles;
        ViewBag.SidebarCtas = NewsLetterSidebarCtas.FromJson(nl.SidebarCtasJson);
        var previewAgent = await _uow.AgentUsers.GetByIdAsync(AgentId);
        ViewBag.WrappedHtmlBody = previewAgent == null ? nl.HtmlBody : NewsletterHtmlComposer.Wrap(nl, previewAgent, GetRequestBaseUrl(), previewArticles, (List<NewsLetterCta>)ViewBag.SidebarCtas);
        var sends = (await _newsletters.GetSendsAsync(id)).OrderByDescending(s => s.ScheduledAt).ToList();
        ViewBag.Sends = sends;
        ViewBag.Recipients = sends.Any()
            ? await _newsletters.GetRecipientsForSendAsync(sends.First().Id)
            : Enumerable.Empty<NewsLetterRecipient>();
        return View(nl);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Duplicate(int id)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        var copy = await _newsletters.DuplicateAsync(id, AgentId);
        if (copy == null) return NotFound();

        TempData["Success"] = "Newsletter copied as a new reusable draft.";
        return RedirectToAction(nameof(Edit), new { id = copy.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelSend(int id, int newsletterId)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        var cancelled = await _newsletters.CancelSendAsync(id, AgentId);
        TempData[cancelled ? "Success" : "Error"] =
            cancelled ? "Scheduled newsletter send cancelled." : "Scheduled newsletter send could not be cancelled.";
        return RedirectToAction(nameof(Preview), new { id = newsletterId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SendTest(int id)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;

        var nl = await _newsletters.GetByIdAsync(id);
        if (nl == null || nl.AgentUserId != AgentId) return NotFound();

        var agent = await _uow.AgentUsers.GetByIdAsync(AgentId);
        if (agent == null || string.IsNullOrWhiteSpace(agent.Email))
        {
            TempData["Error"] = "Your agent profile does not have an email address for test sends.";
            return RedirectToAction(nameof(Preview), new { id });
        }

        var testSendArticles = await _newsletters.GetArticlesAsync(id);
        var testSendCtas = NewsLetterSidebarCtas.FromJson(nl.SidebarCtasJson);
        var htmlBody = $"""
            <div style="margin-bottom:16px;padding:12px 14px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e3a8a;font-family:Arial,sans-serif;">
              <strong>Test send:</strong> This preview was sent only to you. No clients received it.
            </div>
            {NewsletterHtmlComposer.Wrap(nl, agent, GetRequestBaseUrl(), testSendArticles, testSendCtas)}
            """;
        var result = await _email.SendDetailedAsync(
            agent.Email,
            $"{agent.FirstName} {agent.LastName}".Trim(),
            $"[TEST] {nl.Subject}",
            htmlBody,
            nl.TextBody);

        TempData[result.Success ? "Success" : "Error"] = result.Success
            ? $"Test newsletter sent to {agent.Email}."
            : result.Message;
        return RedirectToAction(nameof(Preview), new { id });
    }

    [AllowAnonymous]
    public async Task<IActionResult> Unsubscribe(string token)
    {
        var trimmedToken = token?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedToken))
        {
            ViewBag.Success = false;
            return View();
        }

        var recipient = (await _uow.NewsLetterRecipients.FindAsync(r => r.UnsubscribeToken == trimmedToken)).FirstOrDefault();
        if (recipient != null)
        {
            recipient.Status = NewsLetterRecipientStatus.Unsubscribed;
            recipient.LastEvent = "unsubscribe";
            recipient.UpdatedAt = DateTime.UtcNow;
            _uow.NewsLetterRecipients.Update(recipient);

            if (recipient.ClientId.HasValue)
            {
                var client = await _uow.Clients.GetByIdAsync(recipient.ClientId.Value);
                if (client != null)
                {
                    // Clicking unsubscribe in a newsletter is a person saying "stop", not "send me
                    // the cards and letters instead". This used to set IsNewsletterSubscribed only,
                    // which is what made the promise in Client.cs untrue.
                    await _consent.SuppressAllAsync(client, "newsletter-footer-link");
                }
            }

            await _uow.SaveChangesAsync();
            ViewBag.Success = true;
            ViewBag.Email = recipient.Email;
            ViewBag.Channel = "newsletter";
            return View();
        }

        var enrollment = await _db.DripCampaignEnrollments.FirstOrDefaultAsync(e => e.UnsubscribeToken == trimmedToken);
        if (enrollment != null)
        {
            enrollment.Status = DripCampaignEnrollmentStatus.Cancelled;
            enrollment.CancelledAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == enrollment.ClientId);
            // Cancelling the one enrollment was all this did. Someone who clicks unsubscribe in a
            // drip email is asking to stop hearing from this adviser, not to be moved onto their
            // newsletter -- the same reading the one-click header path already takes.
            if (client != null) await _consent.SuppressAllAsync(client, "drip-footer-link");

            ViewBag.Success = true;
            ViewBag.Email = client?.Email ?? string.Empty;
            ViewBag.Channel = "series";
            return View();
        }

        ViewBag.Success = false;
        return View();
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
    public async Task<IActionResult> AddArticle(NewsLetterArticle article)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;
        var owned = await _newsletters.GetByIdAsync(article.NewsLetterId);
        if (owned == null || owned.AgentUserId != AgentId) return NotFound();

        var existing = await _newsletters.GetArticlesAsync(article.NewsLetterId);
        article.SortOrder = existing.Count();
        article.ImageUrl = NormalizeUrl(article.ImageUrl);
        article.Content = HtmlContentSanitizer.Sanitize(article.Content);
        await _newsletters.AddArticleAsync(article);
        TempData["Success"] = "Article added.";
        return RedirectToAction(nameof(Edit), new { id = article.NewsLetterId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddArticleFromLibrary(int newsletterId, int articleId)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;
        var owned = await _newsletters.GetByIdAsync(newsletterId);
        if (owned == null || owned.AgentUserId != AgentId) return NotFound();

        var source = await _db.Articles.FirstOrDefaultAsync(a => a.Id == articleId && a.AgentUserId == AgentId);
        if (source == null) return NotFound();

        var existing = await _newsletters.GetArticlesAsync(newsletterId);
        await _newsletters.AddArticleAsync(new NewsLetterArticle
        {
            NewsLetterId = newsletterId,
            Title = source.Title,
            Content = HtmlContentSanitizer.Sanitize(source.Content),
            ImageUrl = NormalizeUrl(source.ImageUrl),
            SortOrder = existing.Count()
        });
        TempData["Success"] = $"\"{source.Title}\" added from your Articles library.";
        return RedirectToAction(nameof(Edit), new { id = newsletterId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteArticle(int id, int newsletterId)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;
        var owned = await _newsletters.GetByIdAsync(newsletterId);
        if (owned == null || owned.AgentUserId != AgentId) return NotFound();

        var articleOwned = (await _newsletters.GetArticlesAsync(newsletterId)).Any(a => a.Id == id);
        if (articleOwned) await _newsletters.RemoveArticleAsync(id);
        TempData["Success"] = "Article removed.";
        return RedirectToAction(nameof(Edit), new { id = newsletterId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddSidebarCta(int newsletterId, string label, string url)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;
        var nl = await _newsletters.GetByIdAsync(newsletterId);
        if (nl == null || nl.AgentUserId != AgentId) return NotFound();

        var normalized = NormalizeUrl(url);
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(normalized))
        {
            TempData["Error"] = "Enter a label and a complete http or https URL.";
            return RedirectToAction(nameof(Edit), new { id = newsletterId });
        }
        var ctas = NewsLetterSidebarCtas.FromJson(nl.SidebarCtasJson);
        ctas.Add(new NewsLetterCta { Label = label.Trim(), Url = normalized, SortOrder = ctas.Count });
        nl.SidebarCtasJson = NewsLetterSidebarCtas.ToJson(ctas);
        await _newsletters.UpdateAsync(nl);
        TempData["Success"] = "Sidebar button added.";
        return RedirectToAction(nameof(Edit), new { id = newsletterId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSidebarCta(int newsletterId, string ctaId)
    {
        var gate = await RequireNewsletterAccessAsync();
        if (gate != null) return gate;
        var nl = await _newsletters.GetByIdAsync(newsletterId);
        if (nl == null || nl.AgentUserId != AgentId) return NotFound();

        var ctas = NewsLetterSidebarCtas.FromJson(nl.SidebarCtasJson);
        ctas.RemoveAll(c => c.Id == ctaId);
        nl.SidebarCtasJson = NewsLetterSidebarCtas.ToJson(ctas);
        await _newsletters.UpdateAsync(nl);
        TempData["Success"] = "Sidebar button removed.";
        return RedirectToAction(nameof(Edit), new { id = newsletterId });
    }

    private static string NormalizeUrl(string? value)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal))
        {
            return value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.ToString()
            : string.Empty;
    }
    public async Task<IActionResult> Subscribers() { var gate = await RequireNewsletterAccessAsync(); return gate ?? View(await _clients.GetNewsletterSubscribersAsync(AgentId)); }

    // The custom arg each sender tags its outgoing mail with, and the entity kind it maps to.
    // Newsletters and drip campaigns are handled separately above because they have their own
    // recorder on NewsLetterService that predates this table.
    private static readonly (string CustomArg, string EntityKind)[] TrackedRecipientArgs =
    {
        ("ecard_recipient_id", "ecard"),
        ("eletter_recipient_id", "eletter"),
        ("poll_recipient_id", "poll"),
        ("dyk_queue_item_id", "didyouknow")
    };

    [AllowAnonymous]
    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> SendGridEvents()
    {
        var publicKey = _configuration["Email:SendGridEventWebhookPublicKey"];
        var signature = Request.Headers["X-Twilio-Email-Event-Webhook-Signature"].ToString();
        var timestamp = Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"].ToString();

        Request.EnableBuffering();
        string rawBody;
        using (var reader = new StreamReader(Request.Body, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync();
        }
        Request.Body.Position = 0;

        if (string.IsNullOrWhiteSpace(publicKey))
        {
            _logger.LogWarning("Rejected SendGrid event webhook call: Email:SendGridEventWebhookPublicKey is not configured.");
            return Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(timestamp))
        {
            _logger.LogWarning("Rejected SendGrid event webhook call: missing signature or timestamp header.");
            return Unauthorized();
        }

        bool verified;
        try
        {
            var validator = new SendGrid.Helpers.EventWebhook.RequestValidator();
            var ecdsaKey = validator.ConvertPublicKeyToECDSA(publicKey);
            verified = validator.VerifySignature(ecdsaKey, rawBody, signature, timestamp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rejected SendGrid event webhook call: signature verification threw.");
            verified = false;
        }

        if (!verified)
        {
            _logger.LogWarning("Rejected SendGrid event webhook call: signature did not verify.");
            return Unauthorized();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            return BadRequest();
        }

        using (document)
        {
            var events = document.RootElement;
            if (events.ValueKind != JsonValueKind.Array)
            {
                return BadRequest();
            }

            var consentEventFailed = false;

            foreach (var item in events.EnumerateArray())
            {
                // Read the event name OUTSIDE the guard: the catch has to know whether what just
                // failed was a consent instruction, and it cannot ask afterwards (H8).
                string eventNameForClassification;
                try { eventNameForClassification = ReadString(item, "event"); }
                catch { eventNameForClassification = string.Empty; }

                // JOBS-6 (fixed 2026-08-20): one malformed event used to throw and fail the WHOLE
                // request -- SendGrid then retried the entire batch (duplicating everything before
                // the bad event) or eventually gave up (losing everything after it). Each event
                // now sinks or swims alone.
                //
                // H8 (fixed 2026-08-27): "sinks alone" was applied to EVERY event, including the
                // three that carry a legal instruction. Every path that suppresses a client runs
                // inside this try, so a database hiccup while recording an unsubscribe or a spam
                // complaint was caught, logged, and answered 200 -- and 200 tells SendGrid the
                // event is recorded and must never be sent again. The opt-out was gone with no
                // way to recover it. Now a failed CONSENT event withholds the acknowledgement so
                // SendGrid redelivers; ordinary delivery events still sink alone exactly as
                // before. The cost is duplicated statistics on the redelivered batch, which is
                // recoverable -- a lost opt-out is not.
                try
                {
                var eventName = ReadString(item, "event");
                var providerMessageId = ReadString(item, "sg_message_id");
                var reason = ReadString(item, "reason");
                if (string.IsNullOrWhiteSpace(reason))
                {
                    reason = ReadString(item, "response");
                }
                var occurredAt = ReadUnixTimestamp(item, "timestamp") ?? DateTime.UtcNow;

                var recipientId = ReadCustomInt(item, "newsletter_recipient_id");
                if (recipientId > 0)
                {
                    await _newsletters.RecordRecipientEventAsync(recipientId, eventName, providerMessageId, reason, occurredAt);
                    continue;
                }

                var dripStepSendId = ReadCustomInt(item, "drip_step_send_id");
                if (dripStepSendId > 0)
                {
                    await _newsletters.RecordDripStepEventAsync(dripStepSendId, eventName, providerMessageId, reason, occurredAt);
                    continue;
                }

                // Every other sender. Until 2026-08-08 this fell off the end of the loop: E-Cards,
                // E-Letters and Polls had been tagging their recipient ids since they were built,
                // and every event for them was silently discarded here. Adding a sender means
                // tagging it in its dispatcher and adding one line to this table -- nothing else.
                foreach (var (customArg, entityKind) in TrackedRecipientArgs)
                {
                    var trackedId = ReadCustomInt(item, customArg);
                    if (trackedId <= 0) continue;

                    await _deliveryTracker.RecordAsync(entityKind, trackedId, eventName, providerMessageId, reason, occurredAt);
                    break;
                }
                }
                catch (Exception ex)
                {
                    if (IsConsentEvent(eventNameForClassification))
                    {
                        consentEventFailed = true;
                        _logger.LogError(ex,
                            "SendGrid webhook: a CONSENT event ({Event}) could not be processed. Withholding the " +
                            "acknowledgement so SendGrid redelivers -- this batch will arrive again and its " +
                            "delivery statistics may duplicate. That is the deliberate trade: a lost opt-out " +
                            "cannot be recovered.", eventNameForClassification);
                    }
                    else
                    {
                        _logger.LogError(ex, "SendGrid webhook: one event in the batch could not be processed and was skipped.");
                    }
                }
            }

            if (!ShouldAcknowledge(consentEventFailed))
            {
                // 503, not 500: this is "try me again", not "this request was malformed".
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }

        return Ok();
    }

    // The three SendGrid events that carry an instruction in law -- stop mailing this person --
    // rather than a delivery statistic. A spam complaint and an unsubscribe are the same
    // instruction; group_unsubscribe is the one-click header carried by every card, letter and
    // newsletter. Kept beside the webhook that branches on it so the pair cannot drift.
    internal static bool IsConsentEvent(string? eventName) =>
        eventName is not null &&
        (eventName.Equals("unsubscribe", StringComparison.OrdinalIgnoreCase) ||
         eventName.Equals("group_unsubscribe", StringComparison.OrdinalIgnoreCase) ||
         eventName.Equals("spamreport", StringComparison.OrdinalIgnoreCase));

    // The webhook's whole contract with SendGrid: 200 means "recorded -- never send it again".
    // Only an unprocessed CONSENT event may withhold it.
    internal static bool ShouldAcknowledge(bool consentEventFailed) => !consentEventFailed;

    private string GetRequestBaseUrl() => $"{Request.Scheme}://{Request.Host}";

    private async Task LoadNewsletterContextAsync()
    {
        var subscribers = await _clients.GetNewsletterSubscribersAsync(AgentId);
        ViewBag.SubscriberCount = subscribers.Count();
        await LoadAgentTimeZoneAsync();
    }

    private async Task LoadTemplatePickerAsync()
    {
        ViewBag.NewsletterTemplates = await _db.NewsLetterTemplates
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Name)
            .ToListAsync();
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
        model.Edition = string.IsNullOrWhiteSpace(model.Edition) ? null : model.Edition.Trim();
        model.BannerUrl = string.IsNullOrWhiteSpace(model.BannerUrl) ? null : model.BannerUrl.Trim();
        if (string.IsNullOrWhiteSpace(model.HtmlBody) && !string.IsNullOrWhiteSpace(model.TextBody))
        {
            model.HtmlBody = ConvertPlainTextToHtml(model.TextBody);
        }
        model.HtmlBody = HtmlContentSanitizer.Sanitize(model.HtmlBody);

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
