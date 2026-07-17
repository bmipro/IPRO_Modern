namespace IPRO.Entities;

public static class AgentDomainStatus
{
    public const string PendingDns = "PendingDns";
    public const string DnsReady = "DnsReady";
    public const string BindingPending = "BindingPending";
    public const string Bound = "Bound";
    public const string Failed = "Failed";
    public const string NotConfigured = "NotConfigured";
}

public class AgentDomain
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public int AgentWebsiteId { get; set; }
    public string DomainName { get; set; } = string.Empty;
    public string RootDomain { get; set; } = string.Empty;
    public string WwwDomain { get; set; } = string.Empty;
    public string DnsTarget { get; set; } = string.Empty;
    public string DnsStatus { get; set; } = AgentDomainStatus.PendingDns;
    public string AzureBindingStatus { get; set; } = AgentDomainStatus.BindingPending;
    public string SslStatus { get; set; } = AgentDomainStatus.BindingPending;
    public bool IsPrimary { get; set; } = true;
    public DateTime? LastCheckedAt { get; set; }
    public string LastError { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public DateTime? LastFailedAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public bool AutoRetryExhausted { get; set; }
    public string RootDnsStatus { get; set; } = AgentDomainStatus.PendingDns;
    public bool RootRedirectsToWww { get; set; }
    public DateTime? RootLastCheckedAt { get; set; }
    public string RootLastError { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser AgentUser { get; set; } = null!;
    public AgentWebsite AgentWebsite { get; set; } = null!;
}
