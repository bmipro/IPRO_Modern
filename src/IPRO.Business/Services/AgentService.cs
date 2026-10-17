using IPRO.Business.Interfaces;
using IPRO.DataAccess;
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

    public async Task<AgentUser?> FindForSignInAsync(string username)
    {
        username = username?.Trim() ?? "";
        if (username.Length == 0) return null;
        var normalizedEmail = username.ToLowerInvariant();
        // 583: the database's comparison also takes "jose" with an accent for "jose"; the sign-in lock counts a name
        // that is no account by its letters (SignInLockout.SameName), so an account is found the same way.
        var byName = await GetByUsernameAsync(username);
        var user = byName != null && SignInLockout.SameName(byName.UserName, username) ? byName : null;
        if (user == null)
        {
            var byEmail = await _uow.AgentUsers.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
            user = byEmail != null && SignInLockout.SameName(byEmail.Email, username) ? byEmail : null;
        }
        return user == null || !user.IsActive ? null : user;
    }

    public bool PasswordMatches(AgentUser user, string password) =>
        !string.IsNullOrEmpty(password)
        && _hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Success;

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
        // 583: a new password unlocks sign-in at once.
        await SignInLockout.ClearAsync(_uow.Context, new SignInAccount(SignInAccountKind.Adviser, id), DateTime.UtcNow);
    }

    public async Task<AgentUser?> InitiatePasswordResetAsync(string email)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await _uow.AgentUsers.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        if (user == null || !user.IsActive) return null;

        user.PasswordResetToken = Guid.NewGuid().ToString("N");
        user.PasswordResetTokenExpiresAt = DateTime.UtcNow.AddHours(1);
        _uow.AgentUsers.Update(user);
        await _uow.SaveChangesAsync();
        return user;
    }

    public async Task<AgentUser?> GetByValidPasswordResetTokenAsync(string token)
    {
        var user = await _uow.AgentUsers.FirstOrDefaultAsync(u => u.PasswordResetToken == token);
        if (user == null || user.PasswordResetTokenExpiresAt == null || user.PasswordResetTokenExpiresAt <= DateTime.UtcNow)
        {
            return null;
        }
        return user;
    }

    public async Task<bool> ResetPasswordByTokenAsync(string token, string newPassword)
    {
        var user = await GetByValidPasswordResetTokenAsync(token);
        if (user == null) return false;

        user.PasswordHash = _hasher.HashPassword(user, newPassword);
        user.MustChangePassword = false;
        user.PasswordChangedAt = DateTime.UtcNow;
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiresAt = null;
        _uow.AgentUsers.Update(user);
        await _uow.SaveChangesAsync();
        // 583: the forgot-password reset is the honest person's way out of a sign-in lock.
        await SignInLockout.ClearAsync(_uow.Context, new SignInAccount(SignInAccountKind.Adviser, user.Id), DateTime.UtcNow);
        return true;
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

    public Task<bool> EmailExistsAsync(string email)
    {
        email = email.Trim().ToLowerInvariant();
        return _uow.AgentUsers.ExistsAsync(u => u.Email == email);
    }

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
