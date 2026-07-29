namespace IPRO.Entities;

public static class ECardOccasions
{
    public const string Birthday = "Birthday";
    public const string ThankYou = "ThankYou";
    public const string Holiday = "Holiday";
    public const string Congratulations = "Congratulations";

    public static readonly string[] All = { Birthday, ThankYou, Holiday, Congratulations };

    public static string DisplayName(string occasion) => occasion switch
    {
        Birthday => "Happy Birthday",
        ThankYou => "Thank You",
        Holiday => "Season's Greetings",
        Congratulations => "Congratulations",
        _ => occasion
    };

    // The card's gradient always starts from the agent's own accent color (brand consistency with
    // Newsletter) and ends at one of these per-occasion accents, so cards stay visually distinct
    // by occasion without needing a separate unbranded color system.
    public static string AccentFor(string occasion) => occasion switch
    {
        Birthday => "#ff7a59",
        ThankYou => "#0f9d78",
        Holiday => "#1e3a8a",
        Congratulations => "#7c3aed",
        _ => "#5b8def"
    };

    public static string EmojiFor(string occasion) => occasion switch
    {
        Birthday => "🎂",
        ThankYou => "🙏",
        Holiday => "❄️",
        Congratulations => "🎉",
        _ => "✉️"
    };
}

public static class ECardStatuses
{
    public const string Draft = "Draft";
    public const string Scheduled = "Scheduled";
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}

public class ECard
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string Occasion { get; set; } = ECardOccasions.Birthday;
    public string Subject { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = ECardStatuses.Draft;
    public DateTime ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int TotalRecipients { get; set; }
    public int TotalSent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AgentUser AgentUser { get; set; } = null!;
    public ICollection<ECardRecipient> Recipients { get; set; } = new List<ECardRecipient>();
}
