using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class CampaignsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IEmailConsentService _consent;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public CampaignsController(IPRODbContext db, IPackageEntitlementService entitlements, IEmailConsentService consent)
    {
        _db = db;
        _entitlements = entitlements;
        _consent = consent;
    }

    public async Task<IActionResult> Index()
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        return View(new CampaignIndexViewModel
        {
            Groups = await LoadGroupsAsync(),
            Campaigns = await _db.DripCampaigns
                .Include(c => c.Steps)
                .Include(c => c.Enrollments)
                .Where(c => c.AgentUserId == AgentId)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string? description)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Campaign name is required.";
            return RedirectToAction(nameof(Index));
        }

        var campaign = new DripCampaign
        {
            AgentUserId = AgentId,
            Name = name.Trim(),
            Description = description?.Trim() ?? string.Empty,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.DripCampaigns.Add(campaign);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Campaign created. Add the email steps next.";
        return RedirectToAction(nameof(Details), new { id = campaign.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var campaign = await _db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (campaign == null) return NotFound();

        var steps = await _db.DripCampaignSteps
            .Where(s => s.DripCampaignId == id)
            .OrderBy(s => s.SortOrder)
            .ToListAsync();

        var stepIds = steps.Select(s => s.Id).ToList();
        var stepPerformance = (await _db.DripCampaignStepSends
                .Where(s => stepIds.Contains(s.DripCampaignStepId))
                .ToListAsync())
            .GroupBy(s => s.DripCampaignStepId)
            .ToDictionary(g => g.Key, g => new CampaignStepPerformance
            {
                Sent = g.Count(s => s.SentAt.HasValue || s.DeliveredAt.HasValue || s.OpenedAt.HasValue || s.ClickedAt.HasValue),
                Delivered = g.Count(s => s.DeliveredAt.HasValue),
                Opened = g.Count(s => s.OpenedAt.HasValue),
                Clicked = g.Count(s => s.ClickedAt.HasValue)
            });

        return View(new CampaignDetailsViewModel
        {
            Campaign = campaign,
            Steps = steps,
            StepPerformance = stepPerformance,
            FailedEnrollmentCount = await _db.DripCampaignEnrollments
                .CountAsync(e => e.DripCampaignId == id && e.AgentUserId == AgentId &&
                                 e.Status == DripCampaignEnrollmentStatus.Failed),
            Enrollments = await _db.DripCampaignEnrollments
                .Include(e => e.Client)
                .Include(e => e.ClientCategory)
                .Where(e => e.DripCampaignId == id && e.AgentUserId == AgentId)
                .OrderByDescending(e => e.StartedAt)
                .Take(50)
                .ToListAsync(),
            Groups = await LoadGroupsAsync(),
            Clients = await _db.Clients
                .Where(c => c.AgentUserId == AgentId && !string.IsNullOrWhiteSpace(c.Email))
                .OrderBy(c => c.LastName)
                .ThenBy(c => c.FirstName)
                .ToListAsync(),
            Newsletters = await _db.NewsLetters
                .Where(n => n.AgentUserId == AgentId)
                .OrderByDescending(n => n.CreatedAt)
                .ToListAsync(),
            Forms = await _db.WebsiteForms
                .Where(f => f.AgentUserId == AgentId && f.IsActive)
                .OrderByDescending(f => f.UpdatedAt)
                .ToListAsync(),
            Articles = await _db.Articles
                .Where(a => a.AgentUserId == AgentId)
                .OrderByDescending(a => a.UpdatedAt)
                .ToListAsync()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddStep(int id, string subject, string htmlBody, int delayDays)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var campaign = await _db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (campaign == null) return NotFound();

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(htmlBody))
        {
            TempData["Error"] = "Step subject and body are required.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var nextOrder = await _db.DripCampaignSteps
            .Where(s => s.DripCampaignId == id)
            .Select(s => (int?)s.SortOrder)
            .MaxAsync() ?? 0;

        _db.DripCampaignSteps.Add(new DripCampaignStep
        {
            DripCampaignId = id,
            Subject = subject.Trim(),
            HtmlBody = HtmlContentSanitizer.Sanitize(htmlBody.Trim()),
            DelayDays = Math.Max(0, delayDays),
            SortOrder = nextOrder + 10
        });
        await _db.SaveChangesAsync();
        TempData["Success"] = "Campaign step added.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddStepFromNewsletter(int id, int newsletterId, int delayDays)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var campaign = await _db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (campaign == null) return NotFound();

        var newsletter = await _db.NewsLetters.FirstOrDefaultAsync(n => n.Id == newsletterId && n.AgentUserId == AgentId);
        if (newsletter == null) return NotFound();

        var body = !string.IsNullOrWhiteSpace(newsletter.HtmlBody)
            ? newsletter.HtmlBody
            : ConvertPlainTextToHtml(newsletter.TextBody);

        if (string.IsNullOrWhiteSpace(newsletter.Subject) || string.IsNullOrWhiteSpace(body))
        {
            TempData["Error"] = "That newsletter does not have enough content to use as a campaign step.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var nextOrder = await _db.DripCampaignSteps
            .Where(s => s.DripCampaignId == id)
            .Select(s => (int?)s.SortOrder)
            .MaxAsync() ?? 0;

        _db.DripCampaignSteps.Add(new DripCampaignStep
        {
            DripCampaignId = id,
            Subject = newsletter.Subject.Trim(),
            HtmlBody = HtmlContentSanitizer.Sanitize(body.Trim()),
            DelayDays = Math.Max(0, delayDays),
            SortOrder = nextOrder + 10
        });

        await _db.SaveChangesAsync();
        TempData["Success"] = $"Newsletter \"{newsletter.Subject}\" added as a campaign step.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddStepFromArticle(int id, int articleId, int delayDays)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var campaign = await _db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (campaign == null) return NotFound();

        var article = await _db.Articles.FirstOrDefaultAsync(a => a.Id == articleId && a.AgentUserId == AgentId);
        if (article == null) return NotFound();

        if (string.IsNullOrWhiteSpace(article.Title) || string.IsNullOrWhiteSpace(article.Content))
        {
            TempData["Error"] = "That article does not have enough content to use as a campaign step.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var nextOrder = await _db.DripCampaignSteps
            .Where(s => s.DripCampaignId == id)
            .Select(s => (int?)s.SortOrder)
            .MaxAsync() ?? 0;

        _db.DripCampaignSteps.Add(new DripCampaignStep
        {
            DripCampaignId = id,
            Subject = article.Title.Trim(),
            HtmlBody = HtmlContentSanitizer.Sanitize(article.Content.Trim()),
            DelayDays = Math.Max(0, delayDays),
            SortOrder = nextOrder + 10
        });

        await _db.SaveChangesAsync();
        TempData["Success"] = $"Article \"{article.Title}\" added as a campaign step.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReplaceStepWithArticle(int id, int stepId, int articleId, int delayDays)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var campaign = await _db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (campaign == null) return NotFound();

        var step = await _db.DripCampaignSteps.FirstOrDefaultAsync(s => s.Id == stepId && s.DripCampaignId == id);
        var article = await _db.Articles.FirstOrDefaultAsync(a => a.Id == articleId && a.AgentUserId == AgentId);
        if (step == null || article == null) return NotFound();

        if (string.IsNullOrWhiteSpace(article.Title) || string.IsNullOrWhiteSpace(article.Content))
        {
            TempData["Error"] = "That article does not have enough content to use as a campaign step.";
            return RedirectToAction(nameof(Details), new { id });
        }

        step.Subject = article.Title.Trim();
        step.HtmlBody = HtmlContentSanitizer.Sanitize(article.Content.Trim());
        step.DelayDays = Math.Max(0, delayDays);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Campaign step replaced with article content.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddStepFromForm(int id, int formId, int delayDays)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var campaign = await _db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (campaign == null) return NotFound();

        var form = await _db.WebsiteForms.FirstOrDefaultAsync(f => f.Id == formId && f.AgentUserId == AgentId && f.IsActive);
        if (form == null) return NotFound();

        var website = await _db.AgentWebsites.Include(w => w.AgentUser).FirstOrDefaultAsync(w => w.AgentUserId == AgentId);
        var domain = !string.IsNullOrWhiteSpace(website?.CustomDomain) ? website!.CustomDomain : website?.AgentUser.DomainName;
        if (website == null || string.IsNullOrWhiteSpace(domain))
        {
            TempData["Error"] = "Set up your website domain before sending a form in a campaign.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var formUrl = $"https://{domain}/PublicWebsite/Form/{form.Id}";
        var intro = string.IsNullOrWhiteSpace(form.Description) ? "Please take a moment to fill out this form." : form.Description;

        static string DescribeField(PublicFormField f)
        {
            var label = (f.Label ?? string.Empty).Trim();
            var suffix = f.IsRequired ? " (required)" : "";
            var supportsOptions = f.FieldType == WebsiteFormFieldTypes.CheckboxGroup || f.FieldType == WebsiteFormFieldTypes.Dropdown;
            return supportsOptions && f.Options.Count > 0
                ? $"{label}{suffix} — choose from: {string.Join(", ", f.Options)}"
                : $"{label}{suffix}";
        }

        var formData = await IPRO.Web.Infrastructure.PublicFormBuilder.BuildForFormAsync(_db, form.Id, AgentId);
        var fieldDescriptions = (formData?.Fields ?? new List<PublicFormField>())
            .Where(f => f.FieldType != WebsiteFormFieldTypes.Section && !string.IsNullOrWhiteSpace(f.Label))
            .Select(DescribeField)
            .ToList();
        var fieldListHtml = fieldDescriptions.Count == 0
            ? ""
            : "<ul style=\"margin:0 0 20px;padding-left:20px;color:#334155;font:400 14px/1.6 Arial,sans-serif;\">"
              + string.Join("", fieldDescriptions.Select(d => $"<li>{System.Net.WebUtility.HtmlEncode(d)}</li>"))
              + "</ul>";

        var htmlBody = $"""
            <div style="border:1px solid #e2e8f0;border-radius:10px;padding:24px;max-width:520px;font-family:Arial,sans-serif;">
              <h2 style="margin:0 0 8px;font-size:20px;color:#0f172a;">{System.Net.WebUtility.HtmlEncode(form.Title)}</h2>
              <p style="margin:0 0 16px;color:#475569;font-size:15px;line-height:1.5;">{System.Net.WebUtility.HtmlEncode(intro)}</p>
              {fieldListHtml}
              <a href="{formUrl}" style="display:inline-block;padding:12px 28px;background:#1457d9;color:#fff;text-decoration:none;border-radius:6px;font-weight:700;font-size:15px;">{System.Net.WebUtility.HtmlEncode(form.SubmitButtonText)}</a>
              <p style="margin:16px 0 0;color:#94a3b8;font-size:12px;">Takes less than a minute.</p>
            </div>
            """;

        var nextOrder = await _db.DripCampaignSteps
            .Where(s => s.DripCampaignId == id)
            .Select(s => (int?)s.SortOrder)
            .MaxAsync() ?? 0;

        _db.DripCampaignSteps.Add(new DripCampaignStep
        {
            DripCampaignId = id,
            Subject = form.Title.Trim(),
            HtmlBody = HtmlContentSanitizer.Sanitize(htmlBody),
            DelayDays = Math.Max(0, delayDays),
            SortOrder = nextOrder + 10
        });

        await _db.SaveChangesAsync();
        TempData["Success"] = $"Form \"{form.Title}\" added as a campaign step.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteStep(int id, int stepId)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var campaign = await _db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (campaign == null) return NotFound();

        var step = await _db.DripCampaignSteps.FirstOrDefaultAsync(s => s.Id == stepId && s.DripCampaignId == id);
        if (step != null)
        {
            _db.DripCampaignSteps.Remove(step);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Campaign step removed.";
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReplaceStepWithNewsletter(int id, int stepId, int newsletterId, int delayDays)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var campaign = await _db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (campaign == null) return NotFound();

        var step = await _db.DripCampaignSteps.FirstOrDefaultAsync(s => s.Id == stepId && s.DripCampaignId == id);
        var newsletter = await _db.NewsLetters.FirstOrDefaultAsync(n => n.Id == newsletterId && n.AgentUserId == AgentId);
        if (step == null || newsletter == null) return NotFound();

        var body = !string.IsNullOrWhiteSpace(newsletter.HtmlBody)
            ? newsletter.HtmlBody
            : ConvertPlainTextToHtml(newsletter.TextBody);

        if (string.IsNullOrWhiteSpace(newsletter.Subject) || string.IsNullOrWhiteSpace(body))
        {
            TempData["Error"] = "That newsletter does not have enough content to use as a campaign step.";
            return RedirectToAction(nameof(Details), new { id });
        }

        step.Subject = newsletter.Subject.Trim();
        step.HtmlBody = HtmlContentSanitizer.Sanitize(body.Trim());
        step.DelayDays = Math.Max(0, delayDays);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Campaign step replaced with newsletter content.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStep(int id, int stepId, string subject, string htmlBody, int delayDays)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var campaign = await _db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (campaign == null) return NotFound();

        var step = await _db.DripCampaignSteps.FirstOrDefaultAsync(s => s.Id == stepId && s.DripCampaignId == id);
        if (step == null) return NotFound();

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(htmlBody))
        {
            TempData["Error"] = "Step subject and body are required.";
            return RedirectToAction(nameof(Details), new { id });
        }

        step.Subject = subject.Trim();
        step.HtmlBody = HtmlContentSanitizer.Sanitize(htmlBody.Trim());
        step.DelayDays = Math.Max(0, delayDays);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Campaign step updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EnrollGroup(int id, int clientCategoryId)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var campaign = await _db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (campaign == null) return NotFound();

        var clients = await _db.Clients
            .Include(c => c.Categories)
            .Where(c => c.AgentUserId == AgentId &&
                        !string.IsNullOrWhiteSpace(c.Email) &&
                        c.Categories.Any(cat => cat.Id == clientCategoryId))
            .ToListAsync();

        var enrolled = await EnrollClientsAsync(campaign, clients, clientCategoryId);
        if (!TempData.ContainsKey("Error"))
        {
            if (enrolled > 0)
                TempData["Success"] = $"{enrolled} client{(enrolled == 1 ? "" : "s")} enrolled in {campaign.Name}.";
            else if (!TempData.ContainsKey("Warning"))
                TempData["Warning"] = "No new clients were enrolled. They may already be active in this campaign.";
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EnrollClient(int id, int clientId)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var campaign = await _db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (campaign == null) return NotFound();

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId && c.AgentUserId == AgentId && !string.IsNullOrWhiteSpace(c.Email));
        var enrolled = client == null ? 0 : await EnrollClientsAsync(campaign, new[] { client }, null);
        if (enrolled > 0)
            TempData["Success"] = "Client enrolled in campaign.";
        else if (!TempData.ContainsKey("Warning") && !TempData.ContainsKey("Error"))
            TempData["Warning"] = "That client is already active in this campaign or has no email address.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelEnrollment(int id, int enrollmentId)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var enrollment = await _db.DripCampaignEnrollments
            .FirstOrDefaultAsync(e => e.Id == enrollmentId && e.DripCampaignId == id && e.AgentUserId == AgentId);
        if (enrollment != null)
        {
            enrollment.Status = DripCampaignEnrollmentStatus.Cancelled;
            enrollment.CancelledAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Success"] = "Client removed from campaign.";
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResumeFailedEnrollments(int id)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var campaign = await _db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (campaign == null) return NotFound();

        // H7: the resume path that never existed -- Active was only ever assigned at enrollment
        // creation, so once a row went Failed (a rotated API key, or the transient cap running
        // out during a longer outage) the only recovery was re-enrolling the client, which
        // re-sends every step they already received. Resuming instead reuses the row:
        // NextStepIndex is deliberately NOT touched -- a failed send never advanced it (JOBS-7),
        // so it still points at the exact step that never went out, and the campaign picks up
        // where it stopped with no replays.
        //
        // Resuming a client who unsubscribed in the meantime is safe: the job's consent sweep
        // runs before any send and cancels suppressed enrollments, and the dispatcher re-checks
        // suppression per send. A genuinely-bad recipient (SendGrid 400) re-fails on its first
        // attempt without mailing anyone. Cancelled and Completed rows are not touched --
        // Cancelled is a person's decision, not a failure.
        var failed = await _db.DripCampaignEnrollments
            .Where(e => e.DripCampaignId == id && e.AgentUserId == AgentId &&
                        e.Status == DripCampaignEnrollmentStatus.Failed)
            .ToListAsync();
        foreach (var enrollment in failed)
        {
            enrollment.Status = DripCampaignEnrollmentStatus.Active;
            enrollment.SendAttempts = 0;
            enrollment.NextSendAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        TempData["Success"] = failed.Count == 0
            ? "No failed enrollments to resume."
            : $"{failed.Count} enrollment(s) resumed. Sends restart from the step that never went out; already-delivered steps are not repeated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        var gate = await RequireCampaignAccessAsync();
        if (gate != null) return gate;

        var campaign = await _db.DripCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (campaign == null) return NotFound();

        campaign.IsActive = !campaign.IsActive;
        await _db.SaveChangesAsync();
        TempData["Success"] = campaign.IsActive ? "Campaign activated." : "Campaign paused.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<int> EnrollClientsAsync(DripCampaign campaign, IEnumerable<Client> clients, int? categoryId)
    {
        var firstStep = await _db.DripCampaignSteps
            .Where(s => s.DripCampaignId == campaign.Id)
            .OrderBy(s => s.SortOrder)
            .FirstOrDefaultAsync();
        if (firstStep == null)
        {
            TempData["Error"] = "Add at least one campaign step before enrolling clients.";
            return 0;
        }

        // JOBS-1: enrolling an opted-out client used to succeed -- the job would cancel it at the
        // first due send, so no mail went, but the screen showed a running campaign for someone who
        // asked to be left alone, and the agent was never told. Same decision point as everywhere
        // else (INVARIANTS rule 7): IsSuppressed, never a re-implemented test.
        var eligible = clients.Where(c => !_consent.IsSuppressed(c, EmailChannel.DripCampaign)).ToList();
        var skippedAsUnsubscribed = clients.Count() - eligible.Count;
        if (skippedAsUnsubscribed > 0)
        {
            TempData["Warning"] =
                $"{skippedAsUnsubscribed} client{(skippedAsUnsubscribed == 1 ? " was" : "s were")} not enrolled because they have unsubscribed from email.";
        }
        clients = eligible;

        var clientIds = clients.Select(c => c.Id).ToList();
        var existingIds = await _db.DripCampaignEnrollments
            .Where(e => e.DripCampaignId == campaign.Id &&
                        e.Status == DripCampaignEnrollmentStatus.Active &&
                        clientIds.Contains(e.ClientId))
            .Select(e => e.ClientId)
            .ToListAsync();

        var enrollments = clients
            .Where(c => !existingIds.Contains(c.Id))
            .Select(c => new DripCampaignEnrollment
            {
                AgentUserId = AgentId,
                DripCampaignId = campaign.Id,
                ClientId = c.Id,
                ClientCategoryId = categoryId,
                Status = DripCampaignEnrollmentStatus.Active,
                NextStepIndex = 0,
                StartedAt = DateTime.UtcNow,
                NextSendAt = DateTime.UtcNow.AddDays(Math.Max(0, firstStep.DelayDays)),
                UnsubscribeToken = Guid.NewGuid().ToString("N")
            })
            .ToList();

        _db.DripCampaignEnrollments.AddRange(enrollments);
        await _db.SaveChangesAsync();
        return enrollments.Count;
    }

    private async Task<List<CampaignGroupSummary>> LoadGroupsAsync()
    {
        return await _db.ClientCategories
            .Where(c => c.AgentUserId == AgentId)
            .Select(c => new CampaignGroupSummary
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                ClientCount = c.Clients.Count,
                // Same rule as ClientService.GetNewsletterSubscribersAsync: the number an agent is
                // shown has to be the number a send will actually reach.
                SubscriberCount = c.Clients.Count(client => client.IsNewsletterSubscribed && client.EmailOptOutAt == null)
            })
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    private static string ConvertPlainTextToHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var paragraphs = text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.TrimEntries);

        return string.Join(
            Environment.NewLine,
            paragraphs.Select(line =>
                string.IsNullOrWhiteSpace(line)
                    ? "<br>"
                    : $"<p>{System.Net.WebUtility.HtmlEncode(line)}</p>"));
    }

    private async Task<IActionResult?> RequireCampaignAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.MarketingCampaign);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
