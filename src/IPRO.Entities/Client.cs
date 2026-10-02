namespace IPRO.Entities;

public class Client
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }
    public int? LastBirthdayReminderYear { get; set; }
    // Fair-rotation marker for ClientLifeEventReminderJob's birthday check -- see
    // ClientLifeEvent.LastCheckedAt for the reasoning. Distinct from LastBirthdayReminderYear,
    // which tracks whether this year's reminder was already sent.
    public DateTime? BirthdayReminderLastCheckedAt { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Email2 { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string HomePhone2 { get; set; } = string.Empty;
    public string BusinessPhone { get; set; } = string.Empty;
    public string BusinessPhone2 { get; set; } = string.Empty;
    public string CellPhone { get; set; } = string.Empty;
    public string CellPhone2 { get; set; } = string.Empty;
    public string Fax { get; set; } = string.Empty;
    public string Fax2 { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string UnitNumber { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Province { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = "Canada";
    public bool IsNewsletterSubscribed { get; set; } = false;

    // ---- email consent -------------------------------------------------------------------
    //
    // IsNewsletterSubscribed above is the NEWSLETTER-specific flag and stays exactly as it was:
    // the agent and the website signup form both set it, and it means "wants the newsletter".
    // The three fields below are the GLOBAL opt-out, and they deliberately sit alongside it
    // rather than replacing it.
    //
    // One user action, one result. As of 2026-08-17 this is true of EVERY path, not just the
    // preferences page: the newsletter footer link, the drip footer link, the RFC 8058 one-click
    // endpoint, and SendGrid's spamreport / unsubscribe / group_unsubscribe events on ANY sender all
    // funnel into EmailConsentService.SuppressAllAsync, which sets EmailOptOutAt and clears
    // IsNewsletterSubscribed together.
    //
    // This comment previously claimed that and was wrong -- the webhook paths and the newsletter
    // footer link set IsNewsletterSubscribed alone, so a client who complained about a newsletter
    // kept receiving that agent's cards, letters, polls and drip campaigns.
    //
    // Nothing may write these fields directly. A second opt-out mechanism that could disagree with
    // the first is exactly what made the public-slug collision survive four separate fixes -- see
    // DOCS/INVARIANTS.md rule 1.

    // Set when the client unsubscribes. Null means they have never opted out. A timestamp rather
    // than a bool because "when did they opt out" is the question asked in a complaint.
    public DateTime? EmailOptOutAt { get; set; }

    // 538: WHY, kept beside when -- the source every writer already hands SuppressAllAsync
    // ("ses:bounced:invoice", "sendgrid:spamreport:newsletter", "one-click", "link"). Until this
    // existed the reason went to an Information log line that production does not keep, and the
    // adviser was told "unsubscribed" about an address that had bounced. EmailOptOut.ReasonOf reads it.
    // Empty on a suppression recorded before 538, and whenever the client is not suppressed.
    [System.ComponentModel.DataAnnotations.MaxLength(EmailOptOut.SourceMaxLength)]
    public string EmailOptOutSource { get; set; } = string.Empty;

    // Set only if, AFTER unsubscribing, the client explicitly asks to keep receiving personal
    // greetings on the preferences page. Never set by default: the RFC 8058 one-click POST is fired
    // by Gmail with no human present, so it must suppress everything, and this is the deliberate
    // choice a person makes afterwards. Only designs flagged SendAfterUnsubscribe are covered.
    public DateTime? GreetingsOptInAt { get; set; }

    // Stable per-client token for the preferences link carried in every email. Unguessable and
    // long-lived: these links sit in inboxes for years, so rotating it would silently break the
    // unsubscribe in mail that has already been delivered.
    public string EmailPreferencesToken { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? PortalPasswordHash { get; set; }
    public string? PortalInviteToken { get; set; }
    // Null means either never issued or issued before this field existed - treated as
    // non-expiring so pre-existing outstanding invites keep working rather than being silently
    // invalidated by this fix. Every newly-issued invite always gets a real expiry.
    public DateTime? PortalInviteTokenExpiresAt { get; set; }
    // 454: whether the invite EMAIL went out (the token above only says an invite was issued).
    public DateTime? PortalInviteEmailedAt { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(500)]
    public string? PortalInviteEmailError { get; set; }
    public DateTime? PortalActivatedAt { get; set; }
    public AgentUser AgentUser { get; set; } = null!;
    public ICollection<ClientCategory> Categories { get; set; } = new List<ClientCategory>();
    public ICollection<ClientComment> Comments { get; set; } = new List<ClientComment>();
    public ICollection<ClientFollowUp> FollowUps { get; set; } = new List<ClientFollowUp>();
    public ICollection<ClientLifeEvent> LifeEvents { get; set; } = new List<ClientLifeEvent>();
    public ICollection<PortalMessage> Messages { get; set; } = new List<PortalMessage>();
    public ICollection<PortalDocument> PortalDocuments { get; set; } = new List<PortalDocument>();
    public ICollection<PortalAppointmentRequest> PortalAppointmentRequests { get; set; } = new List<PortalAppointmentRequest>();
}

// 538 (2026-10-02): the three things one word was hiding. The owner's pilot of Amazon SES sent an
// estimate to Amazon's bounce mailbox and an invoice to its complaint mailbox; both clients were
// suppressed, correctly, and both times the adviser was told the client "unsubscribed".
//   Unsubscribed -- the person's own instruction (a link, the one-click header, their portal).
//   Complaint    -- they marked an email as spam. Also their instruction, and it survives anything.
//   Bounced      -- the ADDRESS does not exist. Nobody decided anything; correcting the address ends it.
public enum EmailOptOutReason { Unsubscribed, Bounced, Complaint }

public static class EmailOptOut
{
    public const int SourceMaxLength = 100;

    // The sources are "<provider>:bounced:<kind>" for a hard bounce and "<provider>:spamreport:<kind>"
    // for a complaint; anything else is the person unsubscribing. An empty source -- a suppression
    // recorded before 538 -- reads as Unsubscribed, the cautious answer: only they can undo it.
    public static EmailOptOutReason ReasonOf(string? source)
    {
        var parts = (source ?? string.Empty).Split(':');
        if (parts.Length < 2) return EmailOptOutReason.Unsubscribed;
        return parts[1] switch
        {
            "bounced" => EmailOptOutReason.Bounced,
            "spamreport" => EmailOptOutReason.Complaint,
            _ => EmailOptOutReason.Unsubscribed
        };
    }

    public static EmailOptOutReason ReasonOf(Client client) => ReasonOf(client.EmailOptOutSource);
}
