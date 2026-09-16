using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Email;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// 491 (2026-09-16). Microsoft declined to raise the Azure Communication Services sending limits
// (30 a minute, 100 an hour, per subscription) and will not before 30 days of history. Until then
// the platform has to live inside them, and it did not: the four blast paths sent as fast as they
// could and marked every "slow down" (429) recipient Failed, permanently. A newsletter to 150 clients
// would have delivered 30 and failed 120 on launch day.
//
// EmailSendGate is one object per process that every Azure send passes through. It counts sends in
// the last minute and the last hour and waits when a window is full. A small reserve is kept for
// transactional mail so a blast can never hold a password reset behind it. The limits are settings,
// so the day Microsoft relents is a config change. A fake clock drives these tests; nothing sleeps.
public class EmailSendGateTests
{
    // ---- the windows ------------------------------------------------------------------------

    [Fact]
    public async Task Bulk_sends_stop_at_the_per_minute_limit_less_the_reserve_and_wait_for_the_oldest_to_age_out()
    {
        var clock = new FakeClock();
        var gate = new EmailSendGate(30, 100, 5, 10, () => clock.Now, clock.Delay);

        for (var i = 0; i < 25; i++) await gate.WaitForSlotAsync(bulk: true);
        Assert.Empty(clock.Waits);

        await gate.WaitForSlotAsync(bulk: true);
        var wait = Assert.Single(clock.Waits);
        Assert.InRange(wait.TotalSeconds, 59.5, 61);
    }

    [Fact]
    public async Task Transactional_mail_may_use_the_reserve_a_blast_may_not()
    {
        var clock = new FakeClock();
        var gate = new EmailSendGate(30, 100, 5, 10, () => clock.Now, clock.Delay);

        for (var i = 0; i < 25; i++) await gate.WaitForSlotAsync(bulk: true);
        // The five reserved slots are open to a sign-in mail, an invoice, a portal invite...
        for (var i = 0; i < 5; i++) await gate.WaitForSlotAsync(bulk: false);
        Assert.Empty(clock.Waits);

        // ...and the thirty-first send of the minute waits, whoever it is for.
        await gate.WaitForSlotAsync(bulk: false);
        Assert.InRange(Assert.Single(clock.Waits).TotalSeconds, 59.5, 61);
    }

    [Fact]
    public async Task The_hourly_window_holds_as_well()
    {
        var clock = new FakeClock();
        var gate = new EmailSendGate(1000, 100, 0, 10, () => clock.Now, clock.Delay);

        for (var i = 0; i < 90; i++) await gate.WaitForSlotAsync(bulk: true);
        Assert.Empty(clock.Waits);

        await gate.WaitForSlotAsync(bulk: true);
        Assert.InRange(Assert.Single(clock.Waits).TotalMinutes, 59.9, 61);
    }

    [Fact]
    public async Task After_the_wait_the_send_goes_and_the_minute_starts_filling_again()
    {
        var clock = new FakeClock();
        var gate = new EmailSendGate(3, 100, 0, 0, () => clock.Now, clock.Delay);

        for (var i = 0; i < 3; i++) await gate.WaitForSlotAsync(bulk: true);
        await gate.WaitForSlotAsync(bulk: true);            // waits ~60 s, then goes
        Assert.Single(clock.Waits);
        await gate.WaitForSlotAsync(bulk: true);            // second of the new minute: no wait
        await gate.WaitForSlotAsync(bulk: true);            // third: no wait
        Assert.Single(clock.Waits);
        await gate.WaitForSlotAsync(bulk: true);            // fourth: waits again
        Assert.Equal(2, clock.Waits.Count);
    }

    [Fact]
    public async Task A_throttle_reported_by_the_provider_holds_every_send_for_the_retry_after()
    {
        // The gate is the first line; a 429 that still gets through (another process, a limit that
        // moved) is the second: hold everything for as long as Microsoft asked, a minute if it did not say.
        var clock = new FakeClock();
        var gate = new EmailSendGate(30, 100, 5, 10, () => clock.Now, clock.Delay);

        gate.ReportThrottled(TimeSpan.FromSeconds(30));
        await gate.WaitForSlotAsync(bulk: false);
        Assert.InRange(Assert.Single(clock.Waits).TotalSeconds, 29.5, 31);

        gate.ReportThrottled(null);
        await gate.WaitForSlotAsync(bulk: true);
        Assert.InRange(clock.Waits[1].TotalSeconds, 59.5, 61);
    }

    // ---- who counts as bulk ---------------------------------------------------------------

    [Theory]
    [InlineData("newsletter", true)]
    [InlineData("drip_step", true)]
    [InlineData("ecard", true)]
    [InlineData("eletter", true)]
    [InlineData("poll", true)]
    [InlineData("didyouknow", true)]
    [InlineData("invoice", false)]
    [InlineData("", false)]
    public void The_six_marketing_kinds_are_bulk_everything_else_is_transactional(string entity, bool bulk)
    {
        var args = new Dictionary<string, string> { ["ipro_entity"] = entity };
        Assert.Equal(bulk, EmailSendGate.IsBulk(args));
    }

    [Fact]
    public void Mail_with_no_tags_is_transactional()
    {
        Assert.False(EmailSendGate.IsBulk(null));
        Assert.False(EmailSendGate.IsBulk(new Dictionary<string, string>()));
    }

    // ---- the settings -----------------------------------------------------------------------

    [Fact]
    public void The_defaults_are_the_ACS_default_limits_and_the_keys_bind_by_these_names()
    {
        var defaults = new EmailSettings();
        Assert.Equal(30, defaults.SendsPerMinute);
        Assert.Equal(100, defaults.SendsPerHour);
        Assert.Equal(5, defaults.TransactionalReservePerMinute);
        Assert.Equal(10, defaults.TransactionalReservePerHour);

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:SendsPerMinute"] = "500",
            ["Email:SendsPerHour"] = "10000",
            ["Email:TransactionalReservePerMinute"] = "20",
            ["Email:TransactionalReservePerHour"] = "200"
        }).Build();
        var bound = new EmailSettings();
        cfg.GetSection("Email").Bind(bound);
        var gate = new EmailSendGate(Microsoft.Extensions.Options.Options.Create(bound));
        Assert.Equal(500, gate.SendsPerMinute);
        Assert.Equal(10000, gate.SendsPerHour);
        Assert.Equal(20, gate.TransactionalReservePerMinute);
        Assert.Equal(200, gate.TransactionalReservePerHour);
    }

    // ---- wiring pins ------------------------------------------------------------------------

    [Fact]
    public void The_azure_sender_waits_at_the_gate_before_every_send_and_reports_a_throttle()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Email\AzureEmailService.cs"));
        var wait = src.IndexOf("WaitForSlotAsync(", StringComparison.Ordinal);
        var send = src.IndexOf("client.SendAsync(WaitUntil.Started", StringComparison.Ordinal);
        Assert.True(wait > 0 && send > wait, "the gate must be awaited before the client sends");
        Assert.Contains("ReportThrottled(", src);
    }

    [Theory]
    [InlineData(@"src\IPRO.Web\Program.cs")]
    [InlineData(@"src\IPRO.Admin\Program.cs")]
    public void Both_apps_register_one_gate_per_process(string file)
    {
        Assert.Contains("AddSingleton<EmailSendGate>", File.ReadAllText(FindRepoFile(file)));
    }

    [Theory]
    [InlineData(@"src\IPRO.Email\NewsLetterDispatcher.cs")]
    [InlineData(@"src\IPRO.Email\ECardDispatcher.cs")]
    [InlineData(@"src\IPRO.Email\ELetterDispatcher.cs")]
    [InlineData(@"src\IPRO.Email\PollDispatcher.cs")]
    public void Every_blast_path_pauses_on_a_transient_failure_instead_of_failing_the_recipient(string file)
    {
        var src = File.ReadAllText(FindRepoFile(file));
        Assert.Contains("result.IsTransient", src);
        Assert.Contains("PauseForRetryAsync(", src);
    }

    // ---- harness ------------------------------------------------------------------------------

    private sealed class FakeClock
    {
        public DateTime Now = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);
        public List<TimeSpan> Waits { get; } = new();

        public Task Delay(TimeSpan wait, CancellationToken ct)
        {
            Waits.Add(wait);
            Now = Now.Add(wait);
            return Task.CompletedTask;
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
