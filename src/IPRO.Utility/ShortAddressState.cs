using IPRO.Entities;

namespace IPRO.Utility;

// 553: what every screen says about a custom domain's short address (example.com beside
// www.example.com). There are two ways to make it work and the screens used to know one:
//
//   Forwarding -- the registrar redirects the name. One setting, but most registrars forward only
//                 the home address: GoDaddy's forwarder answered 404 for /about on all three
//                 domains measured on 2026-10-06, so every link to an inner page was lost.
//   Direct     -- the name points at the platform (an A record plus the asuid TXT record). The
//                 platform binds and secures it like the www name and sends every address on to
//                 www with its path, so a site that lived on the short address keeps its links.
//
// One place, so the agent's two panels and SuperAdmin's list cannot word the same state differently.
public static class ShortAddressState
{
    public sealed record State(string Label, string Badge, bool Direct, bool Working);

    // A domain added as a sub-domain (clients.firm.ca) has no separate short address.
    public static bool HasShortAddress(AgentDomain domain) =>
        !string.IsNullOrWhiteSpace(domain.RootDomain) &&
        !string.Equals(domain.RootDomain, domain.DomainName, StringComparison.OrdinalIgnoreCase);

    public static bool PointsAtPlatform(AgentDomain domain) => domain.RootDnsStatus == AgentDomainStatus.Bound;

    public static State Describe(AgentDomain domain)
    {
        // "Needs attention" is reserved for what only the adviser can fix, and the row's sentence
        // (RootLastError) says what: a second A record beside ours (RootDnsStatus = Failed), or a
        // binding Azure refuses for a reason of theirs, the TXT record above all
        // (RootAzureBindingStatus = Failed). Everything that mends itself is Connecting or Securing.
        if (domain.RootDnsStatus == AgentDomainStatus.Failed)
            return new State("Needs attention", "bg-danger", Direct: true, Working: false);

        if (PointsAtPlatform(domain))
        {
            var bound = domain.RootAzureBindingStatus == AgentDomainStatus.Bound;
            if (bound && domain.RootSslStatus == AgentDomainStatus.Bound && domain.RootRedirectsToWww)
                return new State("Connected", "bg-success", Direct: true, Working: true);
            if (domain.RootAzureBindingStatus == AgentDomainStatus.Failed)
                return new State("Needs attention", "bg-danger", Direct: true, Working: false);
            if (bound)
                return new State("Securing", "bg-warning text-dark", Direct: true, Working: false);
            return new State("Connecting", "bg-secondary", Direct: true, Working: false);
        }

        return domain.RootRedirectsToWww
            ? new State("Forwarding OK", "bg-success", Direct: false, Working: true)
            : new State("Not set up", "bg-light text-dark", Direct: false, Working: false);
    }
}
