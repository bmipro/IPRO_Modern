using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 445 (2026-09-01). A docs-only push left the ipro-prod-web worker container STOPPED after the
// workflow's scripted post-deploy restart. The front door returned 503 on every path for ~24 minutes.
// The verify step did exactly what it was built for -- it refused to report success -- and then
// nothing happened: no remediation, no page, a GitHub failure email. One more `az webapp restart`
// was the entire fix.
//
// So the verify step now gets a second chance: if the first 5-minute poll fails, it restarts the app
// once more, right there where the deploy credentials already are, and polls again. It still fails
// loudly if that does not take. These pins hold both workflows to that shape; they are the only
// automated check a CI change gets short of a real deploy.
public class DeployWorkflowTests
{
    [Theory]
    [InlineData(@".github\workflows\main_ipro-prod-web.yml",   "ipro-prod-web")]
    [InlineData(@".github\workflows\main_ipro-prod-admin.yml", "ipro-prod-admin")]
    public void The_verify_step_restarts_once_more_before_declaring_failure(string file, string app)
    {
        var yml = File.ReadAllText(FindRepoFile(file));

        // The second chance is present and says why.
        Assert.Contains("second-chance restart (445)", yml);

        // One scripted restart after the deploy, one more inside the verify step.
        var restarts = Regex.Matches(yml, @"az webapp restart --name '" + Regex.Escape(app) + "'").Count;
        Assert.True(restarts >= 2, $"{app}: expected the post-deploy restart AND the second-chance restart, found {restarts}");

        // The poll is a function called twice, not a copy-pasted loop that can drift.
        Assert.Contains("wait_for_build()", yml);
        Assert.True(Regex.Matches(yml, @"if wait_for_build; then exit 0; fi").Count == 2,
            "the poll must run before AND after the second-chance restart");

        // And it still fails, loudly, if the second chance does not take.
        Assert.Contains("after a second restart", yml);
        Assert.Contains("::error::", yml);
    }

    [Fact]
    public void Both_apps_share_one_deploy_queue_and_never_cancel_each_other()
    {
        // Pinned because 2026-08-31 showed what a cancelled admin run looks like: the two hosts
        // silently drift apart. Queueing is the intended behaviour; cancellation is not.
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
