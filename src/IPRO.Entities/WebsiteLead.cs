namespace IPRO.Entities;

public static class WebsiteLeadTypes
{
    public const string Contact = "Contact";
    public const string Newsletter = "Newsletter";
}

public static class WebsiteLeadStatuses
{
    public const string New = "New";
    public const string Contacted = "Contacted";
    public const string Dismissed = "Dismissed";
}

public class WebsiteLead
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int AgentWebsiteId { get; set; }
    public int? WebsitePageId { get; set; }
    public int? ClientId { get; set; }
    public string SubmissionType { get; set; } = WebsiteLeadTypes.Contact;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string SourceDomain { get; set; } = string.Empty;
    public string SourcePage { get; set; } = string.Empty;
    public string Referrer { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public bool ConsentGiven { get; set; }
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    public string Status { get; set; } = WebsiteLeadStatuses.New;
    public string ProcessingNote { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AgentUser AgentUser { get; set; } = null!;
    public AgentWebsite AgentWebsite { get; set; } = null!;
    public WebsitePage? WebsitePage { get; set; }
    public Client? Client { get; set; }
}
