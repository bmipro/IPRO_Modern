using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using IPRO.Email;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 444 (2026-08-31). Opened and Clicked showed a bare dash that cannot distinguish "nobody
// opened it" from "tracking is not switched on" -- the same defect class as 441, a true state the
// UI cannot express, leaving the agent to guess. Right now it IS not switched on: Microsoft does not
// allow user engagement tracking on custom domains with default sending limits (442), so ACS injects
// no pixel and rewrites no links, and every row in Email Activity shows blank opens and clicks with
// nothing saying why. When 442 is granted the columns start populating for NEW sends only, and
// historical rows stay blank forever -- which will read as a bug unless the UI says otherwise.
//
// The state comes from configuration (Email:EngagementTrackingEnabled), not from the provider: ACS
// has no API that reports whether tracking is on, and the one signal that exists -- a rewritten href
// in a delivered message -- is only visible from the receiving side. The owner flips the flag on
// BOTH App Services when the domain Overview reads Enabled; until then the portal says "not tracked".
public class EngagementTrackingStateTests
{
    // ---- the setting ----------------------------------------------------------------------------

    [Fact]
    public void Tracking_is_off_until_someone_says_otherwise()
    {
        // The safe default. A fresh deployment, a missing key, a typo in the App Service name: all
        // must read as "not tracked", never as "tracked but nobody opens anything".
        Assert.False(new EmailSettings().EngagementTrackingEnabled);
    }

    [Fact]
    public void The_setting_binds_from_the_Email_section_by_this_exact_name()
    {
        // Pins the key the owner will type into Azure: Email__EngagementTrackingEnabled.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Email:EngagementTrackingEnabled"] = "true" })
            .Build();
        var settings = new EmailSettings();
        cfg.GetSection("Email").Bind(settings);
        Assert.True(settings.EngagementTrackingEnabled);
    }

    // ---- the controller hands it to both screens --------------------------------------------

    [Fact]
    public void Email_activity_reads_the_setting_and_passes_it_to_index_and_details()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\EmailActivityController.cs"));
        Assert.Contains("IOptions<EmailSettings>", src);
        // Both actions, not one: Index shows the Opened column, Details shows the tiles and cells.
        Assert.True(Regex.Matches(src, @"ViewBag\.EngagementTracking\s*=").Count >= 2,
            "both Index and Details must set ViewBag.EngagementTracking");
    }

    // ---- both screens say 'not tracked' instead of a dash --------------------------------------

    [Theory]
    [InlineData(@"src\IPRO.Web\Views\EmailActivity\Details.cshtml")]
    [InlineData(@"src\IPRO.Web\Views\EmailActivity\Index.cshtml")]
    public void The_screens_say_not_tracked_rather_than_showing_an_ambiguous_dash(string view)
    {
        var src = File.ReadAllText(FindRepoFile(view));
        Assert.Contains("ViewBag.EngagementTracking", src);
        Assert.Contains("not tracked", src, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_historical_open_is_still_shown_even_when_tracking_is_off_now()
    {
        // SendGrid-era rows carry real OpenedAt/ClickedAt stamps. Turning the flag off must not hide
        // facts we actually have; "not tracked" is only for the ABSENCE of a stamp.
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\EmailActivity\Details.cshtml"));
        Assert.Matches(new Regex(@"OpenedAt\.HasValue\s*\|\|\s*tracking"), src);
        Assert.Matches(new Regex(@"ClickedAt\.HasValue\s*\|\|\s*tracking"), src);
    }

    [Fact]
    public void The_runbook_tells_the_owner_which_key_to_flip_and_when()
    {
        var doc = File.ReadAllText(FindRepoFile(@"DOCS\DNS_ZONE_RUNBOOK.md"));
        Assert.Contains("Email__EngagementTrackingEnabled", doc);
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
