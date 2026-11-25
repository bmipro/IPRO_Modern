using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class SocialPostsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IAiSuggestionService _aiSuggestions;
    private readonly IPackageEntitlementService _entitlements;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public SocialPostsController(IPRODbContext db, IAiSuggestionService aiSuggestions, IPackageEntitlementService entitlements)
    {
        _db = db;
        _aiSuggestions = aiSuggestions;
        _entitlements = entitlements;
    }

    public async Task<IActionResult> Index(string? status)
    {
        var query = _db.SocialPostDrafts.Where(p => p.AgentUserId == AgentId);

        if (string.Equals(status, "draft", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(p => p.Status == SocialPostStatus.Draft);
        }
        else if (string.Equals(status, "posted", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(p => p.Status == SocialPostStatus.Posted);
        }

        ViewBag.Status = status ?? "all";
        ViewBag.AgentTimeZone = await AgentTimeZoneHelper.ResolveForAgentAsync(_db, AgentId);   // 639: the dates on the page
        return View(await query.OrderByDescending(p => p.UpdatedAt).ToListAsync());
    }

    public async Task<IActionResult> Create(string? topic = null, string? seedBody = null)
    {
        ViewBag.AiAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.AiDailyAssistant);
        return View(new SocialPostDraft { Topic = topic?.Trim() ?? "", Body = seedBody?.Trim() ?? "" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SocialPostDraft model)
    {
        model.Topic = model.Topic?.Trim() ?? "";
        model.Body = model.Body?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(model.Body))
        {
            TempData["Error"] = "Post text is required.";
            return View(model);
        }

        _db.SocialPostDrafts.Add(new SocialPostDraft
        {
            AgentUserId = AgentId,
            Topic = model.Topic,
            Body = model.Body,
            ScheduledAt = model.ScheduledAt
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = "Post saved.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var post = await _db.SocialPostDrafts.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == AgentId);
        if (post == null) return NotFound();
        ViewBag.AiAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.AiDailyAssistant);
        ViewBag.AgentTimeZone = await AgentTimeZoneHelper.ResolveForAgentAsync(_db, AgentId);   // 639: the dates on the page
        return View(post);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(SocialPostDraft model)
    {
        var post = await _db.SocialPostDrafts.FirstOrDefaultAsync(p => p.Id == model.Id && p.AgentUserId == AgentId);
        if (post == null) return NotFound();

        model.Topic = model.Topic?.Trim() ?? "";
        model.Body = model.Body?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(model.Body))
        {
            TempData["Error"] = "Post text is required.";
            return View(model);
        }

        post.Topic = model.Topic;
        post.Body = model.Body;
        post.ScheduledAt = model.ScheduledAt;
        post.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Post updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DraftWithAi(string topic)
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.AiDailyAssistant);
        if (!access.IsIncluded)
        {
            return Json(new { success = false, error = access.UpgradeMessage });
        }

        // 678 (audit F-4): a topic of a sentence or two, and the adviser's drafts for the day (AiDraftAllowance).
        if (IPRO.Business.Services.AiDraftAllowance.TopicProblem(topic) is { } topicProblem)
        {
            return Json(new { success = false, error = topicProblem });
        }
        var allowance = await IPRO.Business.Services.AiDraftAllowance.TryTakeAsync(_db, AgentId, DateTime.UtcNow);
        if (!allowance.Allowed)
        {
            return Json(new { success = false, error = IPRO.Business.Services.AiDraftAllowance.UsedUpMessage(allowance.Limit) });
        }

        var result = await _aiSuggestions.DraftSocialPostAsync(topic.Trim(), await _db.AgentUsers.AsNoTracking().Where(a => a.Id == AgentId).Select(a => a.BusinessType).FirstOrDefaultAsync());
        // 678 (audit F-4): recorded for the adviser and for the platform; 679: a call nothing answered gives its draft back.
        await IPRO.Business.Services.AiDraftAllowance.FinishAsync(_db, AgentId, result.InputTokens, result.OutputTokens, DateTime.UtcNow);

        if (string.IsNullOrWhiteSpace(result.Reason))
        {
            return Json(new { success = false, error = "AI drafting isn't available right now — try again in a moment, or write the post yourself." });
        }

        return Json(new { success = true, body = result.Reason });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPosted(int id)
    {
        var post = await _db.SocialPostDrafts.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == AgentId);
        if (post == null) return NotFound();

        post.Status = SocialPostStatus.Posted;
        post.PostedAt = DateTime.UtcNow;
        post.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Marked as posted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var post = await _db.SocialPostDrafts.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == AgentId);
        if (post == null) return NotFound();

        _db.SocialPostDrafts.Remove(post);
        await _db.SaveChangesAsync();

        TempData["Warning"] = "Post deleted.";
        return RedirectToAction(nameof(Index));
    }
}
