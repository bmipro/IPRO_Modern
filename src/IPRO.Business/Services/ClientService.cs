using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;

namespace IPRO.Business.Services;

public class ClientService : IClientService
{
    private readonly IUnitOfWork _uow;

    public ClientService(IUnitOfWork uow) => _uow = uow;

    public Task<IEnumerable<Client>> GetByAgentAsync(int agentId) =>
        _uow.Clients.FindAsync(c => c.AgentUserId == agentId);

    public Task<Client?> GetByIdAsync(int id) =>
        _uow.Clients.GetByIdAsync(id);

    public async Task<Client> CreateAsync(Client client)
    {
        client.CreatedAt = DateTime.UtcNow;
        client.UpdatedAt = DateTime.UtcNow;
        await _uow.Clients.AddAsync(client);
        await _uow.SaveChangesAsync();
        return client;
    }

    public async Task UpdateAsync(Client client)
    {
        client.UpdatedAt = DateTime.UtcNow;
        _uow.Clients.Update(client);
        await _uow.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var client = await _uow.Clients.GetByIdAsync(id);
        if (client != null)
        {
            _uow.Clients.Remove(client);
            await _uow.SaveChangesAsync();
        }
    }

    public async Task<IEnumerable<Client>> SearchAsync(int agentId, string query)
    {
        query = query.ToLower();
        return await _uow.Clients.FindAsync(c =>
            c.AgentUserId == agentId &&
            (c.FirstName.ToLower().Contains(query) ||
             c.LastName.ToLower().Contains(query) ||
             c.Email.ToLower().Contains(query) ||
             c.Phone.Contains(query)));
    }

    public Task<IEnumerable<Client>> GetNewsletterSubscribersAsync(int agentId) =>
        _uow.Clients.FindAsync(c => c.AgentUserId == agentId && c.IsNewsletterSubscribed);

    public Task<int> GetCountAsync(int agentId) =>
        _uow.Clients.CountAsync(c => c.AgentUserId == agentId);

    public async Task AddCommentAsync(ClientComment comment)
    {
        comment.CreatedAt = DateTime.UtcNow;
        await _uow.ClientComments.AddAsync(comment);
        await _uow.SaveChangesAsync();
    }

    public Task<IEnumerable<ClientComment>> GetCommentsAsync(int clientId) =>
        _uow.ClientComments.FindAsync(cc => cc.ClientId == clientId);

    public async Task<int> ImportClientsAsync(int agentId, IEnumerable<Client> clients)
    {
        var list = clients.ToList();
        list.ForEach(c => { c.AgentUserId = agentId; c.CreatedAt = DateTime.UtcNow; c.UpdatedAt = DateTime.UtcNow; });
        await _uow.Clients.AddRangeAsync(list);
        await _uow.SaveChangesAsync();
        return list.Count;
    }
}
