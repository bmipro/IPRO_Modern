using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class SocialPostsController : Controller
{
    private readonly IPRODbContext _db;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public SocialPostsController(IPRODbContext db)
    {
        _db = db;
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
        return View(await query.OrderByDescending(p => p.UpdatedAt).ToListAsync());
    }

    public IActionResult Create()
    {
        return View(new SocialPostDraft());
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
            Body = model.Body
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = "Post saved.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var post = await _db.SocialPostDrafts.FirstOrDefaultAsync(p => p.Id == id && p.AgentUserId == AgentId);
        if (post == null) return NotFound();
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
        post.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Post updated.";
        return RedirectToAction(nameof(Index));
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
