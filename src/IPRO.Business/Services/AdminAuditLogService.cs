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
            // The column holds 500. Longer details (583: an unlock naming a long client email) would fail
            // the write after the action itself had been done, leaving it unrecorded.
            Details = details.Length > 500 ? details[..497] + "..." : details
        });
        await _uow.SaveChangesAsync();
    }
}
