namespace IPRO.Entities;

public class Client
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }
    public int? LastBirthdayReminderYear { get; set; }
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
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? PortalPasswordHash { get; set; }
    public string? PortalInviteToken { get; set; }
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
