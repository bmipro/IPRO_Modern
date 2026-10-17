using IPRO.Entities;

namespace IPRO.Business.Interfaces;

public interface IAgentService
{
    Task<AgentUser?> GetByIdAsync(int id);
    Task<AgentUser?> GetByUsernameAsync(string username);
    Task<AgentUser?> GetByDomainAsync(string domain);
    // 583: finding the account and checking its password are two steps, so the sign-in lock
    // (SignInLockout) can count between them. Active agents only, by username or email.
    Task<AgentUser?> FindForSignInAsync(string username);
    bool PasswordMatches(AgentUser user, string password);
    Task<AgentUser> RegisterAsync(AgentUser user, string plainPassword);
    Task UpdateAsync(AgentUser user);
    Task ChangePasswordAsync(int id, string plainPassword);
    Task<AgentUser?> InitiatePasswordResetAsync(string email);
    Task<AgentUser?> GetByValidPasswordResetTokenAsync(string token);
    Task<bool> ResetPasswordByTokenAsync(string token, string newPassword);
    Task DeactivateAsync(int id);
    Task<bool> UsernameExistsAsync(string username);
    Task<bool> EmailExistsAsync(string email);
    Task<bool> DomainExistsAsync(string domain);
    Task UpdateLastLoginAsync(int id);
    Task<IEnumerable<AgentUser>> GetAllAsync();
    Task<IEnumerable<AgentUser>> GetActiveAsync();
}
