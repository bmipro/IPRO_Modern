using System.Security.Claims;
using IPRO.Business.Interfaces;
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
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public CampaignsController(IPRODbContext db, IPackageEntitlementService entitlements)
    {
        _db = db;
        _entitlements = entitlements;
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
            HtmlBody = htmlBody.Trim(),
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
            HtmlBody = body.Trim(),
            DelayDays = Math.Max(0, delayDays),
            SortOrder = nextOrder + 10
        });

        await _db.SaveChangesAsync();
        TempData["Success"] = $"Newsletter \"{newsletter.Subject}\" added as a campaign step.";
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
        step.HtmlBody = body.Trim();
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
        step.HtmlBody = htmlBody.Trim();
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
            TempData[enrolled == 0 ? "Warning" : "Success"] =
                enrolled == 0
                    ? "No new clients were enrolled. They may already be active in this campaign."
                    : $"{enrolled} client{(enrolled == 1 ? "" : "s")} enrolled in {campaign.Name}.";
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
        TempData[enrolled == 0 ? "Warning" : "Success"] = enrolled == 0 ? "That client is already active in this campaign or has no email address." : "Client enrolled in campaign.";
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
                SubscriberCount = c.Clients.Count(client => client.IsNewsletterSubscribed)
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
