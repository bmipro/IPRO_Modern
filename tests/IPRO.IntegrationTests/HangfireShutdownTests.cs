using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 451 (2026-09-02). App Service stops a container with a grace period -- 5 seconds by default,
// now 30 via WEBSITES_CONTAINER_STOP_TIME_LIMIT on both apps. Hangfire's server waits
// ShutdownTimeout (15s default) for in-flight jobs before the host may exit. When the platform's
// grace period was shorter than that wait, the container was killed mid-shutdown, a worker's
// dequeue transaction on Hangfire_JobQueue never rolled back, and MySQL kept the dead session's
// row lock until it noticed the peer was gone -- the new container's workers then stalled on it
// for a full command timeout (seen 2026-09-02 16:37 UTC; the same mechanism, with a metadata lock,
// was 445's outage). The code half: Hangfire's own stop must fit inside the platform's window with
// margin, so a stopping container always gets to roll back.
public class HangfireShutdownTests
{
    [Fact]
    public void The_web_hangfire_server_stops_well_inside_the_containers_grace_period()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Program.cs"));
        var start = src.IndexOf("AddHangfireServer(", StringComparison.Ordinal);
        Assert.True(start >= 0, "IPRO.Web must run the Hangfire server");
        var block = src[start..src.IndexOf("});", start, StringComparison.Ordinal)];

        var m = Regex.Match(block, @"ShutdownTimeout\s*=\s*TimeSpan\.FromSeconds\((\d+)\)");
        Assert.True(m.Success, "AddHangfireServer must set ShutdownTimeout explicitly");
        var seconds = int.Parse(m.Groups[1].Value);
        // The container grace period is 30s (WEBSITES_CONTAINER_STOP_TIME_LIMIT); leave real margin.
        Assert.InRange(seconds, 1, 15);
    }

    [Fact]
    public void Admin_runs_no_hangfire_server()
    {
        // Documented invariant: Admin is a dashboard over the shared storage and must never process
        // jobs. A server there would double every recurring job and race the claims.
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Program.cs"));
        Assert.DoesNotContain("AddHangfireServer(", src.Replace("// Dashboard-only view of the same Hangfire storage IPRO.Web writes to - no AddHangfireServer here,", ""));
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
