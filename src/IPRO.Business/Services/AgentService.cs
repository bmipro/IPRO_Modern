using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Identity;

namespace IPRO.Business.Services;

public class AgentService : IAgentService
{
    private readonly IUnitOfWork _uow;
    private readonly IPasswordHasher<AgentUser> _hasher;

    public AgentService(IUnitOfWork uow, IPasswordHasher<AgentUser> hasher)
    {
        _uow = uow;
        _hasher = hasher;
    }

    public Task<AgentUser?> GetByIdAsync(int id) =>
        _uow.AgentUsers.GetByIdAsync(id);

    public Task<AgentUser?> GetByUsernameAsync(string username) =>
        _uow.AgentUsers.FirstOrDefaultAsync(u => u.UserName == username);

    public Task<AgentUser?> GetByDomainAsync(string domain) =>
        _uow.AgentUsers.FirstOrDefaultAsync(u => u.DomainName == domain);

    public async Task<AgentUser?> AuthenticateAsync(string username, string password)
    {
        var user = await GetByUsernameAsync(username);
        if (user == null || !user.IsActive) return null;
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result == PasswordVerificationResult.Success ? user : null;
    }

    public async Task<AgentUser> RegisterAsync(AgentUser user, string plainPassword)
    {
        user.PasswordHash = _hasher.HashPassword(user, plainPassword);
        user.CreatedAt = DateTime.UtcNow;
        await _uow.AgentUsers.AddAsync(user);
        await _uow.SaveChangesAsync();
        return user;
    }

    public async Task UpdateAsync(AgentUser user)
    {
        _uow.AgentUsers.Update(user);
        await _uow.SaveChangesAsync();
    }

    public async Task ChangePasswordAsync(int id, string plainPassword)
    {
        var user = await _uow.AgentUsers.GetByIdAsync(id);
        if (user == null) return;

        user.PasswordHash = _hasher.HashPassword(user, plainPassword);
        user.MustChangePassword = false;
        user.PasswordChangedAt = DateTime.UtcNow;
        _uow.AgentUsers.Update(user);
        await _uow.SaveChangesAsync();
    }

    public async Task DeactivateAsync(int id)
    {
        var user = await _uow.AgentUsers.GetByIdAsync(id);
        if (user != null)
        {
            user.IsActive = false;
            _uow.AgentUsers.Update(user);
            await _uow.SaveChangesAsync();
        }
    }

    public Task<bool> UsernameExistsAsync(string username) =>
        _uow.AgentUsers.ExistsAsync(u => u.UserName == username);

    public Task<bool> DomainExistsAsync(string domain) =>
        _uow.AgentUsers.ExistsAsync(u => u.DomainName == domain);

    public async Task UpdateLastLoginAsync(int id)
    {
        var user = await _uow.AgentUsers.GetByIdAsync(id);
        if (user != null)
        {
            user.LastLoginAt = DateTime.UtcNow;
            _uow.AgentUsers.Update(user);
            await _uow.SaveChangesAsync();
        }
    }

    public Task<IEnumerable<AgentUser>> GetAllAsync() => _uow.AgentUsers.GetAllAsync();

    public Task<IEnumerable<AgentUser>> GetActiveAsync() =>
        _uow.AgentUsers.FindAsync(u => u.IsActive);
}
