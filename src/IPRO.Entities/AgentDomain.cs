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

    /// <summary>
    /// 553: the short address pointed straight at the platform (an A record) instead of through a
    /// registrar's forwarding. RootDnsStatus says where the name points (Bound = at the platform);
    /// these say what Azure holds for it. NotConfigured until the name first points here; once the
    /// binding exists it stays recorded, so removing the domain knows there is one to delete.
    /// </summary>
    public string RootAzureBindingStatus { get; set; } = AgentDomainStatus.NotConfigured;
    public string RootSslStatus { get; set; } = AgentDomainStatus.NotConfigured;

    /// <summary>When the short address was first bound: the start of its certificate's grace period.</summary>
    public DateTime? RootBoundAt { get; set; }

    /// <summary>
    /// When IPRO was alerted that this domain is bound but has no certificate. The domain check
    /// runs every 5 minutes and the condition is terminal until a human acts, so without this the
    /// alert would resend forever. Cleared when SSL goes green so a later lapse alerts again.
    /// </summary>
    public DateTime? CertificateAlertSentAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser AgentUser { get; set; } = null!;
    public AgentWebsite AgentWebsite { get; set; } = null!;
}
