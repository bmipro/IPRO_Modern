using System.Security.Cryptography;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Business.Services;

// May we send this client this email? One method, one answer, every sender.
//
// This is the email equivalent of ShouldRouteToPublicWebsite: a decision that must have exactly one
// home. The public-slug collision survived four fixes because ownership of a URL was decided in
// several places that disagreed with each other; a suppression rule spread across five dispatchers
// would fail the same way, except the symptom would be an unsubscribed client still getting mail --
// which is a complaint, not a bug report.
//
// Nothing else may re-implement this test. A dispatcher that needs a new exception changes this file.
// A sender with no member here cannot be consent-checked, and the omission is invisible to anyone
// reading this file -- which is exactly how drip campaigns and testimonial requests ended up mailing
// unsubscribed clients (2026-08-14 ultra-audit). Every outbound path must name its channel here.
public enum EmailChannel
{
    Newsletter,
    ECard,
    ELetter,
    Poll,
    DidYouKnow,
    DripCampaign,
    TestimonialRequest
}

// What SuppressAllAsync actually did. WasAlreadySuppressed matters because SendGrid redelivers
// spamreport events and a person can click an unsubscribe link twice -- the second pass must not
// re-retire queue items, re-cancel enrollments, or email the agent a second time.
public readonly record struct SuppressionResult(
    bool WasAlreadySuppressed,
    int QueuedItemsRetired,
    int EnrollmentsCancelled);

// Telling the agent their client unsubscribed is a MAIL operation, and IPRO.Business cannot
// reference IPRO.Email (the dependency runs the other way: IPRO.Email -> IPRO.Business). So the
// consent service declares the need and IPRO.Email satisfies it.
public interface IUnsubscribeNotifier
{
    Task NotifyAgentAsync(Client client);
}

public interface IEmailConsentService
{
    // designSurvivesOptOut: only meaningful for ECard -- ECardDesign.SendAfterUnsubscribe, set per
    // design by SuperAdmin (birthday and anniversary). Ignored for every other channel.
    bool IsSuppressed(Client client, EmailChannel channel, bool designSurvivesOptOut = false);

    // The WRITE half of consent, living in the same file as the READ so the pair cannot drift.
    // Before this existed, only the preferences page could suppress: a spam complaint or an
    // unsubscribe reported by SendGrid set nothing at all, so the client kept receiving e-cards,
    // e-letters, polls and Did You Know mail (JOBS-4, the CASL exposure). Every writer now calls
    // this one method.
    Task<SuppressionResult> SuppressAllAsync(Client client, string source);

    // The deliberate reverse, made by a person looking at the preferences page.
    Task ResubscribeAsync(Client client);

    // Returns the client's preferences token, creating and persisting one if they don't have it yet.
    // Every outgoing email needs a List-Unsubscribe URL, and a client created before this feature
    // existed has an empty token.
    Task<string> GetOrCreateTokenAsync(Client client);

    string BuildPreferencesUrl(string token);
}

public class EmailConsentService : IEmailConsentService
{
    private readonly IPRODbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailConsentService> _logger;

    // IEnumerable, not a single instance: IPRO.Admin has no reason to send agent notifications and
    // registers none. Taking a collection means the service resolves in both apps without either
    // needing a no-op stub, and a missing registration degrades to "suppression still works, nobody
    // is emailed" rather than a startup crash.
    private readonly IEnumerable<IUnsubscribeNotifier> _notifiers;

    public EmailConsentService(
        IPRODbContext db,
        IConfiguration configuration,
        ILogger<EmailConsentService> logger,
        IEnumerable<IUnsubscribeNotifier> notifiers)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
        _notifiers = notifiers;
    }

    public bool IsSuppressed(Client client, EmailChannel channel, bool designSurvivesOptOut = false)
    {
        if (client == null) return true;

        // No email address is its own kind of suppression, and callers were checking this
        // inconsistently before.
        if (string.IsNullOrWhiteSpace(client.Email)) return true;

        var hasOptedOut = client.EmailOptOutAt.HasValue;

        if (hasOptedOut)
        {
            // The single exception, and it needs BOTH halves: the design must be one SuperAdmin
            // marked as a personal greeting, AND the client must have explicitly asked to keep
            // receiving greetings after unsubscribing. Either alone is not consent.
            var greetingIsWanted =
                channel == EmailChannel.ECard &&
                designSurvivesOptOut &&
                client.GreetingsOptInAt.HasValue;

            if (!greetingIsWanted) return true;
        }

        // Newsletters have their own long-standing flag on top of the global opt-out: an agent or
        // the website signup form can turn the newsletter off without the client ever unsubscribing
        // from everything.
        if (channel == EmailChannel.Newsletter && !client.IsNewsletterSubscribed) return true;

        return false;
    }

    // Moved here from EmailPreferencesController so that the preferences page is no longer the ONLY
    // thing that can suppress. SendGrid's spamreport and unsubscribe events now call this too --
    // before, they set nothing, so someone who hit "this is spam" on a newsletter kept receiving
    // e-cards, e-letters, polls and Did You Know mail (JOBS-4, the CASL exposure).
    //
    // Idempotent by design: SendGrid redelivers events and a person can click unsubscribe twice.
    // A second pass must not re-retire queue items, re-cancel enrollments, or email the agent again.
    public async Task<SuppressionResult> SuppressAllAsync(Client client, string source)
    {
        if (client == null) return new SuppressionResult(true, 0, 0);

        if (client.EmailOptOutAt.HasValue)
        {
            // Already suppressed. Do not repeat the sweeps or the notification -- but DO make sure
            // the newsletter flag agrees, because the two mechanisms drifting apart is the exact
            // inconsistency Client.EmailOptOutAt was introduced to end.
            if (client.IsNewsletterSubscribed)
            {
                client.IsNewsletterSubscribed = false;
                client.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
            return new SuppressionResult(true, 0, 0);
        }

        var now = DateTime.UtcNow;
        client.EmailOptOutAt = now;
        client.GreetingsOptInAt = null;
        client.IsNewsletterSubscribed = false;
        client.UpdatedAt = now;

        // Queued Did You Know mail is already scheduled and would otherwise still go out after they
        // unsubscribed. The dispatcher re-checks consent, but retiring the rows here means the Email
        // Activity screen shows the truth instead of a queue that silently never sends.
        var queued = await _db.DidYouKnowEmailQueueItems
            .Where(q => q.ClientId == client.Id && q.SentAtUtc == null)
            .ToListAsync();
        foreach (var item in queued)
        {
            item.SentAtUtc = now;
            item.Status = DidYouKnowQueueStatuses.Failed;
            item.FailureReason = "Recipient unsubscribed before this was sent.";
        }

        // Same reasoning for drip campaigns: an active enrollment is a standing instruction to keep
        // mailing this client for weeks. Leaving it Active would show the agent a campaign that
        // appears to be running and silently sends nothing.
        var enrollments = await _db.DripCampaignEnrollments
            .Where(e => e.ClientId == client.Id && e.Status == DripCampaignEnrollmentStatus.Active)
            .ToListAsync();
        foreach (var enrollment in enrollments)
        {
            enrollment.Status = DripCampaignEnrollmentStatus.Cancelled;
            enrollment.LastError = "Recipient unsubscribed from all email.";
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Client {ClientId} unsubscribed from all email via {Source}; {Queued} queued item(s) retired, " +
            "{Enrollments} drip enrollment(s) cancelled.",
            client.Id, source, queued.Count, enrollments.Count);

        // Best effort, and deliberately last: an agent notification that fails must never make the
        // suppression itself fail. Suppression is the part with legal weight.
        foreach (var notifier in _notifiers)
        {
            try
            {
                await notifier.NotifyAgentAsync(client);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not notify agent {AgentId} that client {ClientId} unsubscribed. The unsubscribe itself succeeded.",
                    client.AgentUserId, client.Id);
            }
        }

        return new SuppressionResult(false, queued.Count, enrollments.Count);
    }

    // Deliberately does NOT revive retired queue items or cancelled enrollments. Those were specific
    // sends the client was opted out of at the time; resubscribing is consent to receive future mail,
    // not a request to be sent the backlog they already missed.
    public async Task ResubscribeAsync(Client client)
    {
        if (client == null) return;

        client.EmailOptOutAt = null;
        client.GreetingsOptInAt = null;
        client.IsNewsletterSubscribed = true;
        client.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Client {ClientId} resubscribed to email from the preferences page.", client.Id);
    }

    public async Task<string> GetOrCreateTokenAsync(Client client)
    {
        if (!string.IsNullOrWhiteSpace(client.EmailPreferencesToken)) return client.EmailPreferencesToken;

        // 32 bytes of CSPRNG, URL-safe. This token is the only thing standing between a stranger and
        // unsubscribing someone else's client, so it is generated the same way as the other
        // security-sensitive tokens in this codebase rather than from Guid or Random.
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        client.EmailPreferencesToken = token;
        client.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return token;
    }

    // Built against the platform host, exactly like NewsLetterDispatcher.BuildUnsubscribeUrl. The
    // link must keep working after an agent changes or drops a custom domain -- a dead unsubscribe
    // link in mail already delivered is worse than a slightly less branded one.
    public string BuildPreferencesUrl(string token) =>
        $"{IPRO.Utility.WebAppUrlHelper.GetWebAppBaseUrl(_configuration)}/email-preferences?token={Uri.EscapeDataString(token)}";
}
