using IPRO.DataAccess;
using IPRO.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// Each Did You Know submission queues one row per selected article (PublicWebsiteController),
// staggered several minutes apart so a visitor never receives 4-6 emails in the same instant --
// a burst like that is itself a spam signal to mail providers. This job just drains whatever
// is due each minute, one article per email, full content inline (no external links needed).
public class DidYouKnowEmailDispatchJob
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly ILogger<DidYouKnowEmailDispatchJob> _logger;

    public DidYouKnowEmailDispatchJob(IPRODbContext db, IEmailService email, ILogger<DidYouKnowEmailDispatchJob> logger)
    {
        _db = db;
        _email = email;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var due = await _db.DidYouKnowEmailQueueItems
            .Where(q => q.SentAtUtc == null && q.ScheduledForUtc <= DateTime.UtcNow)
            .OrderBy(q => q.ScheduledForUtc)
            .Take(100)
            .ToListAsync();

        foreach (var item in due)
        {
            try
            {
                var article = await _db.Articles.FirstOrDefaultAsync(a => a.Id == item.ArticleId && a.IsPublished);
                var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == item.ClientId);
                if (article == null || client == null || string.IsNullOrWhiteSpace(client.Email))
                {
                    // Nothing sendable (article unpublished/deleted, client gone) -- drop, don't retry forever.
                    item.SentAtUtc = DateTime.UtcNow;
                    continue;
                }

                var agent = await _db.AgentUsers.FirstOrDefaultAsync(u => u.Id == article.AgentUserId);
                var companyName = agent == null || string.IsNullOrWhiteSpace(agent.CompanyName)
                    ? $"{agent?.FirstName} {agent?.LastName}".Trim()
                    : agent.CompanyName;

                var html = $"""
                    <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
                      <div style="padding:22px;background:#1457d9;color:white"><h1 style="margin:0;font-size:22px">Did You Know?</h1></div>
                      <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                        <h2 style="margin:0 0 10px;font-size:20px;color:#0f172a;">{System.Net.WebUtility.HtmlEncode(article.Title)}</h2>
                        <div style="font-family:Arial,sans-serif;font-size:15px;line-height:1.6;color:#334155;">{article.Content}</div>
                      </div>
                    </div>
                    """;

                var clientName = $"{client.FirstName} {client.LastName}".Trim();
                await _email.SendDetailedAsync(
                    client.Email,
                    string.IsNullOrWhiteSpace(clientName) ? client.Email : clientName,
                    article.Title,
                    html,
                    replyToEmail: agent?.Email,
                    replyToName: companyName);

                item.SentAtUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Did You Know queued email {ItemId} (article {ArticleId}) failed", item.Id, item.ArticleId);
            }
        }

        await _db.SaveChangesAsync();
    }
}
