using System.Security.Cryptography;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.Extensions.Configuration;

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
public enum EmailChannel
{
    Newsletter,
    ECard,
    ELetter,
    Poll,
    DidYouKnow
}

public interface IEmailConsentService
{
    // designSurvivesOptOut: only meaningful for ECard -- ECardDesign.SendAfterUnsubscribe, set per
    // design by SuperAdmin (birthday and anniversary). Ignored for every other channel.
    bool IsSuppressed(Client client, EmailChannel channel, bool designSurvivesOptOut = false);

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

    public EmailConsentService(IPRODbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
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
