using System;
using System.IO;
using System.Text.RegularExpressions;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 459 (2026-09-02). Once the Connection status panel reads Found / Connected / Secured /
// Forwarding OK, the two registrar-setup cards (the CNAME and the forwarding rule) took half the
// page for nothing. The owner: "the information is there but not as taking half the page". They
// stay, collapsed, behind "Show the setup steps" -- useful when adding another domain -- and are
// expanded as before while anything is still pending or there is no custom domain yet.
public class DomainSetupPanelTests
{
    private static AgentDomain Healthy() => new()
    {
        DnsStatus = AgentDomainStatus.DnsReady,
        AzureBindingStatus = AgentDomainStatus.Bound,
        SslStatus = AgentDomainStatus.Bound,
        RootRedirectsToWww = true
    };

    [Fact]
    public void Fully_connected_means_all_four_checks_pass()
    {
        Assert.True(DomainSetupState.IsFullyConnected(Healthy()));

        var boundDns = Healthy(); boundDns.DnsStatus = AgentDomainStatus.Bound;
        Assert.True(DomainSetupState.IsFullyConnected(boundDns));

        var legacySsl = Healthy(); legacySsl.SslStatus = "SslBound";
        Assert.True(DomainSetupState.IsFullyConnected(legacySsl));
    }

    [Fact]
    public void Anything_still_pending_keeps_the_steps_open()
    {
        Assert.False(DomainSetupState.IsFullyConnected(null));

        var dns = Healthy(); dns.DnsStatus = AgentDomainStatus.PendingDns;
        Assert.False(DomainSetupState.IsFullyConnected(dns));

        var binding = Healthy(); binding.AzureBindingStatus = AgentDomainStatus.BindingPending;
        Assert.False(DomainSetupState.IsFullyConnected(binding));

        var ssl = Healthy(); ssl.SslStatus = AgentDomainStatus.BindingPending;
        Assert.False(DomainSetupState.IsFullyConnected(ssl));

        // The forwarding rule is the one agents skip; "Forwarding OK" must be part of done.
        var root = Healthy(); root.RootRedirectsToWww = false;
        Assert.False(DomainSetupState.IsFullyConnected(root));
    }

    [Fact]
    public void The_setup_steps_collapse_when_the_domain_is_connected_and_stay_reachable()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\Website\Index.cshtml"));
        Assert.Contains("DomainSetupState.IsFullyConnected(", view);

        // The steps live in one collapsible container that is open unless setup is done...
        var container = Regex.Match(view, @"<div class=""collapse[^""]*""[^>]*id=""registrarSteps""[^>]*>", RegexOptions.Singleline);
        Assert.True(container.Success, "the registrar steps must be wrapped in a #registrarSteps collapse");
        Assert.Contains("setupDone", container.Value);

        // ...both cards are inside it...
        var inside = view.Substring(container.Index);
        Assert.True(inside.IndexOf("Point <span class=\"font-monospace\">www</span> at your site", StringComparison.Ordinal) > 0);
        Assert.True(inside.IndexOf("Send the short address to it", StringComparison.Ordinal) > 0);

        // ...and when it is closed there is a visible way to open it, worded for the next domain.
        Assert.Contains("data-bs-target=\"#registrarSteps\"", view);
        Assert.Contains("Show the setup steps", view);
        Assert.Contains("another domain", view);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
