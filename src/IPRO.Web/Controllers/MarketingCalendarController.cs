using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Web.Infrastructure;
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

    // 462(c) (2026-09-09): events sit on the agent's local date, the way Email Activity shows them.
    // Timestamps are UTC; a newsletter sent at 22:30 Pacific used to land on the next day, and one
    // sent at 21:00 on the 30th fell off the month. The month is bounded by local midnights too.
    public async Task<IActionResult> Index(int? year, int? month)
    {
        var timeZone = await AgentTimeZoneHelper.ResolveForAgentAsync(_db, AgentId);
        var today = AgentTimeZoneHelper.FromUtc(DateTime.UtcNow, timeZone).Date;
        var selectedMonth = new DateTime(
            year.GetValueOrDefault(today.Year),
            month.GetValueOrDefault(today.Month),
            1);
        var monthStart = selectedMonth.Date;
        var monthEnd = monthStart.AddMonths(1);
        var monthStartUtc = AgentTimeZoneHelper.ToUtc(monthStart, timeZone);
        var monthEndUtc = AgentTimeZoneHelper.ToUtc(monthEnd, timeZone);
        DateTime LocalDate(DateTime utc) => AgentTimeZoneHelper.FromUtc(utc, timeZone).Date;

        ViewBag.MonthStart = monthStart;
        ViewBag.PreviousMonth = monthStart.AddMonths(-1);
        ViewBag.NextMonth = monthStart.AddMonths(1);
        ViewBag.Today = today;

        var events = new List<MarketingCalendarEvent>();

        var newsletterSends = await _db.NewsLetterSends
            .Include(s => s.NewsLetter)
            .Where(s => s.AgentUserId == AgentId)
            .ToListAsync();
        foreach (var send in newsletterSends)
        {
            var date = LocalDate(send.SentAt ?? send.ScheduledAt);
            if (date < monthStart || date >= monthEnd) continue;
            events.Add(new MarketingCalendarEvent
            {
                Date = date,
                Type = "Newsletter",
                Title = send.NewsLetter?.Subject is { Length: > 0 } subject ? subject : "Newsletter",
                Url = IPRO.Utility.PortalPaths.To($"/Newsletter/Edit/{send.NewsLetterId}")
            });
        }

        var socialPosts = await _db.SocialPostDrafts
            .Where(p => p.AgentUserId == AgentId && (p.ScheduledAt != null || p.PostedAt != null))
            .ToListAsync();
        foreach (var post in socialPosts)
        {
            var date = LocalDate((post.PostedAt ?? post.ScheduledAt)!.Value);
            if (date < monthStart || date >= monthEnd) continue;
            events.Add(new MarketingCalendarEvent
            {
                Date = date,
                Type = "Social",
                Title = string.IsNullOrWhiteSpace(post.Topic) ? "Social post" : post.Topic,
                Url = IPRO.Utility.PortalPaths.To($"/SocialPosts/Edit/{post.Id}")
            });
        }

        var campaignSends = await _db.DripCampaignStepSends
            .Include(s => s.DripCampaignStep)
            .ThenInclude(step => step.DripCampaign)
            .Where(s => s.DripCampaignStep.DripCampaign.AgentUserId == AgentId &&
                        s.SentAt != null && s.SentAt >= monthStartUtc && s.SentAt < monthEndUtc)
            .ToListAsync();
        foreach (var group in campaignSends.GroupBy(s => new
                 {
                     Date = LocalDate(s.SentAt!.Value),
                     s.DripCampaignStep.DripCampaignId,
                     s.DripCampaignStep.DripCampaign.Name
                 }))
        {
            events.Add(new MarketingCalendarEvent
            {
                Date = group.Key.Date,
                Type = "Campaign",
                Title = $"{group.Key.Name}: sent to {group.Count()}",
                Url = IPRO.Utility.PortalPaths.To($"/Campaigns/Details/{group.Key.DripCampaignId}")
            });
        }

        return View(events.OrderBy(e => e.Date).ToList());
    }
}
