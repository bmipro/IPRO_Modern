namespace IPRO.Entities;

public class AgentUser
{
    public int Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string CompanyAddress { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Province { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = "Canada";
    public string TimeZone { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string BusinessFax { get; set; } = string.Empty;
    public string CellPhone { get; set; } = string.Empty;
    public string BusinessType { get; set; } = string.Empty;
    public string? DefaultPaymentLink { get; set; } = string.Empty;
    public string? PortalAccentColor { get; set; }
    public string? PhotoUrl { get; set; }
    public string DomainName { get; set; } = string.Empty;
    public int PackageId { get; set; }
    public string PromotionCode { get; set; } = string.Empty;
    public DateTime? TermsAcceptedAt { get; set; }
    public string RegistrationIpAddress { get; set; } = string.Empty;
    public bool MustChangePassword { get; set; }
    public DateTime? PasswordChangedAt { get; set; }
    public string? PasswordResetToken { get; set; }
    public DateTime? PasswordResetTokenExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public ICollection<AgentWebsite> Websites { get; set; } = new List<AgentWebsite>();
    public ICollection<Client> Clients { get; set; } = new List<Client>();
    public ICollection<Billing> Billings { get; set; } = new List<Billing>();
    public ICollection<NewsLetter> NewsLetters { get; set; } = new List<NewsLetter>();

    public List<string> GetFormattedAddressLines()
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(CompanyAddress)) lines.Add(CompanyAddress);
        var cityLine = string.Join(" ", new[] { City, Province, PostalCode }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(cityLine)) lines.Add(cityLine);
        if (!string.IsNullOrWhiteSpace(Country)) lines.Add(Country);
        return lines;
    }
}
