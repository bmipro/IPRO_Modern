using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 462(c) (2026-09-09). The Marketing Calendar placed events on the calendar date of their UTC
// timestamp, while Email Activity and every other portal page show the agent's local time. A
// newsletter sent at 22:30 Pacific on the 9th sat on the 10th; a post at 21:00 on September 30
// fell off September altogether. Events now sit on the agent's local date, and the month is
// bounded by local midnights.
public class MarketingCalendarTimeZoneTests
{
    private const string Pacific = "(GMT-08:00) Pacific Time (US & Canada)";

    [Fact]
    public async Task Events_sit_on_the_agents_local_date_not_the_utc_date()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, Pacific);

        // 05:30 UTC on the 10th is 22:30 on the 9th in Pacific time (September: UTC-7).
        await SeedNewsletterSendAsync(db, agentId, "Late-evening newsletter", new DateTime(2026, 9, 10, 5, 30, 0, DateTimeKind.Utc));

        var september = await ListAsync(db, agentId, 2026, 9);
        var newsletter = Assert.Single(september);
        Assert.Equal("Newsletter", newsletter.Type);
        Assert.Equal(new DateTime(2026, 9, 9), newsletter.Date);
    }

    [Fact]
    public async Task The_month_is_bounded_by_local_midnight_not_utc_midnight()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db, Pacific);

        // 04:00 UTC on October 1 is 21:00 on September 30 Pacific: a September post.
        await SeedSocialPostAsync(db, agentId, "Last post of the month", new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc));
        // 05:00 UTC on October 1 is 22:00 on September 30 Pacific: a September campaign send.
        await SeedCampaignSendAsync(db, agentId, "Welcome series", new DateTime(2026, 10, 1, 5, 0, 0, DateTimeKind.Utc));
        // 03:00 UTC on September 1 is 20:00 on August 31 Pacific: not a September event at all.
        await SeedNewsletterSendAsync(db, agentId, "August newsletter", new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc));

        var september = await ListAsync(db, agentId, 2026, 9);
        Assert.Equal(new[] { "Campaign", "Social" }, september.Select(e => e.Type).OrderBy(t => t).ToArray());
        Assert.All(september, e => Assert.Equal(new DateTime(2026, 9, 30), e.Date));

        var august = await ListAsync(db, agentId, 2026, 8);
        var newsletter = Assert.Single(august);
        Assert.Equal("Newsletter", newsletter.Type);
        Assert.Equal(new DateTime(2026, 8, 31), newsletter.Date);

        Assert.Empty(await ListAsync(db, agentId, 2026, 10));
    }

    // ---- harness ------------------------------------------------------------------------------

    private static async Task<List<MarketingCalendarEvent>> ListAsync(IPRODbContext db, int agentId, int year, int month)
    {
        var controller = new MarketingCalendarController(db);
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        var result = Assert.IsType<ViewResult>(await controller.Index(year, month));
        return Assert.IsAssignableFrom<IEnumerable<MarketingCalendarEvent>>(result.Model).ToList();
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db, string timeZone)
    {
        var rule = new BillingRule { PackageName = ($"T462-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t462-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Cal", LastName = "Agent", CompanyName = "Cal Co",
            DomainName = ($"t462-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id,
            TimeZone = timeZone
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task SeedNewsletterSendAsync(IPRODbContext db, int agentId, string subject, DateTime sentAtUtc)
    {
        var newsletter = new NewsLetter { AgentUserId = agentId, Subject = subject, Status = NewsLetterStatus.Sent, SentAt = sentAtUtc };
        db.NewsLetters.Add(newsletter);
        await db.SaveChangesAsync();
        db.NewsLetterSends.Add(new NewsLetterSend
        {
            NewsLetterId = newsletter.Id, AgentUserId = agentId,
            Status = NewsLetterSendStatus.Sent, ScheduledAt = sentAtUtc, SentAt = sentAtUtc
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedSocialPostAsync(IPRODbContext db, int agentId, string topic, DateTime postedAtUtc)
    {
        db.SocialPostDrafts.Add(new SocialPostDraft
        {
            AgentUserId = agentId, Topic = topic, Body = "Posted.", Status = SocialPostStatus.Posted, PostedAt = postedAtUtc
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedCampaignSendAsync(IPRODbContext db, int agentId, string name, DateTime sentAtUtc)
    {
        var campaign = new DripCampaign { AgentUserId = agentId, Name = name };
        db.DripCampaigns.Add(campaign);
        await db.SaveChangesAsync();
        var step = new DripCampaignStep { DripCampaignId = campaign.Id, Subject = "Step 1", HtmlBody = "<p>Hello</p>" };
        db.Add(step);
        var client = new Client { AgentUserId = agentId, FirstName = "Cal", LastName = "Endar", Email = $"cal-{Guid.NewGuid():N}@example.test" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var enrollment = new DripCampaignEnrollment
        {
            AgentUserId = agentId, DripCampaignId = campaign.Id, ClientId = client.Id, UnsubscribeToken = Guid.NewGuid().ToString("N")
        };
        db.Add(enrollment);
        await db.SaveChangesAsync();
        db.DripCampaignStepSends.Add(new DripCampaignStepSend
        {
            DripCampaignEnrollmentId = enrollment.Id, DripCampaignStepId = step.Id,
            Email = client.Email, RecipientName = "Cal", SentAt = sentAtUtc
        });
        await db.SaveChangesAsync();
    }
}
