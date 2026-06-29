namespace IPRO.Entities;

public class AgentUser
{
    public int Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string DomainName { get; set; } = string.Empty;
    public int PackageId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public ICollection<AgentWebsite> Websites { get; set; } = new List<AgentWebsite>();
    public ICollection<Client> Clients { get; set; } = new List<Client>();
    public ICollection<Billing> Billings { get; set; } = new List<Billing>();
    public ICollection<NewsLetter> NewsLetters { get; set; } = new List<NewsLetter>();
}
