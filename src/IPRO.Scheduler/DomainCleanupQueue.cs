using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// 592: removing an address from Azure once it stops being an adviser's, until Azure confirms.
//
// www.411trades.com left its adviser's account and stayed bound on the web app, its DNS still pointed at iPro,
// and another account could take it over. Azure's own activity log for the 90 days before 2026-10-19 holds not
// one hostname-binding or certificate DELETE: whatever removed the row never got a removal through to Azure,
// and nothing anywhere recorded that it had not. My Website's Remove and SuperAdmin's delete both asked Azure
// once, after the row was gone, and dropped the answer (My Website did not even log one that came back
// "skipped"). So the address is now written down in the same save that removes the row (DomainCleanup), asked
// for at once, and asked for again by the domain job until Azure says it is gone.
//
// Never removed: an address a proven domain row still names (that row's live site, 591; an unproven row is
// served nothing, and its own proof binds the name again), and an address that is one of iPro's own or no host
// name at all (589's refusal; there is nothing of an adviser's to remove; it is listed by
// ops/sql/domain-ownership-history.sql for a look by hand). A binding nobody wrote down -- 411trades.com's, from
// before this -- is not touched here: removing that is the owner's decision (ops/domain-ownership/orphan-bindings.sh
// lists them).
public static class DomainCleanupQueue
{
    // Records the row's addresses for removal. The caller saves them with the row's removal.
    // `delay`: when the first try is due (none: at once).
    public static List<DomainCleanup> Add(IPRODbContext db, AgentDomain domain, string reason, TimeSpan? delay = null)
    {
        var now = DateTime.UtcNow;
        var added = AgentDomainHosts.HostsOf(domain)
            .Select(host => new DomainCleanup
            {
                HostName = host, FormerAgentUserId = domain.AgentUserId, Reason = reason, CreatedAt = now,
                NextAttemptAt = delay.HasValue ? now + delay.Value : null,
            })
            .ToList();
        db.DomainCleanups.AddRange(added);
        return added;
    }

    // Asks Azure for each one now and records the answer. Never throws: a removal that did not get through is
    // tried again by the domain job.
    public static async Task AttemptAsync(IPRODbContext db, IAzureDomainAutomationService azure, IConfiguration? configuration, ILogger logger,
        IEnumerable<DomainCleanup> cleanups, CancellationToken cancellationToken = default)
    {
        foreach (var cleanup in cleanups.Where(c => c.DoneAt == null))
        {
            await AttemptOneAsync(db, azure, configuration, logger, cleanup, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record the outcome of removing {Count} address(es) from Azure; the domain job will ask again",
                cleanups.Count());
            // Left tracked, the unsaved outcomes would fail every later save on this context.
            foreach (var cleanup in cleanups) db.Entry(cleanup).State = EntityState.Detached;
        }
    }

    // The domain job's turn: every removal that is due, oldest first.
    public static async Task RunDueAsync(IPRODbContext db, IAzureDomainAutomationService azure, IConfiguration? configuration, ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var due = await db.DomainCleanups
            .Where(c => c.DoneAt == null && (c.NextAttemptAt == null || c.NextAttemptAt <= now))
            .OrderBy(c => c.Id)
            .Take(25)
            .ToListAsync(cancellationToken);
        if (due.Count == 0) return;
        await AttemptAsync(db, azure, configuration, logger, due, cancellationToken);
    }

    private static async Task AttemptOneAsync(IPRODbContext db, IAzureDomainAutomationService azure, IConfiguration? configuration, ILogger logger,
        DomainCleanup cleanup, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var host = cleanup.HostName;
        try
        {
            // 589: iPro's own names and anything that is no host name are never removed. Settled here rather than
            // left to the automation's own refusal, whose answer reads the same as "automation is switched off".
            if (!PlatformHostNames.TryCanonicalHost(host, out _) ||
                PlatformHostNames.IsPlatformHost(host, configuration) ||
                PlatformHostNames.SharesCertificateNameWithPlatformHost(host, configuration))
            {
                Finish(cleanup, now, DomainCleanupOutcomes.Refused);
                logger.LogWarning("Azure cleanup of {Domain} not attempted: it is one of iPro's own addresses or no host name", PlatformHostNames.Printable(host));
                return;
            }

            // 591: a host another proven domain row still names is that row's binding and certificate.
            if (await AgentDomainHosts.StillNamedAsync(db, host, cancellationToken, provenOnly: true))
            {
                Finish(cleanup, now, DomainCleanupOutcomes.KeptForAnotherRow);
                logger.LogWarning("Azure cleanup of {Domain} skipped after removal: another domain row still names it", PlatformHostNames.Printable(host));
                return;
            }

            var result = await azure.RemoveDomainAsync(host, cancellationToken);
            if (result.Success)
            {
                Finish(cleanup, now, DomainCleanupOutcomes.Removed);
                logger.LogWarning("Azure cleanup of {Domain} done ({Reason}, adviser {AgentUserId})", host, cleanup.Reason, cleanup.FormerAgentUserId);
                return;
            }

            Retry(cleanup, now, result.Message);
            logger.LogWarning("Azure cleanup of {Domain} did not go through (try {Attempt}), asked again at {Next:u}: {Message}",
                host, cleanup.Attempts, cleanup.NextAttemptAt, result.Message);
        }
        catch (Exception ex)
        {
            Retry(cleanup, now, ex.Message);
            logger.LogWarning(ex, "Azure cleanup of {Domain} failed (try {Attempt}), asked again at {Next:u}", host, cleanup.Attempts, cleanup.NextAttemptAt);
        }
    }

    private static void Finish(DomainCleanup cleanup, DateTime now, string outcome)
    {
        cleanup.Attempts++;
        cleanup.LastAttemptAt = now;
        cleanup.DoneAt = now;
        cleanup.NextAttemptAt = null;
        cleanup.Outcome = outcome;
        cleanup.LastError = string.Empty;
    }

    // Every five minutes for the first hour, hourly for the rest of the first day, then daily: an Azure outage
    // or a configuration slip is caught up on its own, and nothing gives up.
    internal static TimeSpan RetryDelay(int attempts) => attempts switch
    {
        <= 12 => TimeSpan.FromMinutes(5),
        <= 35 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromDays(1),
    };

    private static void Retry(DomainCleanup cleanup, DateTime now, string? error)
    {
        cleanup.Attempts++;
        cleanup.LastAttemptAt = now;
        cleanup.NextAttemptAt = now + RetryDelay(cleanup.Attempts);
        var message = error ?? string.Empty;
        cleanup.LastError = message.Length > 1000 ? message[..1000] : message;
    }
}
