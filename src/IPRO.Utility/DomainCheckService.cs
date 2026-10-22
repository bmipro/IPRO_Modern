using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using IPRO.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Utility;

public class DomainCheckService : IDomainCheckService
{
    // Deliberately NOT from IHttpClientFactory: the factory's clients follow redirects, and the root
    // check needs to inspect the first hop rather than the destination. Static because a handler per
    // call would leak sockets; this one is called a few times per 5-minute job run.
    private static readonly HttpClient NoRedirectClient = CreateNoRedirectClient();

    private static HttpClient CreateNoRedirectClient()
    {
        // H4: the pinned handler validates the RESOLVED addresses at connect time, atomically --
        // the pre-checks above/below stay for fast, friendly error messages, but the security
        // boundary is here, where DNS rebinding between check and fetch can no longer win.
        var handler = PublicHostGuard.CreatePinnedHandler();
        handler.AllowAutoRedirect = false;
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        // A User-Agent is REQUIRED here, not cosmetic. HttpClient sends none by default, and
        // GoDaddy's domain-forwarding service answers a request with no User-Agent with 403
        // Forbidden instead of the 301 it gives a browser. Verified 2026-08-06 against ouritems.ca
        // and 411trades.com: UA present => 301 to the www host, UA absent => 403.
        //
        // The 403 is not a redirect, so the check concluded "not forwarding" and told two agents
        // their correctly-configured domains were broken. Registrar forwarding services sit behind
        // bot protection; anything probing them has to look like an ordinary client.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; IPRO-DomainCheck/1.0; +https://app.iproadvisers.com)");

        return client;
    }

    // 553 test seam (the PublicHostGuard.ResolveHook pattern): every name this service looks up.
    internal static Func<string, CancellationToken, Task<IPAddress[]>> ResolveHook =
        (host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAzureDomainAutomationService _azureDomains;
    private readonly ILogger<DomainCheckService> _logger;
    private readonly IConfiguration? _configuration;

    // The platform's own addresses, per CNAME target, for the life of this check run: one job run
    // checks up to fifty domains that all point at the same target.
    // Concurrent since 595: SuperAdmin's list looks at several domains at once (DomainsController.GuidesAsync).
    private readonly ConcurrentDictionary<string, IPAddress[]> _platformAddresses = new(StringComparer.OrdinalIgnoreCase);

    public DomainCheckService(
        IHttpClientFactory httpClientFactory,
        IAzureDomainAutomationService azureDomains,
        ILogger<DomainCheckService> logger,
        IConfiguration? configuration = null)
    {
        _httpClientFactory = httpClientFactory;
        _azureDomains = azureDomains;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<bool> CheckAsync(AgentDomain domain, CancellationToken cancellationToken = default)
    {
        domain.LastCheckedAt = DateTime.UtcNow;
        domain.UpdatedAt = DateTime.UtcNow;
        domain.LastError = string.Empty;
        // 595 (review H1): a check that moves the domain on starts its retry budget again (below).
        var provenBefore = !DomainOwnershipProof.IsNeeded(domain);
        var boundAtBefore = domain.BoundAt;
        var rootBoundBefore = domain.RootAzureBindingStatus == AgentDomainStatus.Bound;

        // 589: a row naming one of iPro's own addresses is never an adviser's domain, whatever it says (a row
        // from before My Website refused them), and neither is a row whose name is no host name at all.
        // Nothing is looked up, fetched or asked of Azure: a probe of iPro's own address finds iPro's page,
        // which the check used to read as "bound".
        if (RefusalFor(domain) is { } refusal)
        {
            domain.DnsStatus = AgentDomainStatus.Failed;
            domain.AzureBindingStatus = AgentDomainStatus.Failed;
            domain.SslStatus = AgentDomainStatus.Failed;
            // Nothing of the short address is the adviser's either: no binding to delete, no certificate to wait for.
            domain.RootDnsStatus = AgentDomainStatus.NotConfigured;
            domain.RootAzureBindingStatus = AgentDomainStatus.NotConfigured;
            domain.RootSslStatus = AgentDomainStatus.NotConfigured;
            domain.RootRedirectsToWww = false;
            domain.RootLastError = string.Empty;
            domain.LastError = refusal;
            _logger.LogWarning("Custom domain {Domain} (row {Id}) refused before any check: {Reason}", PlatformHostNames.Printable(domain.DomainName), domain.Id, refusal);
        }
        else if (await OwnershipWaitAsync(domain, cancellationToken) is { } waiting)
        {
            // 592: not proven to be this adviser's (DomainOwnershipProof). Nothing is resolved, fetched or asked
            // of Azure, and nothing reads as connected: an earlier adviser's binding may still be in Azure with
            // the domain's DNS pointed at us, and binding or probing it now would hand that domain to this row.
            // The short address's binding fields are left as they are: a binding recorded there must stay on
            // record so that removing the domain still removes it.
            domain.DnsStatus = AgentDomainStatus.PendingDns;
            domain.AzureBindingStatus = AgentDomainStatus.BindingPending;
            domain.SslStatus = AgentDomainStatus.BindingPending;
            domain.RootRedirectsToWww = false;
            domain.LastError = waiting;
        }
        else
        {
            await CheckDomainAsync(domain, cancellationToken);
            await CheckRootDomainAsync(domain, cancellationToken);
            await CheckCaaAsync(domain, cancellationToken);
        }

        var fullyBound = domain.DnsStatus == AgentDomainStatus.Bound &&
                          domain.AzureBindingStatus == AgentDomainStatus.Bound &&
                          domain.SslStatus == AgentDomainStatus.Bound;

        // 595 (review H1): proven, connected (for the first time under this proof: BoundAt, so a binding that drops to
        // Failed and comes back is no progress) or the short address connected by this check is progress, and the
        // automatic checks start counting again; until now only "fully bound" did, so a domain that kept moving could
        // still run out of retries and never be looked at again.
        var progressed = (!provenBefore && !DomainOwnershipProof.IsNeeded(domain)) ||
                         domain.BoundAt != boundAtBefore ||
                         (!rootBoundBefore && domain.RootAzureBindingStatus == AgentDomainStatus.Bound);

        if (fullyBound || progressed)
        {
            domain.RetryCount = 0;
            domain.AutoRetryExhausted = false;
            domain.NextRetryAt = null;
            domain.LastFailedAt = null;
        }
        else
        {
            domain.RetryCount++;
            domain.LastFailedAt = DateTime.UtcNow;
            domain.NextRetryAt = domain.RetryCount switch
            {
                <= 11 => null,
                <= 17 => DateTime.UtcNow.AddMinutes(30),
                <= 41 => DateTime.UtcNow.AddHours(4),
                _ => null
            };
            domain.AutoRetryExhausted = domain.RetryCount > 41;
        }

        return fullyBound;
    }

    // 589: why a row may not be checked at all, in the adviser's words, or null.
    private string? RefusalFor(AgentDomain domain) => RefusalFor(domain, _configuration);

    // 595: public, so that the setup guide refuses exactly the rows the check refuses.
    public static string? RefusalFor(AgentDomain domain, IConfiguration? configuration)
    {
        var names = new[] { domain.DomainName, domain.WwwDomain, domain.RootDomain }.Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        if (names.Any(name => PlatformHostNames.IsPlatformHost(name, configuration) || PlatformHostNames.SharesCertificateNameWithPlatformHost(name, configuration)))
        {
            return "This is one of IPRO's own addresses, or too close to one, so it cannot be your website address. Remove it and add a domain you own.";
        }

        if (names.Count == 0 || names.Any(name => !PlatformHostNames.TryCanonicalHost(name, out _)))
        {
            return "This is not a valid domain name, so it cannot be used as a website address. Remove it and add it again, for example www.yourname.com.";
        }

        return null;
    }

    // 592: null when the row is proven to be its adviser's (now or before), else the sentence that says what to add.
    private async Task<string?> OwnershipWaitAsync(AgentDomain domain, CancellationToken cancellationToken)
    {
        if (!DomainOwnershipProof.IsNeeded(domain)) return null;

        if (!DomainOwnershipProof.Covers(domain, domain.RootDomain) || !DomainOwnershipProof.Covers(domain, domain.WwwDomain))
        {
            _logger.LogWarning("Custom domain {Domain} (row {Id}) not checked: its short address {Root} or www name {Www} is not one of its own names",
                PlatformHostNames.Printable(domain.DomainName), domain.Id, PlatformHostNames.Printable(domain.RootDomain), PlatformHostNames.Printable(domain.WwwDomain));
            return "This domain's record is not consistent, so it cannot be connected. Remove it and add it again (proof of ownership: _ipro-verify).";
        }

        // A row without a value (saved by the version before 592 during its release) gets one when My Website opens,
        // and only there: a value made here could replace the one the adviser is copying.
        if (string.IsNullOrWhiteSpace(domain.VerificationToken))
        {
            return DomainOwnershipProof.InstructionWithoutValue(domain);
        }

        // 595: the zone's own name servers first. They say what is there now, so the record counts the moment it is
        // saved, whether or not www points anywhere yet; and their "not there" is the truth, whatever a public
        // resolver still remembers. Only when they cannot be asked does the check go on to the resolvers below.
        var atZone = await ProofAtZoneAsync(DomainOwnershipProof.ProvenName(domain), domain.VerificationToken, cancellationToken);
        if (atZone == true)
        {
            domain.OwnershipVerifiedAt = DateTime.UtcNow;
            _logger.LogWarning("Custom domain {Domain} (row {Id}, agent {AgentUserId}) proven by its {Record} TXT record on the zone's own name servers",
                PlatformHostNames.Printable(domain.DomainName), domain.Id, domain.AgentUserId, DomainOwnershipProof.RecordName(domain));
            return null;
        }
        if (atZone == false)
        {
            return DomainOwnershipProof.Instruction(domain);
        }

        // Not asked before the domain's own name resolves: the adviser adds the CNAME and this record in the same
        // visit, and a public resolver asked too early remembers "no such record" for the zone's negative TTL
        // (three hours on one registrar, ops/domain-switch/dns-check.sh), however often Check now is clicked.
        IPAddress[] addresses;
        try { addresses = await ResolveHook(domain.DomainName, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { addresses = Array.Empty<IPAddress>(); }
        if (addresses.Length == 0)
        {
            return DomainOwnershipProof.Instruction(domain);
        }

        var seen =await DomainOwnershipProof.IsPublishedAsync(domain, DomainOwnershipProof.LookupHook ?? LookUpTxtAsync, cancellationToken);
        if (seen == true)
        {
            domain.OwnershipVerifiedAt = DateTime.UtcNow;
            // Warning: Information never reaches the container log, and this is the record of when and how a
            // domain became this adviser's.
            _logger.LogWarning("Custom domain {Domain} (row {Id}, agent {AgentUserId}) proven by its {Record} TXT record",
                PlatformHostNames.Printable(domain.DomainName), domain.Id, domain.AgentUserId, DomainOwnershipProof.RecordName(domain));
            return null;
        }

        return DomainOwnershipProof.Instruction(domain);
    }

    public async Task<bool?> IsOwnershipPublishedAsync(string provenName, string token, CancellationToken cancellationToken = default) =>
        await ProofAtZoneAsync(provenName, token, cancellationToken) ??
        await DomainOwnershipProof.IsPublishedAsync(provenName, token, DomainOwnershipProof.LookupHook ?? LookUpTxtAsync, cancellationToken);

    // 595: whether the zone's own name servers hold the token at _ipro-verify.<provenName>; null when they cannot settle
    // it, and the public resolvers decide as before 595. They settle it only for a zone that is not signed (DNSSEC): the
    // resolvers validate a signed zone's answers and the zone's servers are asked over plain TCP, so a signed zone keeps
    // the validated path (595's review, M1). And only when every server asked gives the same answer, and the record is no
    // CNAME to somewhere else.
    private async Task<bool?> ProofAtZoneAsync(string provenName, string? token, CancellationToken cancellationToken)
    {
        var proven = ZoneDns.Normalize(provenName);
        if (string.IsNullOrWhiteSpace(token) || proven.Length == 0) return null;
        var zone = await ZoneServersAsync(proven, cancellationToken);
        if (zone == null || zone.Signed != false) return null;
        var values = (await AtZoneAsync(zone, new[] { (DomainOwnershipProof.RecordLabel + "." + proven, ZoneDns.TXT, true) }, cancellationToken))[0];
        return values == null ? null : DomainOwnershipProof.Matches(values, token);
    }

    // ------------------------------------------------------------------------------------------------------------
    // 595: the setup guide's look -- what DNS shows right now for the records a domain's setup asks for, and where its
    // email goes. Only names of the row itself are asked about (its proof record, its www name, its short address, that
    // address's asuid and MX records, and the mail servers those name), and only for a row the check itself would look
    // at (589). Nothing is bound, fetched or saved. The caller gives it a time budget (the cancellation token).

    public async Task<DomainSetupLook?> LookAsync(AgentDomain domain, CancellationToken cancellationToken = default)
    {
        if (RefusalFor(domain) != null) return null;
        if (!DomainOwnershipProof.Covers(domain, domain.RootDomain) || !DomainOwnershipProof.Covers(domain, domain.WwwDomain)) return null;

        var proven = ZoneDns.Normalize(DomainOwnershipProof.ProvenName(domain));
        var www = ZoneDns.Normalize(domain.DomainName);
        var root = ShortAddressState.HasShortAddress(domain) ? ZoneDns.Normalize(domain.RootDomain) : null;
        var proofName = DomainOwnershipProof.RecordLabel + "." + proven;

        var platform = (await PlatformAddressesAsync(domain.DnsTarget, cancellationToken)).Select(a => a.ToString()).ToList();
        var websiteAddress = (_configuration?["App:WebsiteAddress"] ?? string.Empty).Trim();
        if (websiteAddress.Length > 0 && !platform.Contains(websiteAddress)) platform.Add(websiteAddress);

        var zone = await ZoneServersAsync(proven, cancellationToken);
        if (zone is { Addresses.Count: > 0 })
        {
            // All of it in one batch per name server (ZoneDns.QueryManyAsync).
            var questions = new List<(string Name, ushort Type, bool Agree)>
            {
                (proofName, ZoneDns.TXT, true), (www, ZoneDns.CNAME, false), (www, ZoneDns.A, false), (www, ZoneDns.AAAA, false),
            };
            if (root != null)
            {
                questions.Add((root, ZoneDns.A, false));
                questions.Add((root, ZoneDns.AAAA, false));
                questions.Add(("asuid." + root, ZoneDns.TXT, false));
                questions.Add((root, ZoneDns.MX, false));
            }
            var seen = await AtZoneAsync(zone, questions, cancellationToken);
            var (proof, cname, wwwA, wwwAaaa) = (seen[0], seen[1], seen[2], seen[3]);
            if (proof != null || cname != null)
            {
                var rootAddresses = root == null ? null : Addresses(seen[4], seen[5]);
                var mailServers = root == null ? null : seen[7];
                return new DomainSetupLook
                {
                    CheckedAtUtc = DateTime.UtcNow, FromZoneServers = true, Zone = zone.Zone, NameServers = zone.Names,
                    ProofValues = proof,
                    WwwCname = cname?.FirstOrDefault(),
                    // Through a CNAME the guide reads the CNAME alone; without one, www's own address records.
                    WwwAddresses = cname == null ? null : cname.Count > 0 ? new List<string>() : Addresses(wwwA, wwwAaaa),
                    RootAddresses = rootAddresses,
                    AsuidValues = root == null ? null : seen[6],
                    MailServers = mailServers,
                    MailOnShortAddress = root == null ? false : await MailOnShortAddressAsync(root, zone, mailServers, rootAddresses, cancellationToken),
                    PlatformAddresses = platform,
                };
            }
        }

        // The public resolvers, when the zone's own servers cannot be asked. They remember "no such record" for a while,
        // so a record is asked about only once the name it depends on resolves (592's rule): the proof and the CNAME once
        // www does (review M-B: a CNAME asked too early was remembered as missing, and the guide said to delete the
        // correct record), asuid once the short address points here.
        var resolvedWww = await ResolveForLookAsync(www, cancellationToken);
        var cnames = resolvedWww is { Count: > 0 } ? await DohValuesAsync(www, ZoneDns.CNAME, cancellationToken) : null;
        var proofValues = resolvedWww is { Count: > 0 } ? await DohValuesAsync(proofName, ZoneDns.TXT, cancellationToken) : null;
        var resolvedRoot = root == null ? null : await ResolveForLookAsync(root, cancellationToken);
        var asuidValues = root != null && resolvedRoot != null && resolvedRoot.Any(platform.Contains)
            ? await DohValuesAsync("asuid." + root, ZoneDns.TXT, cancellationToken)
            : null;
        var resolverMail = root == null ? null : await DohValuesAsync(root, ZoneDns.MX, cancellationToken);
        return new DomainSetupLook
        {
            CheckedAtUtc = DateTime.UtcNow, FromZoneServers = false, Zone = zone?.Zone ?? string.Empty, NameServers = zone?.Names ?? Array.Empty<string>(),
            ProofValues = proofValues,
            WwwCname = cnames?.FirstOrDefault(),
            WwwAddresses = resolvedWww,
            RootAddresses = resolvedRoot,
            AsuidValues = asuidValues,
            MailServers = resolverMail,
            MailOnShortAddress = root == null ? false : await MailOnShortAddressAsync(root, null, resolverMail, resolvedRoot, cancellationToken),
            PlatformAddresses = platform,
        };
    }

    // An address list from an A and an AAAA answer: null when the A question went unanswered (unknown, not "none").
    private static List<string>? Addresses(List<string>? a, List<string>? aaaa) =>
        a == null ? null : a.Concat(aaaa ?? new List<string>()).ToList();

    // Whether the domain's email depends on its short address: an MX naming the short address itself, one naming a name
    // that is a CNAME to it, or a mail server at the short address's current address. False only when every mail server
    // was checked and none does; null when that could not be told (review L-d). A null MX ("MX 0 .") names no server.
    private async Task<bool?> MailOnShortAddressAsync(string root, ZoneServers? zone, List<string>? mailServers, List<string>? rootAddresses, CancellationToken cancellationToken)
    {
        if (mailServers == null) return null;
        var unknown = false;
        foreach (var server in mailServers.Select(ZoneDns.Normalize).Where(s => s.Length > 0).Take(4))
        {
            if (server == root) return true;
            if (server.EndsWith("." + root, StringComparison.Ordinal))
            {
                var alias = zone is { Addresses.Count: > 0 }
                    ? (await AtZoneAsync(zone, new[] { (server, ZoneDns.CNAME, false) }, cancellationToken))[0]
                    : await DohValuesAsync(server, ZoneDns.CNAME, cancellationToken);
                if (alias == null) unknown = true;
                else if (alias.Any(target => ZoneDns.Normalize(target) == root)) return true;
            }
            if (rootAddresses == null) { unknown = true; continue; }
            if (rootAddresses.Count == 0) continue;
            var at = await ResolveForLookAsync(server, cancellationToken);
            if (at == null) unknown = true;
            else if (at.Any(rootAddresses.Contains)) return true;
        }
        return unknown ? null : false;
    }

    // The platform's resolver's answer for a name, public addresses only: an address inside our network is never shown.
    private async Task<List<string>?> ResolveForLookAsync(string host, CancellationToken cancellationToken)
    {
        try { return (await ResolveHook(host, cancellationToken)).Where(a => !PublicHostGuard.IsBlockedAddress(a)).Select(a => a.ToString()).ToList(); }
        catch (SocketException) { return new List<string>(); }                     // no such name
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    // A zone and its name servers: their names (to tell the DNS host), up to two public addresses to ask, and whether the
    // zone is signed (a DS record at its parent; null when that could not be told).
    private sealed record ZoneServers(string Zone, IReadOnlyList<string> Names, IReadOnlyList<IPAddress> Addresses, bool? Signed, DateTime FoundAtUtc);

    // Name servers change rarely: found once for half an hour, per app instance (bounded). A zone whose signing could not
    // be told is found again within two minutes (review L-c).
    private static readonly ConcurrentDictionary<string, ZoneServers> Zones = new(StringComparer.Ordinal);
    private static readonly TimeSpan ZoneLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan UnsureZoneLifetime = TimeSpan.FromMinutes(2);

    // A server that could not be reached (no connection, or nothing at all before the timeout) is left out for ten
    // minutes, so one that hangs costs one timeout, not one per look. A server that answers -- even "refused" for a
    // zone it no longer serves -- is never left out: other domains on it are not held to one adviser's broken zone
    // (review M-C).
    private static readonly ConcurrentDictionary<IPAddress, DateTime> QuietServers = new();
    private static readonly TimeSpan QuietFor = TimeSpan.FromMinutes(10);

    // Addresses a zone's name server may never be: Azure's own DNS and host endpoint.
    private static readonly IPAddress[] NeverAsk = { IPAddress.Parse("168.63.129.16"), IPAddress.Parse("169.254.169.254") };

    internal static void ForgetZones()
    {
        Zones.Clear();
        QuietServers.Clear();
    }

    // The zone's servers are asked in production (no seam set) and by tests that script them (ZoneDns.QueryHook); a
    // test that scripts only the public resolvers (DomainOwnershipProof.LookupHook) gets the resolvers alone, as before.
    // Both seams are internal: the apps themselves cannot set either.
    private static bool ZoneServersAllowed => ZoneDns.QueryHook != null || DomainOwnershipProof.LookupHook == null;

    private async Task<ZoneServers?> ZoneServersAsync(string provenName, CancellationToken cancellationToken)
    {
        if (!ZoneServersAllowed) return null;
        var name = ZoneDns.Normalize(provenName);
        if (Zones.TryGetValue(name, out var known) &&
            DateTime.UtcNow - known.FoundAtUtc < (known.Signed == null ? UnsureZoneLifetime : ZoneLifetime)) return known;

        // The proven name is usually the zone itself (yourfirm.ca); a sub-domain added as the address lives in its
        // parent's zone, so walk up until a name has name servers of its own.
        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i <= labels.Length - 2 && i < 4; i++)
        {
            var candidate = string.Join('.', labels[i..]);
            var nameServers = await DohValuesAsync(candidate, ZoneDns.NS, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (nameServers == null) return null;                          // nobody answered: not remembered, ask again later
            if (nameServers.Count == 0) continue;

            var addresses = new List<IPAddress>();
            foreach (var server in nameServers.Where(s => PlatformHostNames.TryCanonicalHost(s, out _)).Take(4))
            {
                IPAddress[] resolved;
                try { resolved = await ResolveHook(server, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
                // Public addresses only: a zone's owner chooses its name servers, and could name one inside our network.
                var usable = resolved.Where(a => !PublicHostGuard.IsBlockedAddress(a) && !NeverAsk.Contains(a))
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6).FirstOrDefault();
                if (usable != null && !addresses.Contains(usable)) addresses.Add(usable);
                if (addresses.Count == 2) break;
            }

            var delegationSigned = await DohValuesAsync(candidate, ZoneDns.DS, cancellationToken);
            // A question cut short by the caller's budget is no answer: nothing is remembered from it (review L-c).
            cancellationToken.ThrowIfCancellationRequested();
            var found = new ZoneServers(candidate, nameServers.Take(4).ToList(), addresses, delegationSigned == null ? null : delegationSigned.Count > 0, DateTime.UtcNow);
            if (Zones.Count > 2000) Zones.Clear();
            Zones[name] = found;
            return found;
        }
        return null;
    }

    // Questions about one zone, asked of its servers (one connection each). For each question: the values, an empty list
    // for "no such record", or null when no server gave an answer of its own (a referral, REFUSED or SERVFAIL, a
    // malformed reply, a server that could not be reached) or the name is a CNAME to somewhere else. With Agree, every
    // server that answered must have said the same, else null.
    private static async Task<List<string>?[]> AtZoneAsync(ZoneServers zone, IReadOnlyList<(string Name, ushort Type, bool Agree)> questions, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (QuietServers.Count > 200)
            foreach (var old in QuietServers.Where(q => now - q.Value > QuietFor).Select(q => q.Key).ToList()) QuietServers.TryRemove(old, out _);
        var servers = zone.Addresses.Where(a => !QuietServers.TryGetValue(a, out var since) || now - since > QuietFor).ToList();
        var asked = questions.Select(q => (q.Name, q.Type)).ToList();
        var batches = await Task.WhenAll(servers.Select(server => ZoneDns.QueryManyAsync(server, asked, cancellationToken)));
        for (var s = 0; s < servers.Count; s++)
        {
            if (!batches[s].Reached && !cancellationToken.IsCancellationRequested) QuietServers[servers[s]] = DateTime.UtcNow;
        }

        var results = new List<string>?[questions.Count];
        for (var q = 0; q < questions.Count; q++)
        {
            var (name, type, agree) = questions[q];
            var given = batches.Select(batch => ValuesOf(batch.Answers[q], name, type)).Where(values => values != null).Select(values => values!).ToList();
            if (given.Count == 0) continue;
            var first = given[0].OrderBy(v => v, StringComparer.Ordinal).ToList();
            results[q] = !agree || given.All(values => values.OrderBy(v => v, StringComparer.Ordinal).SequenceEqual(first)) ? given[0] : null;
        }
        return results;
    }

    private static List<string>? ValuesOf(ZoneDns.Answer? answer, string name, ushort type)
    {
        if (answer == null || !answer.Usable) return null;
        if (!answer.Authoritative && answer.Records.Count == 0) return null;                  // a referral
        if (type != ZoneDns.CNAME && answer.Values(ZoneDns.CNAME, name).Any()) return null;   // an alias: the resolvers follow it
        return answer.Values(type, name).ToList();
    }

    // One record type of one name from the public resolvers (DNS over HTTPS: Google, then Cloudflare).
    private async Task<List<string>?> DohValuesAsync(string name, ushort type, CancellationToken cancellationToken)
    {
        var lookup = DomainOwnershipProof.LookupHook ?? LookUpTxtAsync;
        var typeName = type switch { ZoneDns.NS => "NS", ZoneDns.CNAME => "CNAME", ZoneDns.TXT => "TXT", ZoneDns.AAAA => "AAAA", ZoneDns.MX => "MX", ZoneDns.DS => "DS", _ => "A" };
        var escaped = Uri.EscapeDataString(name);
        foreach (var address in new[] { $"https://dns.google/resolve?name={escaped}&type={typeName}", $"https://cloudflare-dns.com/dns-query?name={escaped}&type={typeName}" })
        {
            string? json;
            try { json = await lookup(address, cancellationToken); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { continue; }
            if (ParseResolverAnswer(json, name, type) is { } values) return values;
        }
        return null;
    }

    // A resolver's JSON answer: the values of `type` records for `name` (TXT strings joined; an MX's mail server without
    // its preference), an empty list for an answer without them or NXDOMAIN, null for anything else.
    internal static List<string>? ParseResolverAnswer(string? json, string name, ushort type)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Status", out var status) || status.ValueKind != System.Text.Json.JsonValueKind.Number) return null;
            var code = status.GetInt32();
            if (code == ZoneDns.NameError) return new List<string>();
            if (code != ZoneDns.NoError) return null;
            if (type == ZoneDns.TXT) return DomainOwnershipProof.ParseAnswer(json);
            var values = new List<string>();
            if (!document.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != System.Text.Json.JsonValueKind.Array) return values;
            foreach (var answer in answers.EnumerateArray())
            {
                if (!answer.TryGetProperty("type", out var kind) || kind.ValueKind != System.Text.Json.JsonValueKind.Number || kind.GetInt32() != type) continue;
                if (!answer.TryGetProperty("name", out var owner) || owner.ValueKind != System.Text.Json.JsonValueKind.String ||
                    ZoneDns.Normalize(owner.GetString()) != ZoneDns.Normalize(name)) continue;
                if (!answer.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var text = data.GetString() ?? string.Empty;
                if (type == ZoneDns.MX) text = text.Contains(' ') ? text[(text.IndexOf(' ') + 1)..] : text;
                values.Add(ZoneDns.Normalize(text));
            }
            return values;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // One resolver's JSON answer for a TXT question (the address is built by DomainOwnershipProof). Null on any failure.
    private async Task<string?> LookUpTxtAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await client.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(timeout.Token) : null;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "TXT lookup {Address} did not answer", address);
            return null;
        }
    }

    // 506: while a certificate is still wanted, read the domain's CAA records. A list that leaves
    // DigiCert out means the certificate will never arrive, and until now nothing said why. A lookup
    // that fails changes nothing: what was known stays.
    internal async Task CheckCaaAsync(AgentDomain domain, CancellationToken cancellationToken = default)
    {
        var hosts = new List<string>();
        if (domain.SslStatus != AgentDomainStatus.Bound && !string.IsNullOrWhiteSpace(domain.DomainName)) hosts.Add(domain.DomainName);
        // The short address needs a certificate of its own only when it points straight at the platform (553).
        if (domain.RootDnsStatus == AgentDomainStatus.Bound && domain.RootSslStatus != AgentDomainStatus.Bound &&
            !string.IsNullOrWhiteSpace(domain.RootDomain) && !hosts.Contains(domain.RootDomain, StringComparer.OrdinalIgnoreCase))
            hosts.Add(domain.RootDomain);
        if (hosts.Count == 0)
        {
            domain.CaaBlockingName = string.Empty;   // every certificate is in place
            return;
        }

        var blocking = string.Empty;
        foreach (var host in hosts)
        {
            if (PublicHostGuard.IsBlockedHost(host)) continue;
            var found = await CaaCheck.FindBlockingNameAsync(host, CaaCheck.LookupHook ?? LookUpCaaAsync, cancellationToken);
            if (found == null) return;                // a lookup failed: keep what was known
            if (found.Length > 0) { blocking = found; break; }
        }
        if (blocking.Length > 0 && blocking != domain.CaaBlockingName)
            _logger.LogWarning("Custom domain {Domain}: the CAA records at {Name} do not allow {Authority}, so no certificate can be issued", domain.DomainName, blocking, CaaCheck.Authority);
        domain.CaaBlockingName = blocking;
    }

    // One name's CAA records from a public DNS-over-HTTPS resolver, as its JSON. Null on any failure.
    private async Task<string?> LookUpCaaAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync($"https://dns.google/resolve?name={Uri.EscapeDataString(name)}&type=CAA", timeout.Token);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(timeout.Token) : null;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "CAA lookup for {Name} did not answer", name);
            return null;
        }
    }

    private async Task CheckDomainAsync(AgentDomain domain, CancellationToken cancellationToken)
    {
        try
        {
            // A5-M-SSRF: an IP-literal "domain" is refused before we even resolve it.
            if (PublicHostGuard.IsBlockedHost(domain.DomainName))
            {
                domain.DnsStatus = AgentDomainStatus.Failed;
                domain.LastError = "This is not a public domain name, so it cannot be used as a website address.";
                return;
            }

            var addresses = await ResolveHook(domain.DomainName, cancellationToken);
            if (addresses.Length == 0)
            {
                domain.DnsStatus = AgentDomainStatus.PendingDns;
                domain.LastError = "Waiting for DNS propagation. IPRO will check again automatically within 5 minutes.";
                return;
            }

            // A5-M-SSRF: a name resolving to loopback / private / link-local space is a probe of
            // things only this server can reach, not a customer domain. Refuse to fetch it, and do
            // not ask Azure to bind it either.
            if (PublicHostGuard.AnyBlocked(addresses))
            {
                domain.DnsStatus = AgentDomainStatus.Failed;
                domain.LastError = "This domain points at a private or internal address, so it cannot be used as a website address.";
                _logger.LogWarning("Custom domain {Domain} resolves to a non-public address; check refused.", domain.DomainName);
                return;
            }

            domain.DnsStatus = AgentDomainStatus.DnsReady;
            await EnsureAzureBindingAsync(domain, cancellationToken);
        }
        catch (Exception ex)
        {
            domain.DnsStatus = AgentDomainStatus.PendingDns;
            domain.LastError = "Waiting for DNS propagation. Confirm the CNAME points to " + domain.DnsTarget + "; IPRO will check again automatically within 5 minutes.";
            _logger.LogInformation(ex, "DNS check failed for custom domain {Domain}", domain.DomainName);
        }
    }

    private async Task EnsureAzureBindingAsync(AgentDomain domain, CancellationToken cancellationToken)
    {
        if (domain.AzureBindingStatus != AgentDomainStatus.Bound ||
            (_azureDomains.IsConfigured && domain.SslStatus != AgentDomainStatus.Bound))
        {
            var result = await _azureDomains.EnsureDomainAsync(domain.DomainName, cancellationToken);
            if (result.Success)
            {
                if (domain.AzureBindingStatus != AgentDomainStatus.Bound && CertificateClock.FirstBindingFor(domain)) domain.BoundAt = DateTime.UtcNow;   // 595: the certificate's clock
                domain.DnsStatus = AgentDomainStatus.Bound;
                domain.AzureBindingStatus = AgentDomainStatus.Bound;
                domain.SslStatus = result.SslBound ? AgentDomainStatus.Bound : AgentDomainStatus.BindingPending;
                domain.LastError = result.SslBound ? string.Empty : result.Message;
                return;
            }

            if (_azureDomains.IsConfigured)
            {
                domain.AzureBindingStatus = AgentDomainStatus.Failed;
                domain.SslStatus = AgentDomainStatus.BindingPending;
                domain.LastError = result.Message;
                return;
            }
        }

        await CheckAzureBindingAsync(domain, cancellationToken);
    }

    private async Task CheckAzureBindingAsync(AgentDomain domain, CancellationToken cancellationToken)
    {
        try
        {
            // A5-M-SSRF: the factory client follows redirects, so a hostile domain could answer our
            // probe with a 302 to an internal address and have us fetch it. The no-redirect client
            // asks the only question this check has: what does the FIRST response look like? A 3xx
            // has no Azure "not configured" marker in its body, so a legitimately-bound site that
            // redirects http->https still lands in the Bound branch exactly as it did before.
            using var response = await NoRedirectClient.GetAsync("http://" + domain.DomainName, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (body.Contains("Custom domain has not been configured inside Azure", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("404 Web Site not found", StringComparison.OrdinalIgnoreCase))
            {
                domain.AzureBindingStatus = AgentDomainStatus.BindingPending;
                domain.SslStatus = AgentDomainStatus.BindingPending;
                domain.LastError = "DNS is ready. Azure custom-domain binding is still needed.";
                return;
            }

            if (domain.AzureBindingStatus != AgentDomainStatus.Bound && CertificateClock.FirstBindingFor(domain)) domain.BoundAt = DateTime.UtcNow;   // 595
            domain.AzureBindingStatus = AgentDomainStatus.Bound;
            domain.SslStatus = AgentDomainStatus.Bound;
            domain.DnsStatus = AgentDomainStatus.Bound;
            domain.LastError = string.Empty;
        }
        catch (Exception ex)
        {
            domain.AzureBindingStatus = AgentDomainStatus.BindingPending;
            domain.LastError = "DNS is ready, but the site could not be checked yet.";
            _logger.LogInformation(ex, "Azure binding check failed for custom domain {Domain}", domain.DomainName);
        }
    }

    private async Task CheckRootDomainAsync(AgentDomain domain, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(domain.RootDomain) ||
            string.Equals(domain.RootDomain, domain.DomainName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        domain.RootLastCheckedAt = DateTime.UtcNow;
        try
        {
            var addresses = await ResolveHook(domain.RootDomain, cancellationToken);
            if (addresses.Length == 0)
            {
                domain.RootDnsStatus = AgentDomainStatus.NotConfigured;
                domain.RootRedirectsToWww = false;
                domain.RootLastError = "The root domain does not resolve yet. Point it at us or forward it to the www address (see the setup steps).";
                return;
            }

            // A5-M-SSRF: same refusal as the www check -- the root fetch below must never target
            // loopback / private / link-local space.
            if (PublicHostGuard.IsBlockedHost(domain.RootDomain) || PublicHostGuard.AnyBlocked(addresses))
            {
                domain.RootDnsStatus = AgentDomainStatus.NotConfigured;
                domain.RootRedirectsToWww = false;
                domain.RootLastError = "The root domain points at a private or internal address, so it cannot be checked.";
                _logger.LogWarning("Root domain {Domain} resolves to a non-public address; check refused.", domain.RootDomain);
                return;
            }

            // 553: is the short address pointed straight at the platform (an A record) rather than
            // through a registrar's forwarding? Then it is ours to bind and secure, like the www
            // name, and the app itself sends it on to www with the page path kept -- which a
            // registrar's forwarding does not do (GoDaddy's answers 404 for anything but the home
            // address; measured 2026-10-06 on three domains).
            var platform = await PlatformAddressesAsync(domain.DnsTarget, cancellationToken);
            var here = addresses.Count(address => platform.Contains(address));
            if (here > 0)
            {
                await CheckDirectRootAsync(domain, addresses.Length - here, cancellationToken,
                    addresses.Where(address => !platform.Contains(address)).Select(address => address.ToString()).ToList());
                return;
            }

            domain.RootDnsStatus = AgentDomainStatus.DnsReady;

            // Ask only the question we actually care about: does the registrar redirect the bare
            // domain to the www host? Read the FIRST response's Location header instead of following
            // the chain to completion.
            //
            // Following it through meant the app fetched its own public hostname over HTTPS from
            // inside App Service -- a TLS handshake and a round trip that can fail or time out for
            // reasons that have nothing to do with the agent's forwarding, and any such failure was
            // reported to the agent as "not forwarding". One redirect hop is the whole question.
            using var response = await NoRedirectClient.GetAsync(
                "http://" + domain.RootDomain, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var isRedirect = (int)response.StatusCode is >= 300 and < 400;
            var location = response.Headers.Location;
            string? target = null;

            if (isRedirect && location != null)
            {
                // Location may be relative; resolve against the request URI before reading the host.
                target = location.IsAbsoluteUri
                    ? location.Host
                    : new Uri(new Uri("http://" + domain.RootDomain), location).Host;
            }

            domain.RootRedirectsToWww =
                string.Equals(target, domain.WwwDomain, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, domain.DomainName, StringComparison.OrdinalIgnoreCase);

            domain.RootLastError = domain.RootRedirectsToWww
                ? string.Empty
                : isRedirect
                    ? $"The root domain redirects to {target ?? "somewhere else"} rather than {domain.WwwDomain}."
                    : $"The root domain answered with {(int)response.StatusCode} instead of redirecting to {domain.WwwDomain}. Visitors typing the bare domain may not reach the site.";

            // Log the inputs to the decision, not just the verdict. Diagnosing a wrong "Not
            // forwarding" from outside meant guessing at which of several steps had failed, with no
            // way to tell a stale value from a failing check.
            //
            // Warning, not Information, ONLY when the answer is negative: Application Insights
            // captures Warning and above by default, so an Information line here is invisible in
            // production -- which is exactly the hole that made this undiagnosable. A successful
            // check stays at Information so we do not manufacture noise.
            if (domain.RootRedirectsToWww)
            {
                _logger.LogInformation("Root check {Root}: forwards to {Target} as expected", domain.RootDomain, target);
            }
            else
            {
                _logger.LogWarning(
                    "Root check {Root} says NOT forwarding: status={Status} location={Location} target={Target} expected={Www}",
                    domain.RootDomain, (int)response.StatusCode, location?.ToString() ?? "(none)",
                    target ?? "(none)", domain.WwwDomain);
            }
        }
        catch (Exception ex)
        {
            domain.RootDnsStatus = AgentDomainStatus.NotConfigured;
            domain.RootRedirectsToWww = false;
            domain.RootLastError = "Could not check the root domain yet.";
            _logger.LogInformation(ex, "Root domain check failed for {Domain}", domain.RootDomain);
        }
    }

    // 553: the short address resolves to the platform. RootDnsStatus = Bound records that; the two
    // Azure statuses record what has been done about it. "Working" (RootRedirectsToWww, the flag
    // every screen reads) waits for the certificate: until then https on the short address shows a
    // browser warning, exactly as the www name does for its first few minutes.
    private async Task CheckDirectRootAsync(AgentDomain domain, int otherAddresses, CancellationToken cancellationToken, IReadOnlyCollection<string>? otherList = null)
    {
        // "Failed" on this row means one thing: the adviser has something to do, and RootLastError
        // says what (ShortAddressState shows it as Needs attention). A name that is already bound
        // stays recorded as bound whatever happens next -- removing the domain must still know
        // there is a binding to delete -- and anything that mends itself is left to the next check.
        // 595 (www.411trades.com, 2026-10-22): the other addresses are GoDaddy's own forwarding, whose A records GoDaddy
        // locks on the DNS screen; they go only when the forwarding is deleted. Say that, not "remove the other records".
        if (otherAddresses > 0 && DnsHosts.ForwardingOf(otherList ?? Array.Empty<string>()) is { } forwarding)
        {
            domain.RootDnsStatus = AgentDomainStatus.Failed;
            domain.RootRedirectsToWww = false;
            domain.RootLastError = forwarding.HowToRemove(domain.RootDomain) + " Then click Check now.";
            return;
        }

        if (otherAddresses > 0)
        {
            // A leftover record beside ours: the name answers in turn from both hosts, so some
            // visitors -- and the certificate authority's validation request -- still reach the old
            // one. RootDnsStatus = Failed is that state: ours, but not ours alone.
            domain.RootDnsStatus = AgentDomainStatus.Failed;
            domain.RootRedirectsToWww = false;
            domain.RootLastError = $"{domain.RootDomain} has more than one address. Remove the other A (and AAAA) records so that ours is the only one left, then click Check now.";
            return;
        }

        domain.RootDnsStatus = AgentDomainStatus.Bound;

        if (domain.RootAzureBindingStatus != AgentDomainStatus.Bound || domain.RootSslStatus != AgentDomainStatus.Bound)
        {
            var result = await _azureDomains.EnsureRootDomainAsync(domain.RootDomain, cancellationToken);
            if (result.Success || result.BindingCreated)
            {
                // Bound. Success without SslBound is the certificate still being issued; a failure
                // AFTER the binding is the certificate order refused for now (Azure's own DNS check
                // still sees the name's previous record). Both are "securing": the every-run path
                // of the job asks again in five minutes.
                if (domain.RootAzureBindingStatus != AgentDomainStatus.Bound) domain.RootBoundAt = DateTime.UtcNow;
                domain.RootAzureBindingStatus = AgentDomainStatus.Bound;
                domain.RootSslStatus = result.Success && result.SslBound ? AgentDomainStatus.Bound : AgentDomainStatus.BindingPending;
                if (!result.Success)
                {
                    _logger.LogWarning("Short address {Root} is bound; its certificate order was not accepted yet: {Message}", domain.RootDomain, result.Message);
                }
            }
            else
            {
                var (sentence, adviserCanFixIt) = DescribeRootFailure(domain.RootDomain, result.Message, _azureDomains.IsConfigured);
                if (domain.RootAzureBindingStatus != AgentDomainStatus.Bound)
                {
                    domain.RootAzureBindingStatus = adviserCanFixIt ? AgentDomainStatus.Failed : AgentDomainStatus.NotConfigured;
                }

                domain.RootRedirectsToWww = false;
                domain.RootLastError = sentence;
                // Warning: the raw Azure answer is the only place the real reason is written down
                // (the adviser sees the plain sentence above), and Information is not captured.
                _logger.LogWarning("Short address {Root} could not be bound: {Message}", domain.RootDomain, result.Message);
                return;
            }
        }

        var secured = domain.RootSslStatus == AgentDomainStatus.Bound;
        domain.RootRedirectsToWww = secured;
        domain.RootLastError = secured
            ? string.Empty
            : $"We are securing {domain.RootDomain}. This usually takes a few minutes and happens automatically.";
    }

    // What the adviser is told when Azure will not bind the short address, and whether it is theirs
    // to fix. Azure's own sentence for the usual case names the record ("A TXT record pointing from
    // asuid.example.com to ... was not found"), so that case gets the one instruction that fixes it.
    internal static (string Sentence, bool AdviserCanFixIt) DescribeRootFailure(string rootDomain, string? azureMessage, bool automationConfigured)
    {
        if (!automationConfigured)
        {
            return ("Domain automation is being finalized on our side. Please check back shortly.", false);
        }

        var message = azureMessage ?? string.Empty;
        if (message.Contains("TXT", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("asuid", StringComparison.OrdinalIgnoreCase))
        {
            return ($"{rootDomain} points at us, but its TXT record is missing or not right yet. Add the TXT record from the setup steps (name asuid), then click Check now.", true);
        }

        if (message.Contains("Conflict", StringComparison.OrdinalIgnoreCase))
        {
            return ($"{rootDomain} is already connected to another site in our system. Contact support if this seems wrong.", true);
        }

        return ($"We are connecting {rootDomain}. It did not go through on this try; we will keep trying, and there is nothing for you to do.", false);
    }

    private async Task<IPAddress[]> PlatformAddressesAsync(string? dnsTarget, CancellationToken cancellationToken)
    {
        var target = (dnsTarget ?? string.Empty).Trim().Trim('.');
        if (target.Length == 0) return Array.Empty<IPAddress>();
        if (_platformAddresses.TryGetValue(target, out var known)) return known;

        IPAddress[] found;
        try
        {
            found = await ResolveHook(target, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)   // 595: a look's budget running out is no failure to resolve
        {
            // Not remembered: the next domain in this run asks again.
            _logger.LogWarning(ex, "The platform's own name {Target} did not resolve; short addresses pointed at it cannot be recognised this run", target);
            return Array.Empty<IPAddress>();
        }

        _platformAddresses[target] = found;
        return found;
    }
}
