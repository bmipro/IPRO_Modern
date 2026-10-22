namespace IPRO.Utility;

// 595: where a domain's DNS is managed, told from its name servers, and the words that fit that company's screens:
// where to click, what goes in the Name box, and the one or two things that trip people up there. Checked against
// each company's own help pages on 2026-10-21 (GoDaddy's pages refuse automated reads; its wording is from its help
// pages as search results show them and from Microsoft Learn's GoDaddy guide). An unknown company gets the wording
// that fits every screen.
//
//   Key       -- stable, for the page and the tests
//   Name      -- shown: "Your domain's DNS is managed at {Name}."
//   Where     -- the click path to the screen where records are added
//   HelpUrl   -- the company's own help page about DNS records
//   NameNote  -- what goes in the Name (Host) box
//   ProofTip / WwwTip / ShortTip -- the step's company-specific trap, if any
//   RootName  -- what the Name box takes for the domain itself ("@", or empty where the box is left blank)
//   FullNames -- the Name box wants the whole name with a final dot (cPanel's Zone Editor)
public sealed record DnsHost(string Key, string Name, string Where, string HelpUrl, string NameNote,
    string ProofTip = "", string WwwTip = "", string ShortTip = "", string RootName = "@", bool FullNames = false);

public static class DnsHosts
{
    private const string ShortNameNote = "In the Name box type only the part shown: the screen adds your domain to it by itself.";

    // Before the name servers have been looked up.
    public static DnsHost Unknown { get; } = new("unknown", string.Empty,
        "Sign in where you bought the domain (or wherever its DNS is managed) and open its DNS settings. They are often called DNS, DNS Records, DNS Management, Zone Editor or Advanced DNS.",
        string.Empty,
        "In the Name box type only the part shown: most screens add your domain to it by themselves. If yours wants the full name, type the full name instead.");

    private static readonly DnsHost GoDaddy = new("godaddy", "GoDaddy",
        "Sign in at godaddy.com, open My Products, then Domains. Beside your domain choose Edit DNS (or Domain Settings, then the DNS tab), and click Add New Record.",
        "https://www.godaddy.com/help/manage-dns-records-680", ShortNameNote,
        WwwTip: "GoDaddy already has a www record (a CNAME pointing to @). Click Edit on it and change its value; GoDaddy will not take a second www record.",
        ShortTip: "If GoDaddy's forwarding is on for this domain, it locks the @ record: delete the forwarding first (DNS, then Forwarding), then edit the A record for @. GoDaddy may text a code to the account holder to delete forwarding.");

    private static readonly DnsHost Namecheap = new("namecheap", "Namecheap",
        "Sign in at namecheap.com, open Domain List, click Manage beside your domain, then the Advanced DNS tab. Under Host Records click Add New Record, and Save all changes when done.",
        "https://www.namecheap.com/support/knowledgebase/article.aspx/434/2237/how-do-i-set-up-host-records-for-a-domain/",
        "In the Host box type only the part shown; never your domain itself.",
        WwwTip: "A new Namecheap domain has a parking record: a CNAME for www pointing to parkingpage.namecheap.com. Edit it rather than adding a second one.",
        ShortTip: "Delete Namecheap's URL Redirect record for @ if there is one: it hides an extra A record that is not listed, and ours must be the only one.");

    private static readonly DnsHost Cloudflare = new("cloudflare", "Cloudflare",
        "Sign in at dash.cloudflare.com, choose your domain, then DNS, then Records, and click Add record.",
        "https://developers.cloudflare.com/dns/manage-dns-records/how-to/create-dns-records/", ShortNameNote,
        WwwTip: "Set Proxy status to DNS only (the grey cloud). With the orange cloud on, the padlock can never be issued.",
        ShortTip: "Set Proxy status to DNS only (the grey cloud) on the A record too.");

    private static readonly DnsHost Squarespace = new("squarespace", "Squarespace (formerly Google Domains)",
        "Sign in at account.squarespace.com/domains, choose your domain, then DNS, then DNS Settings. Under Custom records click Add record (it may ask for your password again). (These name servers are Google Cloud DNS's too: if your DNS is there, open Cloud DNS, choose the zone and Add standard.)",
        "https://support.squarespace.com/hc/en-us/articles/215744668-Pointing-a-Squarespace-domain", ShortNameNote,
        WwwTip: "If the Squarespace Defaults records are listed, delete them with the trash can first: while they are there the domain cannot point anywhere else. Keep your email records.",
        ShortTip: "The Squarespace Defaults hold four A records for @; they must be gone so that ours is the only one.");

    private static readonly DnsHost Wix = new("wix", "Wix",
        "Sign in at wix.com, open Domains, click Domain Actions beside your domain, then Manage DNS Records. Use + Add Record in the A, CNAME or TXT section, then Save Changes.",
        "https://support.wix.com/en/article/connecting-a-wix-domain-to-an-external-site",
        "In the Host Name box type only the part shown, and leave it empty for the domain itself.",
        WwwTip: "If Wix will not let you change www, the domain is still connected to a Wix site: disconnect it from that site first.",
        ShortTip: "Delete Wix's own A records for the domain once ours is added.", RootName: "");

    private static readonly DnsHost Ionos = new("ionos", "IONOS",
        "Sign in at ionos.ca, open Domains & SSL, click the gear (or the three dots) beside your domain, choose DNS, then Add record.",
        "https://www.ionos.ca/help/domains/configuring-your-ip-address/changing-a-domains-ipv4/ipv6-address-a/aaaa-record/", ShortNameNote,
        ShortTip: "IONOS points @ at its own servers with an A and an AAAA record: change the A record to ours and delete the AAAA record.");

    private static readonly DnsHost Hover = new("hover", "Hover",
        "Sign in at hover.com, choose your domain, open the DNS tab and click Add a record.",
        "https://support.hover.com/support/solutions/articles/201000064728-managing-dns-records-at-hover", ShortNameNote,
        ShortTip: "If Hover forwards this domain, its A records for @ and * point to 216.40.34.41: remove the forward, then edit the @ record.");

    private static readonly DnsHost OpenSrs = new("opensrs", "Tucows (OpenSRS), through the company you pay for the domain",
        "Sign in where you pay for the domain (or at manage.opensrs.net with the login your provider gave you), open DNS (Nameservers/DNS, then Modify DNS Zone) and add the record.",
        "https://support.opensrs.com/support/solutions/articles/201000063431",
        "In the Subdomain box type only the part shown; leave it empty for the domain itself.", RootName: "");

    private static readonly DnsHost SiteGround = new("siteground", "SiteGround",
        "Sign in to SiteGround's Client Area, open Services, then Domains, click Settings beside your domain and choose DNS Zone Editor (or in Site Tools: Domain, then DNS Zone Editor). Choose the record type's tab under Create New Record and click Create.",
        "https://www.siteground.com/kb/manage-dns-records/",
        "In the Name box type only the part shown; leave it empty for the domain itself.", RootName: "");

    private static readonly DnsHost Bluehost = new("bluehost", "Bluehost",
        "Sign in to your Bluehost account, open Domains, then Advanced Tools, and click Manage beside Advanced DNS Records (or in cPanel: Domains, then Zone Editor).",
        "https://www.bluehost.com/help/article/dns-management-add-edit-or-delete-dns-entries", ShortNameNote,
        ShortTip: "Bluehost points @ at your hosting: edit that A record rather than adding a second. Read the email line above first: if your email is at Bluehost, use forwarding instead.");

    // Name servers of web hosts whose DNS is edited in cPanel's Zone Editor (HostPapa, Web Hosting Canada, HostGator,
    // A2 Hosting / hosting.com, InMotion, Namecheap's shared hosting).
    private static readonly (string Suffix, string Company)[] CpanelHosts =
    {
        ("hostpapa.com", "HostPapa"), ("whc.ca", "Web Hosting Canada"), ("web-dns1.com", "Web Hosting Canada"), ("wpdns.ca", "Web Hosting Canada"),
        ("hostgator.com", "HostGator"), ("websitewelcome.com", "HostGator"), ("stableserver.net", "hosting.com (A2 Hosting)"),
        ("a2hosting.com", "hosting.com (A2 Hosting)"), ("inmotionhosting.com", "InMotion Hosting"), ("servconfig.com", "InMotion Hosting"),
        ("namecheaphosting.com", "Namecheap Hosting"),
    };

    private static DnsHost Cpanel(string company) => new("cpanel", company + " (cPanel)",
        "Sign in to cPanel at " + company + ", open Domains, then Zone Editor, click Manage beside your domain, then Add Record (or Edit on a record that is already there), then Save Record.",
        "https://docs.cpanel.net/cpanel/domains/zone-editor/",
        "In the Name box type the full name shown, ending with a dot: cPanel would otherwise add your domain to it a second time.",
        WwwTip: "cPanel usually has a www record already (a CNAME to your domain): click Edit on it and change its value.",
        ShortTip: "cPanel points your domain at your web host with an A record: edit that one. Read the email line above first: if your email is on that host, use forwarding instead.",
        FullNames: true);

    public static DnsHost Recognise(IEnumerable<string>? nameServers)
    {
        var servers = (nameServers ?? Enumerable.Empty<string>())
            .Select(ZoneDns.Normalize).Where(s => s.Length > 0).ToList();
        if (servers.Count == 0) return Unknown;

        foreach (var server in servers)
        {
            if (Ends(server, "domaincontrol.com") || Ends(server, "secureserver.net")) return GoDaddy;
            if (Ends(server, "registrar-servers.com")) return Namecheap;
            if (Ends(server, "ns.cloudflare.com") || Ends(server, "foundationdns.com") || Ends(server, "foundationdns.net") || Ends(server, "foundationdns.org")) return Cloudflare;
            if (Ends(server, "squarespacedns.com") || Ends(server, "googledomains.com")) return Squarespace;
            if (Ends(server, "wixdns.net")) return Wix;
            if (Ends(server, "ui-dns.com") || Ends(server, "ui-dns.de") || Ends(server, "ui-dns.org") || Ends(server, "ui-dns.biz")) return Ionos;
            if (Ends(server, "hover.com")) return Hover;
            if (Ends(server, "systemdns.com")) return OpenSrs;
            if (Ends(server, "siteground.net")) return SiteGround;
            if (Ends(server, "bluehost.com")) return Bluehost;
            foreach (var (suffix, company) in CpanelHosts)
                if (Ends(server, suffix)) return Cpanel(company);
        }

        // Not one we know: named by its first name server, so the adviser can recognise the company.
        return Unknown with { Key = "generic", Name = "the company that runs " + PlatformHostNames.Printable(servers[0]) };
    }

    // 595: addresses that are a DNS host's own forwarding or default records, not a website. GoDaddy's forwarding answers
    // on two addresses (measured on drhug.ca, ouritems.ca, 4ipro.com and 411trades.com) and GoDaddy locks their A records
    // on the DNS screen ("records applied by a product or service"); Hover's forwarding answers on one (Hover's help);
    // Squarespace's default records point at four (Squarespace's help). Every address must be one of the company's.
    public sealed record Forwarder(string Company, bool IsForwarding, Func<string, string> HowToRemove);

    private static readonly Forwarder GoDaddyForwarding = new("GoDaddy", true, root =>
        $"GoDaddy's domain forwarding is on: open the Forwarding tab for {root} and delete the forwarding; its two A records go away by themselves.");
    private static readonly Forwarder HoverForwarding = new("Hover", true, root =>
        $"Hover's forwarding is on for {root}: remove the forward at Hover; its A records for @ and * go away with it.");
    private static readonly Forwarder SquarespaceDefaults = new("Squarespace", false, root =>
        $"Squarespace's default records are still on {root}: in DNS Settings, delete the Squarespace Defaults with the trash can.");

    private static readonly Dictionary<string, Forwarder> ForwardingAddresses = new(StringComparer.Ordinal)
    {
        ["15.197.225.128"] = GoDaddyForwarding, ["3.33.251.168"] = GoDaddyForwarding,
        ["216.40.34.41"] = HoverForwarding,
        ["198.185.159.144"] = SquarespaceDefaults, ["198.185.159.145"] = SquarespaceDefaults,
        ["198.49.23.144"] = SquarespaceDefaults, ["198.49.23.145"] = SquarespaceDefaults,
    };

    public static Forwarder? ForwardingOf(IReadOnlyCollection<string> addresses) =>
        addresses.Count > 0 && addresses.All(ForwardingAddresses.ContainsKey) && addresses.Select(a => ForwardingAddresses[a]).Distinct().Count() == 1
            ? ForwardingAddresses[addresses.First()]
            : null;

    // What to type in the Name box for a record named `fullName` in `zone`.
    public static string NameInBox(DnsHost host, string fullName, string? zone)
    {
        var name = ZoneDns.Normalize(fullName);
        var apex = ZoneDns.Normalize(zone);
        if (host.FullNames) return name + ".";
        if (apex.Length == 0) return name;
        if (name == apex) return host.RootName;
        return name.EndsWith("." + apex, StringComparison.Ordinal) ? name[..^(apex.Length + 1)] : name;
    }

    private static bool Ends(string server, string suffix) =>
        server == suffix || server.EndsWith("." + suffix, StringComparison.Ordinal);
}
