using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;

namespace IPRO.Business.Services;

public class WebsiteService : IWebsiteService
{
    private readonly IUnitOfWork _uow;
    public WebsiteService(IUnitOfWork uow) => _uow = uow;

    public Task<AgentWebsite?> GetByAgentIdAsync(int agentId) =>
        _uow.AgentWebsites.FirstOrDefaultAsync(w => w.AgentUserId == agentId);

    public Task<AgentWebsite?> GetByDomainAsync(string domain) =>
        _uow.AgentWebsites.FirstOrDefaultAsync(w => w.CustomDomain == domain);

    public async Task<AgentWebsite> CreateAsync(AgentWebsite website)
    {
        website.CreatedAt = DateTime.UtcNow;
        website.UpdatedAt = DateTime.UtcNow;
        await _uow.AgentWebsites.AddAsync(website);
        await _uow.SaveChangesAsync();
        return website;
    }

    public async Task UpdateAsync(AgentWebsite website)
    {
        website.UpdatedAt = DateTime.UtcNow;
        _uow.AgentWebsites.Update(website);
        await _uow.SaveChangesAsync();
    }

    public async Task PublishAsync(int agentId)
    {
        var site = await GetByAgentIdAsync(agentId);
        if (site != null) { site.IsPublished = true; site.UpdatedAt = DateTime.UtcNow; _uow.AgentWebsites.Update(site); await _uow.SaveChangesAsync(); }
    }

    public async Task UnpublishAsync(int agentId)
    {
        var site = await GetByAgentIdAsync(agentId);
        if (site != null) { site.IsPublished = false; site.UpdatedAt = DateTime.UtcNow; _uow.AgentWebsites.Update(site); await _uow.SaveChangesAsync(); }
    }

    public async Task<IEnumerable<WebsiteTemplate>> GetTemplatesAsync()
    {
        var templates = await _uow.WebsiteTemplates.FindAsync(t => t.IsActive);
        return templates
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.Name);
    }

    public Task<WebsiteTemplate?> GetTemplateByIdAsync(int id) =>
        _uow.WebsiteTemplates.GetByIdAsync(id);

    public async Task<WebsiteTemplate> EnsureDefaultTemplateAsync()
    {
        var activeTemplates = await GetTemplatesAsync();
        var activeTemplate = activeTemplates
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.Name)
            .FirstOrDefault();
        if (activeTemplate != null)
        {
            return activeTemplate;
        }

        var template = WebsiteTemplateSeeder.CreateDefaultTemplate();
        await _uow.WebsiteTemplates.AddAsync(template);
        await _uow.SaveChangesAsync();
        return template;
    }
}
