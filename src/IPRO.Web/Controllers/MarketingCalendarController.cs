using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class MarketingCalendarController : Controller
{
    private readonly IPRODbContext _db;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public MarketingCalendarController(IPRODbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index(int? year, int? month)
    {
        var today = DateTime.Today;
        var selectedMonth = new DateTime(
            year.GetValueOrDefault(today.Year),
            month.GetValueOrDefault(today.Month),
            1);
        var monthStart = selectedMonth.Date;
        var monthEnd = monthStart.AddMonths(1);

        ViewBag.MonthStart = monthStart;
        ViewBag.PreviousMonth = monthStart.AddMonths(-1);
        ViewBag.NextMonth = monthStart.AddMonths(1);

        var events = new List<MarketingCalendarEvent>();

        var newsletterSends = await _db.NewsLetterSends
            .Include(s => s.NewsLetter)
            .Where(s => s.AgentUserId == AgentId)
            .ToListAsync();
        foreach (var send in newsletterSends)
        {
            var date = (send.SentAt ?? send.ScheduledAt).Date;
            if (date < monthStart || date >= monthEnd) continue;
            events.Add(new MarketingCalendarEvent
            {
                Date = date,
                Type = "Newsletter",
                Title = send.NewsLetter?.Subject is { Length: > 0 } subject ? subject : "Newsletter",
                Url = $"/Newsletter/Edit/{send.NewsLetterId}"
            });
        }

        var socialPosts = await _db.SocialPostDrafts
            .Where(p => p.AgentUserId == AgentId && (p.ScheduledAt != null || p.PostedAt != null))
            .ToListAsync();
        foreach (var post in socialPosts)
        {
            var date = (post.PostedAt ?? post.ScheduledAt)!.Value.Date;
            if (date < monthStart || date >= monthEnd) continue;
            events.Add(new MarketingCalendarEvent
            {
                Date = date,
                Type = "Social",
                Title = string.IsNullOrWhiteSpace(post.Topic) ? "Social post" : post.Topic,
                Url = $"/SocialPosts/Edit/{post.Id}"
            });
        }

        var campaignSends = await _db.DripCampaignStepSends
            .Include(s => s.DripCampaignStep)
            .ThenInclude(step => step.DripCampaign)
            .Where(s => s.DripCampaignStep.DripCampaign.AgentUserId == AgentId &&
                        s.SentAt != null && s.SentAt >= monthStart && s.SentAt < monthEnd)
            .ToListAsync();
        foreach (var group in campaignSends.GroupBy(s => new
                 {
                     Date = s.SentAt!.Value.Date,
                     s.DripCampaignStep.DripCampaignId,
                     s.DripCampaignStep.DripCampaign.Name
                 }))
        {
            events.Add(new MarketingCalendarEvent
            {
                Date = group.Key.Date,
                Type = "Campaign",
                Title = $"{group.Key.Name}: sent to {group.Count()}",
                Url = $"/Campaigns/Details/{group.Key.DripCampaignId}"
            });
        }

        return View(events.OrderBy(e => e.Date).ToList());
    }
}
