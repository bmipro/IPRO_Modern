using IPRO.Entities;

namespace IPRO.Business.Interfaces;

public interface IClientService
{
    Task<IEnumerable<Client>> GetByAgentAsync(int agentId);
    Task<Client?> GetByIdAsync(int id);
    Task<Client> CreateAsync(Client client);
    Task UpdateAsync(Client client);
    Task DeleteAsync(int id);
    Task<IEnumerable<Client>> SearchAsync(int agentId, string query);
    Task<IEnumerable<Client>> GetNewsletterSubscribersAsync(int agentId);
    Task<int> GetCountAsync(int agentId);
    Task AddCommentAsync(ClientComment comment);
    Task<IEnumerable<ClientComment>> GetCommentsAsync(int clientId);
    Task<int> ImportClientsAsync(int agentId, IEnumerable<Client> clients);
}
