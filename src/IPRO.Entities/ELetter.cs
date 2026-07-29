namespace IPRO.Entities;

public sealed record ELetterTemplate(string Key, string Name, string Description, string Subject, string Body);

public static class ELetterTemplates
{
    // Pre-written starting points, not locked templates -- the agent edits the wording before
    // sending. Merge tokens are baked into the default bodies so an agent discovers the feature
    // by seeing it already working, rather than having to read docs first.
    public static readonly ELetterTemplate[] All =
    {
        new(
            "welcome",
            "Welcome / new client",
            "Sent after someone signs on -- what to expect and how to reach you.",
            "Welcome aboard, [First Name]",
            """
            Dear [First Name],

            Thank you for choosing to work with [Advisor Company]. I'm glad to have you as a client, and I wanted to take a moment to welcome you properly.

            Over the next little while I'll be reaching out to make sure everything is set up the way you need it. In the meantime, if anything at all comes up -- a question, a change in your circumstances, or something you'd like reviewed -- please don't hesitate to contact me directly.

            You can reach me any time at [Advisor Phone] or by replying to this email.

            Warm regards,
            [Advisor Name]
            """),

        new(
            "annual-review",
            "Annual review request",
            "Invite an existing client to book their yearly check-in.",
            "Time for your annual review, [First Name]",
            """
            Dear [First Name],

            It's been about a year since we last reviewed your coverage together, and I'd like to set aside some time to go through it with you.

            A lot can change in a year -- a new job, a move, a growing family, a shift in your plans. A short review makes sure what you have in place still matches where you actually are today.

            The conversation usually takes under half an hour. Just reply to this email with a couple of times that suit you, or call me at [Advisor Phone] and we'll find a slot.

            Best regards,
            [Advisor Name]
            [Advisor Company]
            """),

        new(
            "policy-renewal",
            "Policy renewal reminder",
            "Heads-up that a policy or coverage is coming up for renewal.",
            "Your coverage is coming up for renewal",
            """
            Dear [First Name],

            This is a friendly reminder that your coverage is approaching its renewal date.

            There's nothing you need to do right now -- I'll be in touch shortly with the details. I did want to flag it early though, in case anything has changed on your end that we should factor in before it renews.

            If you'd like to talk it through beforehand, reply here or call me at [Advisor Phone].

            Kind regards,
            [Advisor Name]
            [Advisor Company]
            """),

        new(
            "referral-thanks",
            "Thanks for the referral",
            "Acknowledge a client who sent business your way.",
            "Thank you, [First Name]",
            """
            Dear [First Name],

            I wanted to say a genuine thank you for referring someone to me. It means a great deal.

            Referrals are the highest compliment I can receive in this business -- it tells me you trust the work we've done together enough to put your own name behind it. I don't take that lightly, and I'll look after them the same way I look after you.

            Thank you again.

            Sincerely,
            [Advisor Name]
            [Advisor Company]
            """)
    };

    public static ELetterTemplate? Find(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(t => t.Key == key);
}

public static class ELetterStatuses
{
    public const string Draft = "Draft";
    public const string Scheduled = "Scheduled";
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}

public class ELetter
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string TemplateKey { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Status { get; set; } = ELetterStatuses.Draft;
    public DateTime ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int TotalRecipients { get; set; }
    public int TotalSent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AgentUser AgentUser { get; set; } = null!;
    public ICollection<ELetterRecipient> Recipients { get; set; } = new List<ELetterRecipient>();
}
