using IPRO.Entities;

namespace IPRO.Business.Interfaces;

public interface IWebsiteService
{
    Task<AgentWebsite?> GetByAgentIdAsync(int agentId);
    Task<AgentWebsite?> GetByDomainAsync(string domain);
    Task<AgentWebsite> CreateAsync(AgentWebsite website);
    Task UpdateAsync(AgentWebsite website);
    Task PublishAsync(int agentId);
    Task UnpublishAsync(int agentId);
    Task<IEnumerable<WebsiteTemplate>> GetTemplatesAsync();
    Task<WebsiteTemplate?> GetTemplateByIdAsync(int id);
    Task<WebsiteTemplate> EnsureDefaultTemplateAsync(string? businessType = null);
    Task<WebsiteTemplate> EnsureDefaultTemplateForPackageAsync(int? packageId, string? businessType = null);
}
