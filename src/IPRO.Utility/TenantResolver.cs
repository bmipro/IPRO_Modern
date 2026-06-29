using Microsoft.AspNetCore.Http;

namespace IPRO.Utility;

/// <summary>
/// Resolves which agent/tenant is being accessed based on the incoming domain.
/// This is the heart of the multi-tenancy system.
/// </summary>
public interface ITenantResolver
{
    string? GetCurrentDomain(HttpContext context);
    bool IsAdminDomain(HttpContext context);
}

public class DomainTenantResolver : ITenantResolver
{
    private readonly string _adminDomain;

    public DomainTenantResolver(string adminDomain)
    {
        _adminDomain = adminDomain;
    }

    public string? GetCurrentDomain(HttpContext context)
    {
        var host = context.Request.Host.Host.ToLower();
        return host == _adminDomain ? null : host;
    }

    public bool IsAdminDomain(HttpContext context)
    {
        var host = context.Request.Host.Host.ToLower();
        return host == _adminDomain || host.StartsWith("admin.");
    }
}
