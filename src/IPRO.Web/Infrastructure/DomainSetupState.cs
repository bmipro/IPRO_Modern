using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

// 459 (2026-09-02): "done" for the custom-domain setup panel means every check the Connection
// status panel shows is green -- DNS found, binding connected, certificate secured, AND the bare
// domain forwarding to www (the step agents skip, and the one that costs them half their visitors).
// Only then do the registrar instructions collapse out of the way.
public static class DomainSetupState
{
    public static bool IsFullyConnected(AgentDomain? primary)
    {
        if (primary == null) return false;
        var dnsReady = primary.DnsStatus == AgentDomainStatus.DnsReady || primary.DnsStatus == AgentDomainStatus.Bound;
        var bound = primary.AzureBindingStatus == AgentDomainStatus.Bound;
        var secured = primary.SslStatus == AgentDomainStatus.Bound || primary.SslStatus == "SslBound";
        return dnsReady && bound && secured && primary.RootRedirectsToWww;
    }
}
