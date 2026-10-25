using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using IPRO.Utility;
using Xunit;

namespace IPRO.IntegrationTests;

// 603 (owner, 2026-10-24, after connecting www.iproadvisors.ca on the MichaelTran demo account): "From step 1 to step 2
// took almost no time ... I did not realize that I was already on my second step." The guide changed step quietly: a
// class, a thin border, "Checking..." in small grey at the bottom, and words only a screen reader hears. Now the step
// the adviser is on is large with a bar of the four steps, an hourglass turns while iPro looks and the next look counts
// down, a line says so when a step changes on its own, the new step glows once, and the tab's title carries the step.
// Also from him: the word "SSL" instead of "Padlock" wherever an adviser or staff member reads it.
public class DomainGuideProgress603Tests
{
    private const string Target = DomainGuideScreens595Tests.Target;
    private const string Token = DomainGuideScreens595Tests.Token;
    private const string Asuid = "C6D5FC2CBD50987D0FF7FE648BC710D4B2454FA211AE9094BB2816A6505E8572";

    [Fact]
    public async Task The_step_the_adviser_is_on_is_large_with_a_bar_of_the_steps_and_an_hourglass_line()
    {
        var bakery = DomainGuideScreens595Tests.LiveBakery();
        var maison = DomainGuideScreens595Tests.NewMaison();
        var html = await DomainGuideScreens595Tests.RenderMyWebsiteAsync(new[] { bakery, maison }, bakery, openGuideFor: maison.Id, page: "Domains");
        var guide = html[html.IndexOf("data-domain-guide=\"58\"", StringComparison.Ordinal)..];

        Assert.Contains("<p class=\"guide-heading mb-0\" data-guide-heading>Step 1 of 4: Prove it&#x27;s yours</p>", guide);
        Assert.Contains("guide-meter__part guide-meter__part--current\" data-guide-meter=\"1\"", guide);
        Assert.Contains("guide-meter__part guide-meter__part--upcoming\" data-guide-meter=\"2\"", guide);
        // The bar repeats what the heading says: not read out twice.
        Assert.Contains("<div class=\"guide-meter\" aria-hidden=\"true\">", guide);
        // The hourglass line, with the time to the next look; the line for a step changing waits, hidden.
        Assert.Contains("guide-activity guide-activity--waiting\" data-guide-activity", guide);
        Assert.Contains("fa-hourglass-half guide-activity__icon", guide);
        Assert.Contains("data-guide-next", guide);
        Assert.Contains("<div class=\"guide-moved\" data-guide-moved hidden>", guide);
        // "Checked" moved up beside the hourglass, once.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(guide[..guide.IndexOf("</section>", StringComparison.Ordinal)], "data-guide-checked"));
        Assert.Contains("guide-check\" data-guide-check>Check now</button>", guide);

        // A live domain's guide: a tick instead of the hourglass, every step of the bar done.
        var live = html[html.IndexOf("data-domain-guide=\"41\"", StringComparison.Ordinal)..];
        live = live[..live.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Contains("guide-activity guide-activity--done\" data-guide-activity", live);
        Assert.Contains("fa-circle-check guide-activity__icon", live);
        Assert.DoesNotContain("guide-meter__part--current", live);
        Assert.DoesNotContain("guide-meter__part--upcoming", live);
    }

    [Fact]
    public void The_script_shows_what_it_does_and_says_when_a_step_changes()
    {
        var script = Read("src/IPRO.Web/wwwroot/js/domain-guide.js");
        Assert.Contains("var CHECKING = 'Checking your domain now...';", script);
        Assert.Contains("this.setActivity('busy');", script);
        Assert.Contains("guide.setActivity(guide.stopped ? 'stopped' : guide.watching() ? 'waiting' : 'done');", script);
        Assert.Contains("'Next check in ' + seconds + (seconds === 1 ? ' second.' : ' seconds.')", script);
        Assert.Contains(": done + 'Now step ' + shown + ': ' + title + '.', back);", script);
        Assert.Contains("this.moved(data.heading + ', secured with SSL. There is nothing more to do.', false);", script);
        Assert.Contains("guide.moved('Step 3 skipped. Now step 4: iPro finishes.', false);", script);
        // Gone live with the page open: the domain's row stops saying "Setting up" at the same moment.
        Assert.Contains("if (badge) { badge.className = 'badge bg-success'; text(badge, 'Live'); }", script);
        Assert.Contains("step.classList.add('guide-step--arrived');", script);
        Assert.Contains("document.title = (live ? 'Live' : 'Step ' + shown + ' of ' + count) + (BASE_TITLE ? ' | ' + BASE_TITLE : '');", script);
        // In another tab only the guide in the title looks, at most once a minute (the server's limit is 20 a minute).
        Assert.Contains("var HIDDEN_INTERVAL = 60000;", script);
        Assert.Contains("var wait = document.visibilityState === 'visible' ? delay : Math.max(delay, HIDDEN_INTERVAL);", script);
        Assert.Contains("return this.titled && !this.section.hidden && this.inView && document.visibilityState !== 'visible';", script);
        Assert.Equal(20, IPRO.Web.Controllers.WebsiteController.GuideLooks.PerWindow);
        // The scroll to a step that arrives off the screen keeps still for people who asked for less motion.
        Assert.Contains("matchMedia('(prefers-reduced-motion: reduce)')", script);
        // Still text only: what DNS holds never becomes markup.
        Assert.DoesNotContain("innerHTML", script);
        Assert.DoesNotContain("insertAdjacentHTML", script);

        var page = Read("src/IPRO.Web/Views/Website/Domains.cshtml");
        Assert.Contains("@@keyframes guide-turn", page);
        Assert.Contains("@@keyframes guide-arrive", page);
        Assert.Contains("@@media (prefers-reduced-motion: reduce) {\n  .guide-activity--busy .guide-activity__icon, .guide-check.is-checking::before, .guide-step--arrived { animation:none; }", page);
        Assert.Contains(".guide-step--current .guide-step__title { font-size:1.15rem; font-weight:700; color:#0c1f44; }", page);
    }

    [Fact]
    public void SSL_not_padlock_wherever_an_adviser_or_staff_reads_it()
    {
        var finishing = DomainGuideScreens595Tests.NewMaison();
        finishing.OwnershipVerifiedAt = DateTime.UtcNow;
        finishing.DnsStatus = finishing.AzureBindingStatus = AgentDomainStatus.Bound;
        var settings = DomainSetupGuide.Settings.From(Target, "40.89.19.0", Asuid);
        var guide = DomainSetupGuide.Describe(finishing, Look(), settings, DateTime.UtcNow);
        Assert.Equal("SSL certificate", guide.Finish.Single(p => p.Key == "padlock").Label);
        Assert.Equal("SSL ordered", guide.Steps.Single(s => s.Number == 4).Status);
        var said = string.Join("\n", new[] { guide.Heading }
            .Concat(guide.Steps.SelectMany(s => new[] { s.Title, s.Status, s.Seen, s.Tip }))
            .Concat(guide.Finish.SelectMany(p => new[] { p.Label, p.Detail })));
        Assert.DoesNotContain("padlock", said, StringComparison.OrdinalIgnoreCase);
        foreach (var file in new[]
        {
            "src/IPRO.Utility/DnsHosts.cs",
            "src/IPRO.Web/Views/Website/_DomainGuide.cshtml", "src/IPRO.Web/Views/Website/Index.cshtml", "src/IPRO.Scheduler/DomainAutomationJob.cs",
            "src/IPRO.Admin/Views/Agents/Details.cshtml", "src/IPRO.Admin/Views/Domains/Index.cshtml",
        })
        {
            // Only the step's own key stays (data-guide-progress="padlock"), never as words on the screen.
            var words = Read(file).Replace("\"padlock\"", string.Empty);
            Assert.DoesNotContain("padlock", words, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("secured with SSL.</p>", Read("src/IPRO.Scheduler/DomainAutomationJob.cs"));
        Assert.Contains("The SSL certificate renews itself.", Read("src/IPRO.Scheduler/DomainAutomationJob.cs"));
        Assert.Contains("\"Connected, SSL on its way\"", Read("src/IPRO.Web/Views/Website/Index.cshtml"));
    }

    private static DomainSetupLook Look() => new()
    {
        CheckedAtUtc = DateTime.UtcNow, FromZoneServers = true, Zone = "maison1850.com", NameServers = new[] { "ns25.domaincontrol.com", "ns26.domaincontrol.com" },
        ProofValues = new[] { Token }, WwwCname = Target, WwwAddresses = new[] { "40.89.19.0" },
        RootAddresses = new[] { "40.89.19.0" }, AsuidValues = new[] { Asuid }, PlatformAddresses = new[] { "40.89.19.0" },
        MailServers = new[] { "maison1850-com.mail.protection.outlook.com" }, MailOnShortAddress = false,
    };

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(DomainGuideScreens595Tests.RepoRoot(), relative)).Replace("\r\n", "\n");
}
