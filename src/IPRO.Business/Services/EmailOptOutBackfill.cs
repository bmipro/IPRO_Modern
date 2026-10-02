using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Business.Services;

// 540 (2026-10-02): the reason for the suppressions made before 538 kept one.
//
// 538 writes WHY beside WHEN for every suppression from now on. The ones already there have a time
// and no reason, so they read "Unsubscribed ... only they can" turn it back on. For a client whose
// address bounced last month that is 538's defect left in place: the adviser cannot unblock them, and
// an adviser who already corrected the address and resent the invoice has nothing left to correct.
// The owner met it within the hour, on his own two test clients; his word on this backfill: "go".
//
// The email history can say why. Every recipient table records a bounce (BouncedAt) and the last
// event on the email (a "spamreport" is a complaint), and a suppression is stamped in the same
// moment its cause is processed. So a reason is written only where one of those sits within minutes
// of the suppression -- and nothing is written otherwise. "Unsubscribed" is the cautious reading,
// the one only the client can undo, and a wrong guess towards "bounced" would hand the adviser a
// way to mail someone who had asked to be left alone.
//
// It lives beside the consent service on purpose (INVARIANTS rule 1, and the comment on
// Client.EmailOptOutAt): it writes the REASON and never whether the client is suppressed. The one
// case where a suppression ends -- the address that bounced has already been replaced, and the
// address on file now has never bounced -- goes through LiftBounceSuppression, like the adviser's
// own correction does.
public static class EmailOptOutBackfill
{
    public sealed record Result(int Looked, int Bounced, int Complaints, int Lifted);

    // How far a suppression may sit from the event that caused it. The event carries the mail
    // provider's time; the suppression is stamped when iPro processed the report -- seconds later,
    // minutes when a report was retried. A little slack before, for two clocks that disagree.
    public static readonly TimeSpan Before = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan After = TimeSpan.FromHours(1);

    // The first segment of the source it writes: "backfill:bounced:invoice", "backfill:spamreport:newsletter".
    public const string Provider = "backfill";
    private const int BatchSize = 500;

    private sealed record Evidence(int ClientId, string Kind, bool Complaint, DateTime At, string Address);

    public static async Task<Result> RunAsync(IPRODbContext db, IEmailConsentService consent, ILogger logger, CancellationToken ct)
    {
        int looked = 0, bounced = 0, complaints = 0, lifted = 0, lastId = 0;
        while (!ct.IsCancellationRequested)
        {
            // Walked by id, so a client the history says nothing about is passed over and not met
            // again in this run; it is looked at afresh on the next start, which costs one query.
            var clients = await db.Clients
                .Where(c => c.Id > lastId && c.EmailOptOutAt != null && c.EmailOptOutSource == "")
                .OrderBy(c => c.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (clients.Count == 0) break;
            lastId = clients[^1].Id;
            looked += clients.Count;

            var evidence = await EvidenceAsync(db, clients.Select(c => c.Id).ToList(), ct);
            foreach (var client in clients)
            {
                var suppressedAt = client.EmailOptOutAt!.Value;
                var mine = evidence.Where(e => e.ClientId == client.Id).ToList();
                var near = mine.Where(e => suppressedAt >= e.At - Before && suppressedAt <= e.At + After)
                    .OrderBy(e => Math.Abs((e.At - suppressedAt).Ticks))
                    .ToList();

                // The person's own instruction outranks an address problem, here as in SuppressAllAsync.
                var complaint = near.FirstOrDefault(e => e.Complaint);
                if (complaint != null)
                {
                    client.EmailOptOutSource = $"{Provider}:spamreport:{complaint.Kind}";
                    complaints++;
                    continue;
                }

                var bounce = near.FirstOrDefault(e => !e.Complaint);
                if (bounce == null) continue;
                client.EmailOptOutSource = $"{Provider}:bounced:{bounce.Kind}";
                bounced++;

                // Already corrected? The address that bounced is no longer the one on file, and no email
                // to the one on file has ever bounced: the adviser fixed it (and until 538 that fixed
                // nothing). End it the way their correction would have. When the address is unknown
                // (Did You Know keeps none) or unchanged, the client stays on hold and reads "Email
                // bounced", which the adviser can now correct.
                var onFile = CanonicalEmail.Canonical(client.Email);
                var thatBounced = CanonicalEmail.Canonical(bounce.Address);
                var onFileBouncedToo = mine.Any(e => !e.Complaint && CanonicalEmail.Canonical(e.Address) == onFile);
                if (onFile.Length > 0 && thatBounced.Length > 0 && onFile != thatBounced && !onFileBouncedToo
                    && consent.LiftBounceSuppression(client))
                {
                    lifted++;
                }
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        // A warning so that it is kept: production's container log holds no Information lines.
        if (bounced + complaints > 0)
        {
            logger.LogWarning(
                "Suppression reasons from before 538: {Looked} clients had none recorded; the email history shows {Bounced} bounces " +
                "({Lifted} with the address already corrected, switched back on) and {Complaints} spam complaints. The rest stay Unsubscribed.",
                looked, bounced, lifted, complaints);
        }
        return new Result(looked, bounced, complaints, lifted);
    }

    // What each kind of email recorded about these clients: every bounce, and every email whose last
    // event is a spam complaint. The complaint's time is the failure's where the table keeps one, else
    // the moment the row was last written -- which is the moment the complaint was processed, as long
    // as it is still the last event (a later open or click replaces it, and then it proves nothing).
    private static async Task<List<Evidence>> EvidenceAsync(IPRODbContext db, List<int> ids, CancellationToken ct)
    {
        const string spam = "spamreport";
        var evidence = new List<Evidence>();

        void Add(string kind, int clientId, string lastEvent, DateTime? bouncedAt, DateTime? complaintAt, string? address)
        {
            if (bouncedAt.HasValue) evidence.Add(new Evidence(clientId, kind, false, bouncedAt.Value, address ?? string.Empty));
            if (lastEvent == spam && complaintAt.HasValue) evidence.Add(new Evidence(clientId, kind, true, complaintAt.Value, address ?? string.Empty));
        }

        foreach (var r in await db.ClientInvoiceEmails.AsNoTracking()
                     .Where(e => ids.Contains(e.ClientId) && (e.BouncedAt != null || e.LastEvent == spam))
                     .Select(e => new { e.ClientId, e.LastEvent, e.BouncedAt, e.FailedAt, e.UpdatedAt, e.ToEmail }).ToListAsync(ct))
            Add("invoice", r.ClientId, r.LastEvent, r.BouncedAt, r.FailedAt ?? r.UpdatedAt, r.ToEmail);

        foreach (var r in await db.NewsLetterRecipients.AsNoTracking()
                     .Where(e => e.ClientId != null && ids.Contains(e.ClientId.Value) && (e.BouncedAt != null || e.LastEvent == spam))
                     .Select(e => new { ClientId = e.ClientId!.Value, e.LastEvent, e.BouncedAt, e.FailedAt, e.UpdatedAt, e.Email }).ToListAsync(ct))
            Add("newsletter", r.ClientId, r.LastEvent, r.BouncedAt, r.FailedAt ?? r.UpdatedAt, r.Email);

        foreach (var r in await db.ECardRecipients.AsNoTracking()
                     .Where(e => ids.Contains(e.ClientId) && (e.BouncedAt != null || e.LastEvent == spam))
                     .Select(e => new { e.ClientId, e.LastEvent, e.BouncedAt, e.UpdatedAt, e.Email }).ToListAsync(ct))
            Add("ecard", r.ClientId, r.LastEvent, r.BouncedAt, r.UpdatedAt, r.Email);

        foreach (var r in await db.ELetterRecipients.AsNoTracking()
                     .Where(e => ids.Contains(e.ClientId) && (e.BouncedAt != null || e.LastEvent == spam))
                     .Select(e => new { e.ClientId, e.LastEvent, e.BouncedAt, e.UpdatedAt, e.Email }).ToListAsync(ct))
            Add("eletter", r.ClientId, r.LastEvent, r.BouncedAt, r.UpdatedAt, r.Email);

        foreach (var r in await db.PollRecipients.AsNoTracking()
                     .Where(e => e.ClientId != null && ids.Contains(e.ClientId.Value) && (e.BouncedAt != null || e.LastEvent == spam))
                     .Select(e => new { ClientId = e.ClientId!.Value, e.LastEvent, e.BouncedAt, e.FailedAt, e.UpdatedAt, e.Email }).ToListAsync(ct))
            Add("poll", r.ClientId, r.LastEvent, r.BouncedAt, r.FailedAt ?? r.UpdatedAt, r.Email);

        // Did You Know keeps neither the address nor the time of a failure: its bounces only.
        foreach (var r in await db.DidYouKnowEmailQueueItems.AsNoTracking()
                     .Where(e => ids.Contains(e.ClientId) && e.BouncedAt != null)
                     .Select(e => new { e.ClientId, e.BouncedAt }).ToListAsync(ct))
            Add("didyouknow", r.ClientId, string.Empty, r.BouncedAt, null, null);

        // A drip step reaches the person through the enrollment, and keeps no last event: its bounces only.
        foreach (var r in await (from s in db.DripCampaignStepSends.AsNoTracking()
                                 join e in db.DripCampaignEnrollments.AsNoTracking() on s.DripCampaignEnrollmentId equals e.Id
                                 where ids.Contains(e.ClientId) && s.BouncedAt != null
                                 select new { e.ClientId, s.BouncedAt, s.Email }).ToListAsync(ct))
            Add("dripcampaign", r.ClientId, string.Empty, r.BouncedAt, null, r.Email);

        return evidence;
    }
}
