using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 445 (2026-09-01). A docs-only push took production down for 24 minutes. The container log
// settled the cause: the workflow's explicit post-deploy restart fired into the deploy's OWN restart,
// killed the app mid startup-DDL, and every later start blocked on the dead session's metadata lock
// until the 30s command timeout threw -- exit 134, five times, until the lock was reaped. Admin runs
// the same startup against the same database and has never crashed, because its explicit restart had
// been failing silently (wrong resource group) and it only ever restarted once.
//
// Two rules now, both pinned here because a CI change gets no other automated check short of a real
// deploy:
//   1. The explicit restart is CONDITIONAL: poll for the new build first and restart only if the
//      old one is still serving. Never restart an app that is in the middle of starting.
//   2. The verify step gets ONE second-chance restart before failing, in case the deploy's own
//      restart wedged -- the manual remedy that worked, automated.
// And admin is addressed in its real resource group, so both restarts can actually reach it.
public class DeployWorkflowTests
{
    [Theory]
    [InlineData(@".github\workflows\main_ipro-prod-web.yml",   "ipro-prod-web",   "ipro-production")]
    [InlineData(@".github\workflows\main_ipro-prod-admin.yml", "ipro-prod-admin", "ipro-prod-admin_group")]
    public void The_post_deploy_restart_is_conditional_on_the_old_build_still_serving(string file, string app, string rg)
    {
        var yml = File.ReadAllText(FindRepoFile(file));

        Assert.Contains("Restart only if the new package is not being served yet", yml);
        // Gives the deploy's own restart 90 seconds (9 x 10s) before deciding.
        Assert.Contains("seq 1 9", yml);
        Assert.Contains("no restart needed", yml);
        // The unconditional step is gone.
        Assert.DoesNotContain("name: Restart the app so the new package is mounted", yml);

        // Every restart names the app's REAL resource group -- admin was silently unreachable.
        var restarts = Regex.Matches(yml, @"az webapp restart --name '" + Regex.Escape(app) + @"' --resource-group '([^']+)'");
        Assert.True(restarts.Count >= 2, $"{app}: expected the conditional restart AND the second-chance restart, found {restarts.Count}");
        foreach (Match m in restarts)
            Assert.Equal(rg, m.Groups[1].Value);
    }

    [Theory]
    [InlineData(@".github\workflows\main_ipro-prod-web.yml",   "ipro-prod-web")]
    [InlineData(@".github\workflows\main_ipro-prod-admin.yml", "ipro-prod-admin")]
    public void The_verify_step_restarts_once_more_before_declaring_failure(string file, string app)
    {
        var yml = File.ReadAllText(FindRepoFile(file));

        Assert.Contains("second-chance restart (445)", yml);
        Assert.Contains("wait_for_build()", yml);
        Assert.True(Regex.Matches(yml, @"if wait_for_build; then exit 0; fi").Count == 2,
            "the poll must run before AND after the second-chance restart");
        Assert.Contains("after a second restart", yml);
        Assert.Contains("::error::", yml);
        Assert.Contains($"restarting once more (445)", yml);
    }

    [Fact]
    public void Admin_is_never_addressed_in_the_wrong_resource_group()
    {
        // The exact string that failed silently on every deploy until 2026-09-01.
        var yml = File.ReadAllText(FindRepoFile(@".github\workflows\main_ipro-prod-admin.yml"));
        Assert.DoesNotContain("--name 'ipro-prod-admin' --resource-group 'ipro-production'", yml);
    }

    [Fact]
    public void Both_apps_share_one_deploy_queue_and_never_cancel_each_other()
    {
        foreach (var file in new[] { @".github\workflows\main_ipro-prod-web.yml", @".github\workflows\main_ipro-prod-admin.yml" })
        {
            var yml = File.ReadAllText(FindRepoFile(file));
            Assert.Contains("group: deploy-ipro-production", yml);
            Assert.Contains("cancel-in-progress: false", yml);
        }
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
