using IPRO.Entities;

namespace IPRO.Admin.Models;

public class AgentDomainViewModel
{
    public AgentDomain Domain { get; set; } = new();
    public string AgentName { get; set; } = string.Empty;
    public string AgentEmail { get; set; } = string.Empty;
    public string TemporaryDomain { get; set; } = string.Empty;
}
