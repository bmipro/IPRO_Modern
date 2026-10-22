namespace IPRO.Utility;

// 595: what DNS shows right now for the records a custom domain's setup asks for, as found -- nothing judged here
// (DomainSetupGuide compares it with what is expected). Every list is null when nobody answered the question, and
// empty when the answer was "no such record". Values come from the domain's own DNS, which its owner writes: they
// are shown to the adviser as text only, cut short and stripped of control characters (PlatformHostNames.Printable).
public sealed class DomainSetupLook
{
    public DateTime CheckedAtUtc { get; init; }

    // Whether the answers came from the zone's own name servers (true) or from public resolvers, which can keep
    // saying "no such record" for a while after one is added.
    public bool FromZoneServers { get; init; }

    // The zone the domain's records live in (yourfirm.ca for www.yourfirm.ca), and its name servers; empty when
    // they could not be found.
    public string Zone { get; init; } = string.Empty;
    public IReadOnlyList<string> NameServers { get; init; } = Array.Empty<string>();

    // TXT values at _ipro-verify.<proven name> (592).
    public IReadOnlyList<string>? ProofValues { get; init; }

    // The www name (the domain itself): the CNAME it points to, if any, and the addresses it ends at.
    public string? WwwCname { get; init; }
    public IReadOnlyList<string>? WwwAddresses { get; init; }

    // The short address's own A and AAAA records (553), and the TXT values at asuid.<short address>.
    public IReadOnlyList<string>? RootAddresses { get; init; }
    public IReadOnlyList<string>? AsuidValues { get; init; }

    // Where the domain's email goes (its MX records' mail servers), and whether that depends on the short address
    // itself -- an MX naming the short address, or a mail server at the short address's current address (a web host
    // with the website and the mail on one server, cPanel's default). Then pointing the short address at iPro would
    // stop the email, and the guide says so (595's review, H2). Null when it could not be checked.
    public IReadOnlyList<string>? MailServers { get; init; }
    public bool? MailOnShortAddress { get; init; }

    // The platform's own addresses (the CNAME target resolved, and App:WebsiteAddress), to tell "ours" apart.
    public IReadOnlyList<string> PlatformAddresses { get; init; } = Array.Empty<string>();
}
