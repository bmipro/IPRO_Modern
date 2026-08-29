using System;
using System.IO;
using IPRO.Utility;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// The two config hazards from the 2026-08-28 staging review round, fixed 2026-08-29. Both share
// one failure shape: a second boot of this codebase with copied production config -- a staging
// box, the snapshot-rehearsal server, a local run with pasted settings -- inherits production's
// levers and pulls them within minutes.
//
//   1. Both apps SHIP "WebAppName": "ipro-prod-web" in committed appsettings, and
//      AzureDomainAutomationService issues HTTP DELETE against that app's hostname bindings and
//      certificates. The service now refuses to act unless WEBSITE_SITE_NAME (stamped by App
//      Service on every real instance) MATCHES the configured target: it can only mutate the app
//      it is running as. Production matches itself; everything else goes inert, loudly.
//
//   2. All 16 recurring jobs registered unconditionally and the Hangfire server started
//      unconditionally. Jobs__RecurringDisabled=true now makes an instance a bystander -- no
//      server (a server on shared storage processes jobs even with no registrations), no
//      registrations. Program.cs wiring is pinned by source-walk (the M8 pattern).
public class ConfigHazardTests : IDisposable
{
    public ConfigHazardTests() => AzureDomainAutomationService.SiteNameProvider =
        () => Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
    public void Dispose() => AzureDomainAutomationService.SiteNameProvider =
        () => Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");

    private static AzureDomainAutomationService NewService(string configuredApp) => new(
        new NullHttpFactory(),
        Options.Create(new AzureDomainAutomationOptions
        {
            Enabled = true,
            TenantId = "t", ClientId = "c", ClientSecret = "s",
            SubscriptionId = "sub", ResourceGroup = "rg",
            WebAppName = configuredApp
        }),
        NullLogger<AzureDomainAutomationService>.Instance);

    [Fact]
    public void A_process_running_as_a_different_app_may_not_manage_production()
    {
        // The staging-box scenario: copied config says ipro-prod-web, but the process is running
        // as some other site. Refused before any HTTP call exists to make.
        AzureDomainAutomationService.SiteNameProvider = () => "ipro-staging-web";
        var service = NewService("ipro-prod-web");

        Assert.False(service.RunningAsConfiguredApp(out var reason));
        Assert.Contains("ipro-staging-web", reason);
        Assert.Contains("ipro-prod-web", reason);
    }

    [Fact]
    public void A_process_outside_app_service_may_not_manage_anything()
    {
        // Local dev with pasted prod settings: WEBSITE_SITE_NAME is unset outside App Service.
        AzureDomainAutomationService.SiteNameProvider = () => null;
        var service = NewService("ipro-prod-web");

        Assert.False(service.RunningAsConfiguredApp(out var reason));
        Assert.Contains("not running in Azure App Service", reason);
    }

    [Fact]
    public void Production_managing_itself_is_unaffected()
    {
        // The other direction: the fix must not break the one legitimate caller. Case-insensitive
        // because Azure site names are.
        AzureDomainAutomationService.SiteNameProvider = () => "IPRO-PROD-WEB";
        var service = NewService("ipro-prod-web");

        Assert.True(service.RunningAsConfiguredApp(out _));
    }

    [Fact]
    public void Both_domain_entry_points_consult_the_identity_guard()
    {
        // Wiring pin: a guard only EnsureDomainAsync consults still lets RemoveDomainAsync
        // delete another app's certificate.
        var source = File.ReadAllText(FindRepoFile(@"src\IPRO.Utility\AzureDomainAutomationService.cs"));
        var first = source.IndexOf("RunningAsConfiguredApp(out var identityReason)", StringComparison.Ordinal);
        var second = source.IndexOf("RunningAsConfiguredApp(out var identityReason)", first + 1, StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first,
            "both EnsureDomainAsync and RemoveDomainAsync must call RunningAsConfiguredApp");
    }

    [Fact]
    public void The_recurring_schedule_has_a_bystander_switch()
    {
        // Source-walk pin on Program.cs: the flag must gate BOTH the Hangfire server and the
        // registrations -- a server on shared storage processes jobs with no registrations at all.
        var program = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Program.cs"));
        Assert.Contains("Jobs:RecurringDisabled", program);

        var flagIndex = program.IndexOf("var recurringJobsDisabled", StringComparison.Ordinal);
        var serverIndex = program.IndexOf("AddHangfireServer", StringComparison.Ordinal);
        var jobsIndex = program.IndexOf("RecurringJob.AddOrUpdate<NewsLetterDispatchJob>", StringComparison.Ordinal);
        Assert.True(flagIndex >= 0 && flagIndex < serverIndex,
            "the flag must be read before (and gate) AddHangfireServer");
        Assert.Contains("if (!recurringJobsDisabled)", program[..(serverIndex + 200)]);
        Assert.Contains("if (recurringJobsDisabled)", program[..jobsIndex]);
        Assert.Contains("bystander", program);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }

    private sealed class NullHttpFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("the identity guard must refuse before any HTTP client is created");
    }
}
