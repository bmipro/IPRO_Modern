using IPRO.Entities;

namespace IPRO.Business.Interfaces;

public interface IAgentService
{
    Task<AgentUser?> GetByIdAsync(int id);
    Task<AgentUser?> GetByUsernameAsync(string username);
    Task<AgentUser?> GetByDomainAsync(string domain);
    Task<AgentUser?> AuthenticateAsync(string username, string password);
    Task<AgentUser> RegisterAsync(AgentUser user, string plainPassword);
    Task UpdateAsync(AgentUser user);
    Task ChangePasswordAsync(int id, string plainPassword);
    Task DeactivateAsync(int id);
    Task<bool> UsernameExistsAsync(string username);
    Task<bool> EmailExistsAsync(string email);
    Task<bool> DomainExistsAsync(string domain);
    Task UpdateLastLoginAsync(int id);
    Task<IEnumerable<AgentUser>> GetAllAsync();
    Task<IEnumerable<AgentUser>> GetActiveAsync();
}
