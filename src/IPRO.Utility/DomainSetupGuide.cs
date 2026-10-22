using System.Globalization;
using IPRO.Entities;
using Microsoft.Extensions.Configuration;

namespace IPRO.Utility;

// 595 (2026-10-21): the "Connect your domain" guide, worked out for one domain. The owner connected a customer's new
// domain himself and found it confusing even as a developer; he asked for "very smooth and idiot proof". Four steps,
// always the same, one open at a time:
//
//   1. Prove it's yours        -- the 592 TXT record (_ipro-verify)
//   2. Point www at iPro       -- the CNAME
//   3. The short address       -- optional, recommended: the A record and TXT asuid (553), or the registrar's forwarding
//   4. iPro finishes           -- connected, padlock ordered, live
//
// From the row (what the domain check has done) and a look at DNS (DomainSetupLook: what is there right now), so
// each step ticks as soon as its record is seen, before the domain check has caught up. Nothing here looks anything
// up or changes anything: My Website, its polling endpoint, the help ticket, the emails and SuperAdmin all call this,
// so they all say the same thing. Every value that came from DNS is shown through Show (printable, cut short).
public static class DomainSetupGuide
{
    public const int StepCount = 4;

    // Bound with no certificate this long: the alert has gone to IPRO (DomainAutomationJob), say so.
    private static readonly TimeSpan CertificateSlowAfter = TimeSpan.FromHours(3);

    // How often the page may ask iPro to connect a domain whose DNS is ahead of it (each asks Azure).
    private static readonly TimeSpan CheckSpacing = TimeSpan.FromMinutes(1);
    // Check now: as far apart as Retry now (WebsiteController.RetryDomain), so a click just after the five-minute job's
    // check still checks (595's third review, L-5).
    private static readonly TimeSpan AskedCheckSpacing = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CertificateCheckSpacing = TimeSpan.FromMinutes(2);
    // Azure refused the binding: as often as the five-minute job, no more (595's review, H1).
    private static readonly TimeSpan RefusedCheckSpacing = TimeSpan.FromMinutes(5);

    private const string CouldNotCheck = "We could not check just now; we will try again in a moment.";
    private const string MailUnknown = "We could not check where your email goes. If your email is with your web host, use your registrar's forwarding (below) instead of these two records, or click I need help.";

    public enum StepState { Done, Current, Upcoming, NotNeeded }

    // What the records must say: the www CNAME's target, the short address's A record, and the asuid value.
    public sealed record Settings(string DnsTarget, string WebsiteAddress, string DomainVerificationId)
    {
        public IConfiguration? Configuration { get; init; }

        // The short address can point straight at the platform only when both values are configured (553).
        public bool CanPointShortAddress => WebsiteAddress.Length > 0 && DomainVerificationId.Length > 0;

        public static Settings From(string? dnsTarget, string? websiteAddress, string? domainVerificationId) =>
            new((dnsTarget ?? string.Empty).Trim(), (websiteAddress ?? string.Empty).Trim(), (domainVerificationId ?? string.Empty).Trim());

        public static Settings From(IConfiguration? configuration) =>
            From(configuration?["App:WebsiteDnsTarget"] ?? "ipro-prod-web.azurewebsites.net", configuration?["App:WebsiteAddress"],
                configuration?["App:DomainVerificationId"]) with { Configuration = configuration };
    }

    // One record to add. Name is what goes in the Name box at this DNS host; FullName is the record's whole name.
    public sealed record Record(string Key, string Type, string Name, string FullName, string Value);

    public sealed class Step
    {
        public int Number { get; init; }
        public string Key { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public StepState State { get; set; }
        public string Status { get; set; } = string.Empty;
        // What iPro sees for this step's records, in a sentence; SeenRight is true when that is what is expected.
        public string Seen { get; set; } = string.Empty;
        public bool? SeenRight { get; set; }
        // The DNS host's trap for this step, if it has one.
        public string Tip { get; set; } = string.Empty;
        public IReadOnlyList<Record> Records { get; init; } = Array.Empty<Record>();
    }

    // A line of step 4.
    public sealed class Progress
    {
        public string Key { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public StepState State { get; init; }
        public string Detail { get; init; } = string.Empty;
    }

    public sealed class Guide
    {
        public int DomainId { get; init; }
        public string DomainName { get; init; } = string.Empty;
        public string ProvenName { get; init; } = string.Empty;
        public string RootDomain { get; init; } = string.Empty;
        public bool HasShortAddress { get; init; }
        // 589: a name iPro will never connect; the guide is only this sentence.
        public string? Refusal { get; init; }
        // 1 to 4; 0 when live (or refused).
        public int CurrentStep { get; init; }
        public bool Live { get; init; }
        public string Heading { get; init; } = string.Empty;
        public DnsHost Host { get; init; } = DnsHosts.Unknown;
        public string Zone { get; init; } = string.Empty;
        public IReadOnlyList<string> NameServers { get; init; } = Array.Empty<string>();
        public DateTime? CheckedAtUtc { get; init; }
        public bool FromZoneServers { get; init; }
        public IReadOnlyList<Step> Steps { get; init; } = Array.Empty<Step>();
        public IReadOnlyList<Progress> Finish { get; init; } = Array.Empty<Progress>();
        // 506: the name whose CAA records keep the certificate authority out, and what goes in the Name box for it there.
        public string? CaaName { get; init; }
        public string? CaaNameInBox { get; init; }
        public bool CertificateSlow { get; init; }
        // What the domain check found wrong that only the adviser can mend (a name pointing at a private address).
        public string? Problem { get; init; }
        // Where the domain's email goes, and whether pointing the short address at iPro would stop it (review H2).
        public string MailNote { get; init; } = string.Empty;
        public bool MailAtRisk { get; init; }
        // The short address already points at iPro while the email depends on it: the email is not arriving (review L-e).
        public bool MailBroken { get; init; }
        // The look found the short address's own records (any address at all).
        public bool ShortAddressSeen { get; init; }
        // The short address's A and asuid records are both in place (the page may ask iPro to connect it).
        public bool ShortRecordsSeen { get; init; }
        // Set by the page's endpoint: this answer followed a full check of the domain, not only a look at its DNS.
        public bool CheckedNow { get; set; }
    }

    public static Guide Describe(AgentDomain domain, DomainSetupLook? look, Settings settings, DateTime utcNow)
    {
        var www = domain.DomainName;
        var proven = DomainOwnershipProof.ProvenName(domain);
        var root = domain.RootDomain;
        var hasShort = ShortAddressState.HasShortAddress(domain);
        var host = DnsHosts.Recognise(look?.NameServers);
        var printableNames = (look?.NameServers ?? Array.Empty<string>()).Select(Show).ToList();

        // 589's refusal, or a row whose names are not its own (592's check refuses those as well).
        var refusal = DomainCheckService.RefusalFor(domain, settings.Configuration) ??
                      (!DomainOwnershipProof.Covers(domain, domain.RootDomain) || !DomainOwnershipProof.Covers(domain, domain.WwwDomain)
                          ? "This domain's record is not consistent, so it cannot be connected. Remove it and add it again."
                          : null);
        if (refusal != null)
        {
            return new Guide
            {
                DomainId = domain.Id, DomainName = www, ProvenName = proven, RootDomain = root, HasShortAddress = hasShort,
                Refusal = refusal, Heading = "This domain cannot be connected", Host = host,
            };
        }

        // The zone the Name box is relative to: the one the look found, else the 592 rule (a two-part name is a zone).
        var zone = !string.IsNullOrEmpty(look?.Zone) ? look!.Zone : proven.Count(c => c == '.') == 1 ? proven : string.Empty;
        string NameFor(string fullName) => DnsHosts.NameInBox(host, fullName, zone);
        var target = ZoneDns.Normalize(string.IsNullOrWhiteSpace(domain.DnsTarget) ? settings.DnsTarget : domain.DnsTarget);
        var platform = new HashSet<string>(look?.PlatformAddresses ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (settings.WebsiteAddress.Length > 0) platform.Add(settings.WebsiteAddress);

        // ---- step 1: prove it's yours
        var provenAlready = !DomainOwnershipProof.IsNeeded(domain);
        var proofName = DomainOwnershipProof.RecordName(domain);
        bool? proofRight = look?.ProofValues is { } proofValues ? DomainOwnershipProof.Matches(proofValues, domain.VerificationToken) : null;
        var step1Done = provenAlready || proofRight == true;
        var step1 = new Step
        {
            Number = 1, Key = "prove", Title = "Prove it's yours",
            Records = new[] { new Record("proof", "TXT", NameFor(proofName), proofName, domain.VerificationToken) },
            SeenRight = provenAlready ? true : proofRight,
            Seen = provenAlready
                ? "Done: we have seen your record. You can leave it in place."
                : look?.ProofValues is not { } values ? (look == null ? string.Empty : CouldNotCheck)
                : proofRight == true ? "Found it: your record is in place."
                : values.Count == 0 ? "We don't see this record yet. It usually shows up a minute or two after you save it."
                : $"We see a TXT record there, but its value is {Show(values[0])} rather than {domain.VerificationToken}. Edit it so the value is exactly {domain.VerificationToken}.",
            Tip = host.ProofTip,
        };

        // ---- step 2: point www (or the sub-domain itself) at iPro
        var cname = look?.WwwCname is { Length: > 0 } seenCname ? ZoneDns.Normalize(seenCname) : null;
        var wwwAddresses = look?.WwwAddresses;
        var bound = domain.AzureBindingStatus == AgentDomainStatus.Bound;
        var secured = bound && domain.SslStatus is AgentDomainStatus.Bound or "SslBound";
        // A name that is its zone's own apex (firm.qc.ca added as itself) cannot hold a CNAME at all.
        var apex = look != null && !string.IsNullOrEmpty(look.Zone) && ZoneDns.Normalize(www) == ZoneDns.Normalize(look.Zone) &&
                   !www.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        // An A record (or a CNAME the DNS host flattens) at the platform's own address, on a row already connected and
        // secured, is working; only the CNAME connects a new one.
        var atPlatform = wwwAddresses is { Count: > 0 } && wwwAddresses.All(platform.Contains);
        // Through the public resolvers (the zone's own servers could not be asked) a missing CNAME proves nothing: they
        // remember an answer from before it was added (review M-B). There the platform's address could be the CNAME or an
        // A record Azure will not connect, so it is said as it is and the step follows what iPro recorded (third
        // review, L-4).
        var reachesPlatform = look != null && !look.FromZoneServers && !secured && atPlatform && cname == null && !apex;
        bool? wwwRight = look == null ? null
            : apex ? false
            : cname != null ? cname == target
            : wwwAddresses == null ? null
            : reachesPlatform ? null
            : atPlatform && secured;
        var storedDnsReady = domain.DnsStatus is AgentDomainStatus.DnsReady or AgentDomainStatus.Bound;
        var step2Done = wwwRight ?? storedDnsReady;
        string wwwSeen;
        if (look == null) wwwSeen = string.Empty;
        else if (apex)
            wwwSeen = $"{www} is the domain itself, and the domain itself cannot point at a website with a CNAME. Remove it from your list and add www.{www} instead.";
        else if (wwwRight == true && cname == null)
            wwwSeen = $"Found: {www} points to iPro.";
        else if (reachesPlatform)
            wwwSeen = $"{www} reaches iPro's address. iPro connects it once it sees the CNAME above; if you used an A record instead, replace it with that CNAME.";
        else if (cname != null && cname == target)
            wwwSeen = step1Done ? $"Found: {www} points to iPro." : $"{www} already points to iPro, so this step is done.";
        else if (cname != null)
            wwwSeen = $"Right now {www} points to {Show(cname)}. Edit that record and change its value to {target}.";
        else if (wwwAddresses == null) wwwSeen = CouldNotCheck;
        else if (wwwAddresses.Count == 0) wwwSeen = $"We don't see a record for {www} yet.";
        else if (host.Key == "cloudflare" && wwwAddresses.All(IsCloudflareAddress))
            wwwSeen = $"Cloudflare's proxy is on for {www} (the orange cloud), so we see Cloudflare's own addresses. Edit the record: value {target}, Proxy status DNS only (the grey cloud).";
        else if (!look.FromZoneServers)
            wwwSeen = $"Right now {www} goes to {List(wwwAddresses)}, not to iPro. Its record should be the CNAME above, with the value {target}.";
        else
            wwwSeen = $"Right now {www} has an A record ({List(wwwAddresses)}) instead. Delete it, then add the CNAME above.";
        var step2 = new Step
        {
            Number = 2, Key = "www",
            Title = www.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "Point www at iPro" : $"Point {www} at iPro",
            Records = new[] { new Record("www", "CNAME", NameFor(www), www, target) },
            SeenRight = wwwRight, Seen = wwwSeen, Tip = host.WwwTip,
        };

        // ---- step 3: the short address (553), optional
        var stored = ShortAddressState.Describe(domain);
        var forwardingWorks = hasShort && stored.Working && !stored.Direct;
        var directWorks = hasShort && stored.Working && stored.Direct;
        var rootAddresses = look?.RootAddresses;
        var ours = rootAddresses?.Where(a => platform.Contains(a)).ToList() ?? new List<string>();
        var others = rootAddresses?.Where(a => !platform.Contains(a)).ToList() ?? new List<string>();
        var rootRight = rootAddresses is { Count: > 0 } && others.Count == 0;
        bool? asuidRight = look?.AsuidValues is { } asuidValues ? DomainOwnershipProof.Matches(asuidValues, settings.DomainVerificationId) : null;
        // A null MX ("MX 0 .", RFC 7505) names no server: the domain takes no email.
        var mailServers = look?.MailServers?.Select(ZoneDns.Normalize).Where(server => server.Length > 0).ToList() ?? new List<string>();
        // Already broken once the short address points here, secured or not: what iPro recorded, or what DNS shows now
        // (third review, L-2).
        var mailBroken = hasShort && look?.MailOnShortAddress == true && (ShortAddressState.PointsAtPlatform(domain) || rootRight);
        var mailAtRisk = hasShort && look?.MailOnShortAddress == true && !mailBroken;
        var mailServer = mailServers.Count > 0 ? $" (its mail server is {Show(mailServers[0])})" : string.Empty;
        var mailNote = !hasShort ? string.Empty
            : look == null ? "Checking where your email goes."
            : look.MailOnShortAddress is not { } mailDepends ? MailUnknown
            : mailBroken
                ? $"Your email may not be arriving: it uses {root}{mailServer}, which now points at iPro. Click I need help and we will sort it out with you."
            : mailDepends
                ? $"Careful: your email uses {root}{mailServer}. Pointing {root} at iPro would stop your email. Use your registrar's forwarding instead (below), or click I need help."
            : mailServers.Count > 0
                ? $"Your email is not affected: it goes to {List(mailServers)}, which these records do not touch."
            : look.MailServers is { Count: > 0 }
                ? "Your email is not affected: this domain receives no email."
                : "Your email is not affected: this domain has no email set up.";
        var step3Done = hasShort && (directWorks || forwardingWorks || (settings.CanPointShortAddress && rootRight && asuidRight == true));
        var step3 = new Step
        {
            Number = 3, Key = "short", Title = "The short address (optional, recommended)",
            Records = hasShort && settings.CanPointShortAddress
                ? new[]
                {
                    new Record("root", "A", NameFor(root), root, settings.WebsiteAddress),
                    new Record("asuid", "TXT", NameFor("asuid." + root), "asuid." + root, settings.DomainVerificationId),
                }
                : Array.Empty<Record>(),
            SeenRight = !hasShort ? null : step3Done ? true : look == null || rootAddresses == null ? null : false,
            Tip = host.ShortTip,
        };
        if (!hasShort) step3.Seen = string.Empty;
        else if (directWorks) step3.Seen = $"Done: {root} is connected and secured.";
        else if (forwardingWorks)
            step3.Seen = $"{root} forwards to {www}. That works for your home page. To keep every page address working (a website you are moving here), point it straight at iPro with the two records below instead.";
        else if (look != null)
            step3.Seen = string.Join(" ", new[] { RootSeen(), AsuidSeen() }.Where(s => s.Length > 0));

        string RootSeen()
        {
            if (rootAddresses == null) return CouldNotCheck;
            if (rootAddresses.Count == 0) return $"We don't see an A record for {root} yet.";
            if (others.Count == 0) return $"Found: {root} points to iPro.";
            if (mailAtRisk) return $"Right now {root} points to {List(others)}, where your email is as well. Leave that record as it is.";
            if (!settings.CanPointShortAddress) return $"Right now {root} points to {List(others)}. Forward it to {www} at your registrar (below).";
            // 595 (www.411trades.com, 2026-10-22): a host's own forwarding, beside ours or alone. GoDaddy locks its records on
            // the DNS screen; they go only with the forwarding, so say where that is.
            if (DnsHosts.ForwardingOf(others) is { } forwarding)
            {
                var remove = forwarding.HowToRemove(root);
                if (ours.Count > 0) return remove;
                var choice = forwarding.IsForwarding
                    ? $" Or keep the forwarding: {root} then goes to {www}, but only its home page, so we recommend the A record."
                    : string.Empty;
                return $"{remove} Then add an A record for @ with the value {settings.WebsiteAddress}.{choice}";
            }
            if (ours.Count > 0) return $"{root} points to iPro but also to {List(others)}. Delete the other A (and AAAA) records so that ours is the only one.";
            return $"Right now {root} points to {List(others)}. Change that A record to {settings.WebsiteAddress}.";
        }

        string AsuidSeen()
        {
            if (!settings.CanPointShortAddress || look?.AsuidValues is not { } asuid) return string.Empty;
            if (asuidRight == true) return "The asuid record is in place.";
            if (asuid.Count == 0) return "We don't see the asuid record yet.";
            return $"We see an asuid record, but its value is {Show(asuid[0])}. Edit it so the value is exactly the one above.";
        }

        // ---- step 4: iPro finishes
        var live = provenAlready && secured && wwwRight != false;
        var slow = bound && !secured && utcNow - CertificateClock.StartOf(domain) > CertificateSlowAfter;
        var finish = new List<Progress>
        {
            new()
            {
                Key = "connected", Label = "Connected to your website",
                State = bound ? StepState.Done : step1Done && step2Done ? StepState.Current : StepState.Upcoming,
                Detail = bound ? string.Empty
                    : domain.AzureBindingStatus == AgentDomainStatus.Failed && !string.IsNullOrWhiteSpace(domain.LastError) ? DomainErrorTranslator.ToAgentMessage(domain.LastError)
                    : step1Done && step2Done ? "Connecting. This usually takes a minute or two once both records are seen."
                    : string.Empty,
            },
            new()
            {
                Key = "padlock", Label = "Padlock (security certificate)",
                State = secured ? StepState.Done : bound ? StepState.Current : StepState.Upcoming,
                Detail = secured ? "In place. It renews itself."
                    : !bound ? string.Empty
                    : slow && domain.CertificateAlertSentAt != null ? "Taking longer than usual. We have been alerted and are looking into it; you don't need to contact us."
                    : slow ? "Taking longer than usual. We keep trying on our own, and we are alerted if it does not arrive."
                    : "Ordered. It usually arrives within a few minutes, and it renews itself.",
            },
        };
        if (hasShort && (stored.Direct || (settings.CanPointShortAddress && rootRight && asuidRight == true)))
        {
            finish.Add(new Progress
            {
                Key = "short", Label = $"{root} connected and secured",
                State = directWorks ? StepState.Done : StepState.Current,
                Detail = directWorks ? string.Empty : !string.IsNullOrWhiteSpace(domain.RootLastError) ? domain.RootLastError : $"Connecting {root}. This usually takes a few minutes.",
            });
        }
        finish.Add(new Progress
        {
            Key = "live", Label = $"Live at https://{www}", State = live ? StepState.Done : StepState.Upcoming,
            Detail = live ? string.Empty : "We will email you when it is.",
        });

        var caaName = !live && !string.IsNullOrEmpty(domain.CaaBlockingName) ? domain.CaaBlockingName : null;
        // A padlock held up (a CAA record missing, or hours late) comes before the optional short address (review M2).
        var finishFirst = !secured && (caaName != null || slow);
        var current = !step1Done ? 1 : !step2Done ? 2 : live ? 0 : finishFirst ? 4 : hasShort && !step3Done ? 3 : 4;
        var step4 = new Step
        {
            Number = 4, Key = "finish", Title = "iPro finishes",
            SeenRight = live ? true : null,
            Seen = live ? $"Done: https://{www} is live." : string.Empty,
        };

        var steps = new[] { step1, step2, step3, step4 };
        foreach (var step in steps)
        {
            var done = step.Number switch { 1 => step1Done, 2 => step2Done, 3 => step3Done, _ => live };
            step.State = step.Number == 3 && !hasShort ? StepState.NotNeeded
                : done ? StepState.Done
                : step.Number == current ? StepState.Current
                : StepState.Upcoming;
        }
        step1.Status = StatusOf(step1.State, "To do now", "Not started");
        step2.Status = StatusOf(step2.State, "To do now", "Not started");
        step3.Status = step3.State switch
        {
            StepState.NotNeeded => "Not needed",
            StepState.Done => forwardingWorks ? "Done (forwarding)" : "Done",
            StepState.Current => mailAtRisk ? "Use forwarding" : "Recommended",
            _ => mailAtRisk ? "Use forwarding" : "Optional",
        };
        step4.Status = live ? "Done" : !step1Done || !step2Done ? "Not started" : !bound ? "Connecting" : !secured ? "Padlock ordered" : "Finishing";

        return new Guide
        {
            DomainId = domain.Id, DomainName = www, ProvenName = proven, RootDomain = root, HasShortAddress = hasShort,
            CurrentStep = current, Live = live,
            Heading = live ? $"{www} is live" : $"Step {current} of {StepCount}: {steps[current - 1].Title}",
            Host = host, Zone = zone, NameServers = printableNames, CheckedAtUtc = look?.CheckedAtUtc, FromZoneServers = look?.FromZoneServers == true,
            Steps = steps, Finish = finish,
            CaaName = caaName,
            CaaNameInBox = caaName == null ? null : NameFor(caaName),
            CertificateSlow = slow,
            Problem = domain.DnsStatus == AgentDomainStatus.Failed && !string.IsNullOrWhiteSpace(domain.LastError) ? DomainErrorTranslator.ToAgentMessage(domain.LastError) : null,
            MailNote = mailNote,
            MailAtRisk = mailAtRisk,
            MailBroken = mailBroken,
            ShortAddressSeen = rootAddresses is { Count: > 0 },
            ShortRecordsSeen = hasShort && settings.CanPointShortAddress && rootRight && asuidRight == true,
        };
    }

    private static string StatusOf(StepState state, string current, string upcoming) => state switch
    {
        StepState.Done => "Done",
        StepState.Current => current,
        StepState.NotNeeded => "Not needed",
        _ => upcoming,
    };

    // Cloudflare's own addresses (its proxy), from www.cloudflare.com/ips-v4: an orange-cloud record answers with these.
    private static readonly (uint Network, int Bits)[] CloudflareRanges =
        new[] { "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18", "108.162.192.0/18",
                "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
                "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22" }
        .Select(range => (ToUInt(range.Split('/')[0]), int.Parse(range.Split('/')[1], CultureInfo.InvariantCulture))).ToArray();

    private static uint ToUInt(string address) =>
        System.Net.IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes())
            : 0;

    internal static bool IsCloudflareAddress(string address)
    {
        var value = ToUInt(address);
        return value != 0 && CloudflareRanges.Any(r => (value >> (32 - r.Bits)) == (r.Network >> (32 - r.Bits)));
    }

    // A value from DNS, as text: printable characters only, at most 80 of them.
    internal static string Show(string? value)
    {
        var text = PlatformHostNames.Printable(value);
        return text.Length <= 80 ? text : text[..77] + "...";
    }

    private static string List(IEnumerable<string> values)
    {
        var shown = values.Take(4).Select(Show).ToList();
        return string.Join(", ", shown) + (values.Count() > 4 ? ", ..." : string.Empty);
    }

    // Whether the page should ask iPro to check the domain now. The check asks Azure, so this is what keeps a page that
    // polls every 20 seconds from hammering it: DNS shows something the row has not caught up with, or the adviser pressed
    // Check now, and the last check was long enough ago.
    //   - the proof seen while the row is unproven, or www pointing here while it is not connected: after a minute;
    //   - a binding Azure refused, the short address (its two records in place, or for a live domain any address while
    //     it is not working: the registrar's forwarding may just have been switched on), a CAA record to look at again:
    //     every five minutes, as the job would;
    //   - a padlock on its way: every two minutes in its first three hours, counted from the connection
    //     (CertificateClock); after that the job and its alert carry it;
    //   - Check now: 15 seconds after the last check (the job's included), whatever the row's state (reviews M-A, L-5).
    // The page's checks never spend the job's retry budget (WebsiteController.DomainGuide puts it back), so closing the
    // page leaves the domain to the job as before.
    public static bool ShouldCheckNow(AgentDomain domain, Guide guide, DateTime utcNow, bool asked = false)
    {
        if (guide.Refusal != null || guide.Steps.Count < StepCount) return false;
        var since = domain.LastCheckedAt is { } last ? utcNow - last : TimeSpan.MaxValue;
        if (asked) return since >= AskedCheckSpacing;
        if (since < CheckSpacing) return false;

        if (DomainOwnershipProof.IsNeeded(domain)) return guide.Steps[0].SeenRight == true;

        var everyFive = since >= RefusedCheckSpacing;
        // The short address: its two records in place and not connected yet, or a live domain's short address not working
        // with addresses seen (the registrar's forwarding may just have been switched on).
        var rootToConnect = guide.ShortRecordsSeen && domain.RootAzureBindingStatus != AgentDomainStatus.Bound;
        var liveShortUnset = guide.Live && guide.HasShortAddress && guide.ShortAddressSeen && guide.Steps[2].State != StepState.Done;
        if ((rootToConnect || liveShortUnset) && everyFive) return true;
        if (guide.Live) return false;

        var bound = domain.AzureBindingStatus == AgentDomainStatus.Bound;
        if (guide.Steps[1].SeenRight == true && !bound)
            return domain.AzureBindingStatus != AgentDomainStatus.Failed || everyFive;
        if (guide.CaaName != null) return everyFive;
        var padlockOrdered = bound && domain.SslStatus != AgentDomainStatus.Bound;
        return padlockOrdered && since >= CertificateCheckSpacing && utcNow - CertificateClock.StartOf(domain) < CertificateSlowAfter;
    }

    // The text of an "I need help" ticket: where the adviser is, the DNS host, and for every record what iPro expects
    // beside what it sees.
    public static string SupportMessage(Guide guide)
    {
        var lines = new List<string>
        {
            $"Domain: {guide.DomainName}",
            $"Where I am: {guide.Heading}",
        };
        if (guide.Refusal != null) lines.Add(guide.Refusal);
        if (guide.Problem != null) lines.Add("Problem: " + guide.Problem);
        if (guide.MailNote.Length > 0) lines.Add("Email: " + guide.MailNote);
        if (guide.Host.Name.Length > 0)
            lines.Add($"DNS managed at: {guide.Host.Name}" + (guide.NameServers.Count > 0 ? $" ({string.Join(", ", guide.NameServers)})" : string.Empty));
        if (guide.CheckedAtUtc is { } checkedAt)
            lines.Add($"What iPro sees (checked {checkedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC, " +
                      (guide.FromZoneServers ? "from the domain's own name servers):" : "from public DNS):"));
        foreach (var step in guide.Steps)
        {
            lines.Add(string.Empty);
            lines.Add($"Step {step.Number}, {step.Title}: {step.Status}");
            foreach (var record in step.Records)
                lines.Add($"  Expected: {record.Type} {record.FullName} = {record.Value}  (Name box: {(record.Name.Length == 0 ? "left empty" : record.Name)})");
            if (step.Seen.Length > 0) lines.Add("  Seen: " + step.Seen);
            if (step.Number == StepCount)
                foreach (var line in guide.Finish)
                    lines.Add($"  {line.Label}: {(line.State == StepState.Done ? "done" : line.State == StepState.Current ? "in progress" : "not yet")}" +
                              (line.Detail.Length > 0 ? " - " + line.Detail : string.Empty));
        }
        if (guide.CaaName != null) lines.Add($"CAA records at {guide.CaaName} do not allow DigiCert ({CaaCheck.RecordToAdd} is missing).");
        return string.Join("\n", lines);
    }

    // What is still missing, one sentence per thing, for the "not connected yet" email a day after the domain was added.
    public static IReadOnlyList<string> StillToDo(Guide guide)
    {
        var missing = new List<string>();
        if (guide.Refusal != null) return new[] { guide.Refusal };
        if (guide.Problem != null) missing.Add(guide.Problem);
        foreach (var step in guide.Steps.Where(s => s.Number <= 2 && s.State != StepState.Done))
        {
            var record = step.Records[0];
            missing.Add($"Step {step.Number}, {step.Title}: a {record.Type} record named {(record.Name.Length == 0 ? record.FullName : record.Name)} " +
                        $"({record.FullName}) with the value {record.Value}." + (step.Seen.Length > 0 ? " " + step.Seen : string.Empty));
        }
        foreach (var line in guide.Finish.Where(p => p.State == StepState.Current && p.Detail.Length > 0 && p.Key != "live"))
            missing.Add($"{line.Label}: {line.Detail}");
        if (guide.CaaName != null)
            missing.Add($"A CAA record at {guide.CaaName}: {CaaCheck.RecordToAdd} (your DNS lists which companies may issue its padlock, and ours is not on it).");
        return missing;
    }
}
