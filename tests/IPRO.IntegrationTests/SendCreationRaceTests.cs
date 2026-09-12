using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 481 (2026-09-12). The owner sent an e-letter to two clients: the first was delivered, the
// second stayed Queued, with no error or warning in the production log and exactly one SendMail at
// ACS, and the minutely sweep never touched it again. The dispatcher mails every Queued row it finds
// (probed both ways on 09-12), so the second row was not there to find: the controller saved the
// letter first -- Scheduled, ScheduledAt = now, already DUE -- and its recipient rows in a second
// save. The minutely dispatch job can claim the letter in that gap and finish with none (or, if the
// row inserts commit one by one, one) of its recipients; the letter ends Sent, the rest stay Queued
// under it forever, and the claim-first design means the button's own dispatch then finds nothing
// to claim. Same shape in the e-card controller. Newsletters and polls build their recipient rows
// inside the dispatcher, so they never had the gap.
//
// Fix: the send and its recipient rows commit in ONE transaction, so nothing can see the letter
// before its rows exist. Repair: a Queued row under a Sent or Failed parent is a contradiction the
// sweep will never resolve; startup marks such rows Failed with the reason on the row, where Email
// Activity's Issue column reads it, so the adviser can send again.
public class SendCreationRaceTests
{
    [Theory]
    [InlineData(@"src\IPRO.Web\Controllers\ELettersController.cs", "_db.ELetters.Add(letter)", "_db.ELetterRecipients.AddRange(recipients)", "_dispatcher.DispatchAsync(letter.Id)")]
    [InlineData(@"src\IPRO.Web\Controllers\ECardsController.cs", "_db.ECards.Add(card)", "_db.ECardRecipients.AddRange(recipients)", "_dispatcher.DispatchAsync(card.Id)")]
    public void The_send_and_its_recipient_rows_become_visible_together(string file, string addParent, string addRows, string dispatch)
    {
        var src = File.ReadAllText(FindRepoFile(file));
        var begin = src.IndexOf("BeginTransactionAsync(", StringComparison.Ordinal);
        var parent = src.IndexOf(addParent, StringComparison.Ordinal);
        var rows = src.IndexOf(addRows, StringComparison.Ordinal);
        var commit = src.IndexOf(".CommitAsync()", StringComparison.Ordinal);
        var send = src.IndexOf(dispatch, StringComparison.Ordinal);
        Assert.True(begin >= 0, "the creation is not wrapped in a transaction");
        Assert.True(parent > begin && rows > parent && commit > rows && send > commit,
            $"expected begin < add parent < add rows < commit < dispatch; got {begin}, {parent}, {rows}, {commit}, {send}");
    }

    [Fact]
    public async Task Startup_marks_recipients_stranded_under_a_finished_send_as_failed_with_the_reason()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = $"race-{Guid.NewGuid():N}"[..20], Email = "race.agent@example.com", FirstName = "Race", LastName = "Agent",
            DomainName = $"race-{Guid.NewGuid():N}"[..24], PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        var client = new Client { AgentUserId = agent.Id, FirstName = "John", LastName = "Mot", Email = $"john-{Guid.NewGuid():N}"[..20] + "@example.net", IsNewsletterSubscribed = true };
        db.Add(client);
        await db.SaveChangesAsync();

        // The owner's letter: Sent, one recipient Sent, one left Queued.
        var finished = NewLetter(agent.Id, ELetterStatuses.Sent, DateTime.UtcNow.AddMinutes(-40));
        // A letter still waiting for its time, and one mid-send: their Queued rows are legitimate.
        var waiting = NewLetter(agent.Id, ELetterStatuses.Scheduled, DateTime.UtcNow.AddHours(2));
        var sending = NewLetter(agent.Id, ELetterStatuses.Sending, DateTime.UtcNow.AddMinutes(-1));
        sending.ClaimedAt = DateTime.UtcNow;
        var failedCard = new ECard { AgentUserId = agent.Id, Occasion = "birthday", Subject = "s", Message = "m", Status = ECardStatuses.Failed, ScheduledAt = DateTime.UtcNow.AddMinutes(-30) };
        db.AddRange(finished, waiting, sending, failedCard);
        await db.SaveChangesAsync();

        var sentRow = new ELetterRecipient { ELetterId = finished.Id, ClientId = client.Id, Email = client.Email, RecipientName = "iPro test", Status = ELetterRecipientStatuses.Sent, SentAt = DateTime.UtcNow.AddMinutes(-40) };
        var strandedRow = new ELetterRecipient { ELetterId = finished.Id, ClientId = client.Id, Email = client.Email, RecipientName = "John Mot" };
        var waitingRow = new ELetterRecipient { ELetterId = waiting.Id, ClientId = client.Id, Email = client.Email, RecipientName = "John Mot" };
        var sendingRow = new ELetterRecipient { ELetterId = sending.Id, ClientId = client.Id, Email = client.Email, RecipientName = "John Mot" };
        var strandedCardRow = new ECardRecipient { ECardId = failedCard.Id, ClientId = client.Id, Email = client.Email, RecipientName = "John Mot" };
        db.AddRange(sentRow, strandedRow, waitingRow, sendingRow, strandedCardRow);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var repaired = await StartupSchemaRepair.RepairRecipientsStrandedUnderFinishedSendsAsync(db);
        Assert.Equal(2, repaired);

        await using var check = testDb.CreateContext();
        var stranded = await check.ELetterRecipients.SingleAsync(r => r.Id == strandedRow.Id);
        Assert.Equal(ELetterRecipientStatuses.Failed, stranded.Status);
        Assert.Contains("finished before this recipient", stranded.FailureReason);
        Assert.Contains("again", stranded.FailureReason);
        Assert.Equal(ELetterRecipientStatuses.Sent, (await check.ELetterRecipients.SingleAsync(r => r.Id == sentRow.Id)).Status);
        Assert.Equal(ELetterRecipientStatuses.Queued, (await check.ELetterRecipients.SingleAsync(r => r.Id == waitingRow.Id)).Status);
        Assert.Equal(ELetterRecipientStatuses.Queued, (await check.ELetterRecipients.SingleAsync(r => r.Id == sendingRow.Id)).Status);
        var card = await check.ECardRecipients.SingleAsync(r => r.Id == strandedCardRow.Id);
        Assert.Equal(ECardRecipientStatuses.Failed, card.Status);
        Assert.Contains("finished before this recipient", card.FailureReason);

        // Idempotent: the next startup finds nothing to do.
        Assert.Equal(0, await StartupSchemaRepair.RepairRecipientsStrandedUnderFinishedSendsAsync(check));
    }

    [Fact]
    public void Both_apps_run_the_repair_at_startup_after_the_send_tables_exist()
    {
        foreach (var program in new[] { @"src\IPRO.Web\Program.cs", @"src\IPRO.Admin\Program.cs" })
        {
            var src = File.ReadAllText(FindRepoFile(program));
            var tables = src.IndexOf("StartupSchemaRepair.EnsureELetterSchemaAsync(db)", StringComparison.Ordinal);
            var repair = src.IndexOf("StartupSchemaRepair.RepairRecipientsStrandedUnderFinishedSendsAsync(db)", StringComparison.Ordinal);
            Assert.True(tables >= 0, program + " does not create the e-letter tables");
            Assert.True(repair > tables, program + " does not run the stranded-recipient repair after the send tables exist");
        }
    }

    private static ELetter NewLetter(int agentId, string status, DateTime scheduledAt) => new()
    {
        AgentUserId = agentId, TemplateKey = "welcome", Subject = "Welcome aboard, [First Name]", Body = "<p>Hello</p>",
        Status = status, ScheduledAt = scheduledAt, TotalRecipients = 1
    };

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
