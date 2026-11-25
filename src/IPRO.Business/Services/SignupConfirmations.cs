using System.Net;
using System.Security.Cryptography;
using System.Text;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Business.Services;

// 678 (audit 2026-11-24 D-4, the owner 2026-11-25 "Double opt-in YES"). A newsletter sign-up or a Did You Know
// request on an adviser's website waits for one click in the email it sends. Anyone can type anyone's address into
// a form; the consent recorded is the click, made from the address's own inbox. The click is a button on a page
// (POST), never the link itself, so the mail gateways that open every link in an email cannot confirm for anyone.
//
// Rules:
//   - one confirmation email per contact and kind a day; a repeat sign-up in that day sends nothing more;
//   - at most MaxPerAdviserPerHour confirmation emails an hour per adviser, so one site cannot be used to mail
//     strangers in bulk;
//   - a link works for LinkDays days;
//   - a contact who has confirmed once with this adviser (either kind) is not asked again, unless they have
//     opted out since: an opt-out is their later word, and only a new click undoes it.
public static class SignupConfirmations
{
    public const int LinkDays = 7;
    public const int MaxPerAdviserPerHour = 30;

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("+", "-").Replace("/", "_").TrimEnd('=');

    // True when this contact's word is already proven: a confirmed click with this adviser and no opt-out since.
    public static async Task<bool> AlreadyConfirmedAsync(IPRODbContext db, Client client)
    {
        if (client.EmailOptOutAt.HasValue) return false;
        return await db.EmailSignupConfirmations.AsNoTracking()
            .AnyAsync(c => c.ClientId == client.Id && c.AgentUserId == client.AgentUserId && c.ConfirmedAt != null);
    }

    // A new confirmation to email, or null when one went to this contact for this kind within the day (or the
    // adviser's hourly allowance is used up): then nothing more is sent.
    public static async Task<string?> RequestAsync(IPRODbContext db, Client client, string kind, string sourceDomain, DateTime now)
    {
        var dayAgo = now.AddDays(-1);
        if (await db.EmailSignupConfirmations.AnyAsync(c => c.ClientId == client.Id && c.Kind == kind && c.CreatedAt > dayAgo && c.ConfirmedAt == null))
            return null;
        var hourAgo = now.AddHours(-1);
        if (await db.EmailSignupConfirmations.CountAsync(c => c.AgentUserId == client.AgentUserId && c.CreatedAt > hourAgo) >= MaxPerAdviserPerHour)
            return null;

        var token = NewToken();
        db.EmailSignupConfirmations.Add(new EmailSignupConfirmation
        {
            AgentUserId = client.AgentUserId,
            ClientId = client.Id,
            Kind = kind,
            TokenHash = HashToken(token),
            SourceDomain = (sourceDomain ?? string.Empty).Length > 255 ? sourceDomain![..255] : sourceDomain ?? string.Empty,
            CreatedAt = now,
            ExpiresAt = now.AddDays(LinkDays)
        });
        await db.SaveChangesAsync();
        return token;
    }

    public static async Task<EmailSignupConfirmation?> FindAsync(IPRODbContext db, string? token, DateTime now)
    {
        var trimmed = token?.Trim() ?? string.Empty;
        if (trimmed.Length < 20 || trimmed.Length > 100) return null;
        var hash = HashToken(trimmed);
        var row = await db.EmailSignupConfirmations.FirstOrDefaultAsync(c => c.TokenHash == hash);
        return row == null || (row.ConfirmedAt == null && row.ExpiresAt < now) ? null : row;
    }

    public enum Outcome { Subscribed, ArticlesOnTheirWay, AlreadyConfirmed, OptedOutNoArticles, Gone }

    // The click. Newsletter: subscribed (an earlier opt-out is lifted: this is their own, later word). Did You Know:
    // the held articles are let go, a few minutes apart, unless they have opted out of email (then nothing is sent).
    public static async Task<Outcome> ConfirmAsync(IPRODbContext db, IEmailConsentService consent, EmailSignupConfirmation row, DateTime now)
    {
        if (row.ConfirmedAt != null) return Outcome.AlreadyConfirmed;
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == row.ClientId && c.AgentUserId == row.AgentUserId);
        if (client == null) return Outcome.Gone;

        // Claimed by a conditional update, so a double press confirms once.
        var claimed = await db.EmailSignupConfirmations
            .Where(c => c.Id == row.Id && c.ConfirmedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.ConfirmedAt, now));
        if (claimed == 0) return Outcome.AlreadyConfirmed;

        if (row.Kind == EmailSignupKinds.Newsletter)
        {
            if (client.EmailOptOutAt.HasValue)
            {
                await consent.ResubscribeAsync(client);
            }
            else
            {
                client.IsNewsletterSubscribed = true;
                client.UpdatedAt = now;
            }
            db.ClientComments.Add(new ClientComment
            {
                ClientId = client.Id,
                Comment = $"Confirmed the newsletter sign-up from {row.SourceDomain} by the button in the confirmation email.",
                CreatedAt = now
            });
            await db.SaveChangesAsync();
            return Outcome.Subscribed;
        }

        if (client.EmailOptOutAt.HasValue) return Outcome.OptedOutNoArticles;
        await ReleaseHeldArticlesAsync(db, client.Id, now);
        return Outcome.ArticlesOnTheirWay;
    }

    // The Did You Know articles held for a contact until their click, let go a few minutes apart (several emails in
    // the same second look like spam to mail providers).
    public static async Task<int> ReleaseHeldArticlesAsync(IPRODbContext db, int clientId, DateTime now)
    {
        var held = await db.DidYouKnowEmailQueueItems
            .Where(q => q.ClientId == clientId && q.Status == DidYouKnowQueueStatuses.AwaitingConfirmation && q.SentAtUtc == null)
            .OrderBy(q => q.Id)
            .ToListAsync();
        var at = now.AddMinutes(1);
        foreach (var item in held)
        {
            item.Status = DidYouKnowQueueStatuses.Queued;
            item.ScheduledForUtc = at;
            at = at.AddMinutes(Random.Shared.Next(4, 9));
        }
        await db.SaveChangesAsync();
        return held.Count;
    }

    // Held articles nobody confirmed within the link's life are dropped, so a later request can hold them again.
    public static Task<int> DropExpiredHeldArticlesAsync(IPRODbContext db, int clientId, DateTime now)
    {
        var cutoff = now.AddDays(-LinkDays);
        return db.DidYouKnowEmailQueueItems
            .Where(q => q.ClientId == clientId && q.Status == DidYouKnowQueueStatuses.AwaitingConfirmation && q.CreatedAt < cutoff)
            .ExecuteDeleteAsync();
    }

    public static string ConfirmUrl(string platformBaseUrl, string token) =>
        $"{platformBaseUrl.TrimEnd('/')}/email-preferences/confirm?t={Uri.EscapeDataString(token)}";

    public static string Subject(AgentUser agent, string kind) =>
        AdviserSender.Subject(agent, kind == EmailSignupKinds.DidYouKnow
            ? "please confirm to receive the articles you asked for"
            : "please confirm your newsletter subscription");

    public static string Html(AgentUser agent, string kind, string sourceDomain, string confirmUrl)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var business = AdviserSender.BusinessName(agent);
        var what = kind == EmailSignupKinds.DidYouKnow
            ? "asked for articles from"
            : "signed up for the newsletter of";
        var button = kind == EmailSignupKinds.DidYouKnow ? "Yes, send me the articles" : "Yes, subscribe me";
        var where = string.IsNullOrWhiteSpace(sourceDomain) ? string.Empty : $" on {E(sourceDomain)}";
        return $@"<div style=""font-family:Arial,Helvetica,sans-serif;max-width:560px;margin:auto;color:#17223a;font-size:15px;line-height:1.55"">
<p>Hello,</p>
<p>Someone, we hope you, {what} <strong>{E(business)}</strong>{where} with this email address.</p>
<p>To confirm, press the button:</p>
<p style=""margin:22px 0""><a href=""{E(confirmUrl)}"" style=""display:inline-block;padding:12px 22px;background:#1457d9;color:#ffffff;text-decoration:none;border-radius:6px;font-weight:bold"">{button}</a></p>
<p>If it was not you, ignore this email: nothing more is sent, and the link stops working in {LinkDays} days.</p>
<p style=""color:#64748b;font-size:13px"">{E(business)} uses iPro to send its email.</p>
</div>";
    }
}
