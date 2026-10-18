using Microsoft.Extensions.Configuration;

namespace IPRO.Utility;

// 585 (the owner's decision of 2026-10-17, "Builder + staging together"): a staging copy of iPro, where
// a release is checked before it reaches production. It runs the same code, so what keeps it from
// touching a real person is configuration, and this guard holds that configuration to the rule:
//
//   App:Environment         "Staging" makes an app a staging copy (anything else, or nothing, is not)
//   Email:OnlyTo            required: the only addresses (or "@domain" endings) staging may email;
//                           every other email is written to the log and not sent (RestrictedEmail).
//                           An ending of a public mail service (@gmail.com and the like) is refused:
//                           it would let staging email everyone who has an address there
//   PayPal:IsSandbox        required true: staging never takes real money
//   AzureDomainAutomation:Enabled  must not be true: staging never touches customers' domains
//   Application Insights    none of its settings (connection string or instrumentation key, in any of
//                           the names the SDK reads): staging's traffic is not production's telemetry
//   its own addresses       App:BaseUrl (or App:PortalBaseUrl) and Signups:AdminBaseUrl must be set and
//                           must not be production's, so no link staging writes leads into production
//   its own data            the database and storage named in its settings must not be production's
//
// A staging copy that breaks a rule does not start (Program.cs of both apps), and says which rule.
// (Which app has App:Environment set at all is checked from outside: ops\Check-AppSettings.ps1 -Expect.)
public static class StagingGuard
{
    public const string EnvironmentSetting = "App:Environment";

    public static bool IsStaging(IConfiguration configuration) =>
        string.Equals(configuration[EnvironmentSetting]?.Trim(), "Staging", StringComparison.OrdinalIgnoreCase);

    // What is wrong with this staging copy's settings, in plain sentences (empty: nothing, or not staging).
    public static IReadOnlyList<string> Problems(IConfiguration configuration)
    {
        if (!IsStaging(configuration)) return Array.Empty<string>();
        var problems = new List<string>();
        if (OnlyTo(configuration).Count == 0)
            problems.Add("Email:OnlyTo is empty: staging must name the only addresses it may email (for example test@iproadvisers.com).");
        if (!bool.TryParse(configuration["PayPal:IsSandbox"], out var sandbox) || !sandbox)
            problems.Add("PayPal:IsSandbox is not true: staging must use PayPal's sandbox only.");
        if (bool.TryParse(configuration["AzureDomainAutomation:Enabled"], out var domains) && domains)
            problems.Add("AzureDomainAutomation:Enabled is true: staging must not change customers' domains.");
        foreach (var name in TelemetrySettings)
        {
            if (!string.IsNullOrWhiteSpace(configuration[name]))
                problems.Add($"{name} is set: staging must not report to production's Application Insights.");
        }
        foreach (var wide in OnlyTo(configuration).Where(entry => entry.StartsWith('@') && PublicMailDomains.Contains(entry[1..])))
            problems.Add($"Email:OnlyTo has {wide}: that would let staging email everyone with an address there. Name whole addresses instead.");

        var portal = configuration["App:BaseUrl"];
        if (string.IsNullOrWhiteSpace(portal)) portal = configuration["App:PortalBaseUrl"];
        if (!Uri.TryCreate(portal?.Trim(), UriKind.Absolute, out var portalUri))
            problems.Add("App:BaseUrl is not set: staging's links would lead to production's portal. Set it to staging's own address.");
        else if (IsProductionHost(portalUri.Host))
            problems.Add($"App:BaseUrl is {portalUri.Host}, production's: set it to staging's own address.");
        if (!Uri.TryCreate(configuration["Signups:AdminBaseUrl"]?.Trim(), UriKind.Absolute, out var adminUri))
            problems.Add("Signups:AdminBaseUrl is not set: staging's notices would link to production's SuperAdmin. Set it to staging's SuperAdmin address.");
        else if (IsProductionHost(adminUri.Host))
            problems.Add($"Signups:AdminBaseUrl is {adminUri.Host}, production's: set it to staging's SuperAdmin address.");

        var database = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
        if (ProductionDataNames.Any(name => database.Contains(name, StringComparison.OrdinalIgnoreCase)))
            problems.Add("ConnectionStrings:DefaultConnection names production's database server: staging has its own.");
        foreach (var setting in new[] { "Azure:StorageAccountName", "Azure:StorageConnectionString" })
        {
            if (ProductionDataNames.Any(name => (configuration[setting] ?? string.Empty).Contains(name, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"{setting} names production's storage: staging has its own.");
        }
        return problems;
    }

    // Every name the Application Insights SDK takes a connection from.
    public static readonly IReadOnlyList<string> TelemetrySettings = new[]
    {
        "APPLICATIONINSIGHTS_CONNECTION_STRING", "ApplicationInsights:ConnectionString",
        "APPINSIGHTS_INSTRUMENTATIONKEY", "ApplicationInsights:InstrumentationKey"
    };

    // Mail services anyone can have an address at.
    private static readonly HashSet<string> PublicMailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "hotmail.ca", "live.com", "live.ca", "msn.com",
        "yahoo.com", "yahoo.ca", "ymail.com", "icloud.com", "me.com", "mac.com", "aol.com", "proton.me", "protonmail.com",
        "gmx.com", "gmx.net", "mail.com", "zoho.com", "yandex.com", "shaw.ca", "telus.net", "rogers.com", "sympatico.ca", "bell.net"
    };

    // Production's own names (DOCS/18_AZURE_INFRASTRUCTURE.md): its web addresses, its database server and storage.
    private static readonly string[] ProductionHosts =
    {
        "iproadvisers.com", "www.iproadvisers.com", "app.iproadvisers.com", "admin.iproadvisers.com",
        "iproaccountants.com", "www.iproaccountants.com", "ipromortgages.com", "www.ipromortgages.com",
        "ipro-prod-web.azurewebsites.net", "ipro-prod-admin.azurewebsites.net"
    };

    private static readonly string[] ProductionDataNames = { "ipro-mysql-prod", "iprostorageprod" };

    private static bool IsProductionHost(string host) =>
        ProductionHosts.Contains(host.Trim().TrimEnd('.'), StringComparer.OrdinalIgnoreCase);

    public static void ThrowIfUnsafe(IConfiguration configuration)
    {
        var problems = Problems(configuration);
        if (problems.Count > 0)
            throw new InvalidOperationException("This staging copy is not safe to start: " + string.Join(" ", problems));
    }

    // Email:OnlyTo, "a@x.com, @example.test": whole addresses, and endings that start with "@".
    public static IReadOnlyList<string> OnlyTo(IConfiguration configuration) =>
        (configuration["Email:OnlyTo"] ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.Contains('@'))
            .Select(entry => entry.ToLowerInvariant())
            .ToList();

    public static bool MaySend(IReadOnlyList<string> onlyTo, string? address)
    {
        var to = (address ?? string.Empty).Trim().ToLowerInvariant();
        if (to.Length == 0) return false;
        return onlyTo.Any(entry => entry.StartsWith('@') ? to.EndsWith(entry, StringComparison.Ordinal) : to == entry);
    }
}
