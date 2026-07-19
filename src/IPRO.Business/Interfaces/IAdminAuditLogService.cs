namespace IPRO.Business.Interfaces;

public interface IAdminAuditLogService
{
    Task LogAsync(int adminUserId, string adminUsername, string action, string details);
}
