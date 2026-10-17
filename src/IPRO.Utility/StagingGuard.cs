using Microsoft.Extensions.Configuration;

namespace IPRO.Utility;

// 585 (the owner's decision of 2026-10-17, "Builder + staging together"): a staging copy of iPro, where
// a release is checked before it reaches production. It runs the same code, so what keeps it from
// touching a real person is configuration, and this guard holds that configuration to the rule:
//
//   App:Environment         "Staging" makes an app a staging copy (anything else, or nothing, is not)
//   Email:OnlyTo            required: the only addresses (or "@domain" endings) staging may email;
//                           every other email is written to the log and not sent (RestrictedEmail)
//   PayPal:IsSandbox        required true: staging never takes real money
//   AzureDomainAutomation:Enabled  must not be true: staging never touches customers' domains
//   APPLICATIONINSIGHTS_CONNECTION_STRING  must not be set: staging's traffic is not production's telemetry
//
// A staging copy that breaks a rule does not start (Program.cs of both apps), and says which rule.
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
        if (!string.IsNullOrWhiteSpace(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
            problems.Add("APPLICATIONINSIGHTS_CONNECTION_STRING is set: staging must not report to production's Application Insights.");
        return problems;
    }

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
