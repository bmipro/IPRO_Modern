using System;
using System.IO;
using IPRO.Scheduler;
using Xunit;

namespace IPRO.IntegrationTests;

// 521 (2026-09-25): the certificate watchdog spoke for the lego days -- "Renew with Renew-Certs.ps1
// on the maintenance machine" -- after 505 moved both hosts to App Service managed certificates.
// A watchdog that fires once in six months and then gives the wrong instruction is worse than
// silence, so its message, its email and the doc they point at now say what to check when Azure
// has not renewed: the zone's CAA record and the certificate's binding.
public class CertificateWatchdog521Tests
{
    [Fact]
    public void The_watchdog_no_longer_sends_anyone_to_the_lego_script()
    {
        var job = File.ReadAllText(FindRepoFile(@"src\IPRO.Scheduler\CertificateExpiryJob.cs"));
        Assert.DoesNotContain("Renew-Certs", job);
        Assert.DoesNotContain("maintenance machine", job);
        Assert.Contains("digicert.com", job);
        Assert.Contains("When a managed certificate does not renew", job);

        var doc = File.ReadAllText(FindRepoFile(@"DOCS\20_CERTIFICATES.md"));
        Assert.Contains("## When a managed certificate does not renew", doc);
    }

    [Fact]
    public void The_red_row_names_each_host_and_what_to_check()
    {
        var message = CertificateExpiryJob.FailureMessage(new[]
        {
            "app.iproadvisers.com: 12 days left (expires 2026-10-07)",
            "www.ipromortgages.com: certificate could not be read"
        }, 30);

        Assert.Contains("app.iproadvisers.com: 12 days left (expires 2026-10-07)", message);
        Assert.Contains("www.ipromortgages.com: certificate could not be read", message);
        Assert.Contains("inside 30 days", message);
        Assert.Contains("CAA", message);
        Assert.Contains("digicert.com", message);
        Assert.Contains("bound", message);
        Assert.Contains("DOCS/20_CERTIFICATES.md", message);
        Assert.DoesNotContain("Renew-Certs", message);
        Assert.DoesNotContain("lego", message);
    }

    [Fact]
    public void The_alert_email_lists_the_checks_and_carries_no_command_to_run()
    {
        var html = CertificateExpiryJob.AlertHtml(new[]
        {
            "admin.iproadvisers.com: 29 days left (expires 2026-10-24)",
            "x <b>y</b>"
        }, 30);

        Assert.Contains("admin.iproadvisers.com: 29 days left (expires 2026-10-24)", html);
        Assert.Contains("x &lt;b&gt;y&lt;/b&gt;", html);            // a problem is text, never markup
        Assert.Contains("inside 30 days", html);
        Assert.Contains("digicert.com", html);
        Assert.Contains("bound", html);
        Assert.Contains("app.iproadvisers.com/media", html);       // why it matters is still said
        Assert.DoesNotContain("powershell", html);
        Assert.DoesNotContain("Renew-Certs", html);
        Assert.DoesNotContain("<pre", html);
    }

    [Fact]
    public void Every_platform_name_is_watched_by_default()
    {
        var watched = CertificateExpiryJob.DefaultWatchList;
        foreach (var host in new[]
                 {
                     "app.iproadvisers.com", "admin.iproadvisers.com",
                     "www.iproadvisers.com", "iproadvisers.com",
                     "www.iproaccountants.com", "iproaccountants.com",
                     "www.ipromortgages.com", "ipromortgages.com"
                 })
            Assert.Contains(host, watched);
        Assert.Equal(8, watched.Count);
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
