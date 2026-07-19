using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;

namespace IPRO.Business.Services;

public class AdminAuditLogService : IAdminAuditLogService
{
    private readonly IUnitOfWork _uow;

    public AdminAuditLogService(IUnitOfWork uow)
    {
        _uow = uow;
    }

    public async Task LogAsync(int adminUserId, string adminUsername, string action, string details)
    {
        await _uow.AdminAuditLogEntries.AddAsync(new AdminAuditLogEntry
        {
            AdminUserId = adminUserId,
            AdminUsername = adminUsername,
            Action = action,
            Details = details
        });
        await _uow.SaveChangesAsync();
    }
}
