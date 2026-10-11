using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Xunit;

namespace IPRO.IntegrationTests;

// 562 (2026-10-11). The owner, looking at the bakery's first invoice in Email Activity: "remind me
// again as why we do not track Opened?" and, of the paragraph under the table, "it is as confusing
// if it was written by me". Invoice mail has no open pixel on purpose: the invoice page stamps a
// view each time the client opens the invoice (452), which says more than a mail app loading
// pictures. But the screen answered "not tracked" and never said so. It now shows whether the
// client viewed the invoice, and the explanation is one short line per word on the screen.
public class InvoiceViewed562Tests
{
    private static readonly DateTime Sent = new(2026, 10, 10, 21, 26, 0, DateTimeKind.Utc);
    private static string Stamp(DateTime value) => value.ToString("MMM d, h:mm tt");

    [Fact]
    public void A_view_counts_for_an_email_when_it_happened_after_that_email_went_out()
    {
        Assert.True(InvoiceViewText.ViewedSince(Sent, Sent.AddMinutes(5)));
        Assert.True(InvoiceViewText.ViewedSince(Sent, Sent));
        Assert.False(InvoiceViewText.ViewedSince(Sent, Sent.AddMinutes(-5)));     // viewed before this (re)send
        Assert.False(InvoiceViewText.ViewedSince(Sent, null));
        Assert.False(InvoiceViewText.ViewedSince(null, Sent));                    // never sent

        Assert.Equal("Not viewed yet", InvoiceViewText.Describe(null, Stamp));
        Assert.Equal("Not viewed yet", InvoiceViewText.Describe(new InvoiceViewInfo(1, "INV-1001", Sent, null, null, 0), Stamp));
        Assert.Equal("Viewed Oct 10, 9:31 PM",
            InvoiceViewText.Describe(new InvoiceViewInfo(1, "INV-1001", Sent, Sent.AddMinutes(5), Sent.AddMinutes(5), 1), Stamp));
        Assert.Equal("Viewed Oct 11, 9:26 AM (3 views in all)",
            InvoiceViewText.Describe(new InvoiceViewInfo(1, "INV-1001", Sent, Sent.AddMinutes(5), Sent.AddHours(12), 3), Stamp));
        // A reminder sent after the last view: the client has not looked since.
        Assert.Equal("Not since this email (last viewed Oct 10, 9:31 PM)",
            InvoiceViewText.Describe(new InvoiceViewInfo(1, "INV-1001", Sent.AddDays(7), Sent.AddMinutes(5), Sent.AddMinutes(5), 1), Stamp));
    }

    [Fact]
    public async Task The_list_and_the_detail_read_the_invoices_own_views()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var (agentId, otherAgentId) = (await SeedAgentAsync(db), await SeedAgentAsync(db));
        var client = new Client { AgentUserId = agentId, FirstName = "Jeremy", LastName = "Kessler", Email = "jeremy@example.test" };
        db.Add(client);
        await db.SaveChangesAsync();
        var invoice = new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id, DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent,
            DocumentNumber = "INV-1001", Total = 250m, Currency = "CAD", ViewToken = Guid.NewGuid().ToString("N"),
            IssueDate = Sent.Date, DueDate = Sent.Date.AddDays(14), SentAt = Sent,
            FirstViewedAt = Sent.AddMinutes(5), LastViewedAt = Sent.AddMinutes(5), ViewCount = 1,
        };
        db.Add(invoice);
        await db.SaveChangesAsync();
        var original = new ClientInvoiceEmail
        {
            ClientInvoiceId = invoice.Id, AgentUserId = agentId, ClientId = client.Id, Kind = ClientInvoiceEmailKind.Send,
            Status = ClientInvoiceEmailStatus.Delivered, ToEmail = client.Email, Subject = "Invoice INV-1001", SentAt = Sent, DeliveredAt = Sent,
        };
        var reminder = new ClientInvoiceEmail
        {
            ClientInvoiceId = invoice.Id, AgentUserId = agentId, ClientId = client.Id, Kind = ClientInvoiceEmailKind.Reminder,
            Status = ClientInvoiceEmailStatus.Delivered, ToEmail = client.Email, Subject = "Reminder: INV-1001", SentAt = Sent.AddDays(7), DeliveredAt = Sent.AddDays(7),
        };
        db.AddRange(original, reminder);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The list: the Opened figure of an invoice row is "viewed since this email".
        var rows = await EmailActivityQueries.InvoiceRowsAsync(db, agentId);
        Assert.Equal(1, rows.Single(r => r.Id == original.Id).Opened);     // viewed five minutes after it went out
        Assert.Equal(0, rows.Single(r => r.Id == reminder.Id).Opened);     // not looked at since the reminder

        // The detail: the invoice behind the email, for its owner only.
        var view = await EmailActivityQueries.InvoiceViewAsync(db, agentId, original.Id);
        Assert.NotNull(view);
        Assert.Equal((invoice.Id, "INV-1001", 1), (view!.InvoiceId, view.DocumentNumber, view.ViewCount));
        Assert.True(view.ViewedSinceThisEmail);
        Assert.False((await EmailActivityQueries.InvoiceViewAsync(db, agentId, reminder.Id))!.ViewedSinceThisEmail);
        Assert.Null(await EmailActivityQueries.InvoiceViewAsync(db, otherAgentId, original.Id));
    }

    [Fact]
    public void The_screens_say_viewed_for_an_invoice_and_the_explanation_is_short()
    {
        var list = Read(@"src\IPRO.Web\Views\EmailActivity\Index.cshtml");
        Assert.Contains("row.Opened > 0 ? \"invoice viewed\" : \"not viewed yet\"", list);
        // Other kinds still say "not tracked" when nothing is counting, never for an invoice.
        Assert.Contains("else if (!tracking && row.Opened == 0) { <span class=\"text-muted small\">not tracked</span> }", list);

        var detail = Read(@"src\IPRO.Web\Views\EmailActivity\Details.cshtml");
        Assert.Contains("var isInvoice = send.TypeKey == \"invoice\";", detail);
        Assert.Contains("<div class=\"text-muted small text-uppercase\">Invoice viewed</div>", detail);
        Assert.Contains("@if (isInvoice) { <th>Invoice viewed</th> } else { <th>Opened</th><th>Clicked</th> }", detail);
        Assert.Contains("if (isInvoice && invoiceView?.ViewedSinceThisEmail == true) return (\"Viewed\",", detail);
        Assert.Contains("<a href=\"/portal/ClientInvoices/Details/@invoiceView.InvoiceId\">Open @invoiceView.DocumentNumber</a>", detail);
        Assert.Contains("Invoice emails do not count opens or clicks.", detail);
        // The paragraph the owner could not follow is gone; each word on the screen has its own line.
        Assert.DoesNotContain("Delivered is reported back by the mail provider.", detail);
        Assert.DoesNotContain("not switched on for this kind of mail", detail);
        Assert.Contains("<li><strong>Delivered</strong>: the receiving mail system confirmed it arrived.</li>", detail);
        Assert.Contains("<li><strong>Clicked</strong>: a link in the email was followed.</li>", detail);

        var controller = Read(@"src\IPRO.Web\Controllers\EmailActivityController.cs");
        Assert.Contains("ViewBag.InvoiceView = await IPRO.Web.Infrastructure.EmailActivityQueries.InvoiceViewAsync(_db, AgentId, id);", controller);
        Assert.Contains("the list reads **invoice viewed** or **not viewed yet**", Read(@"DOCS\23_EMAIL_ACTIVITY.md"));
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T562-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t562-{Guid.NewGuid():N}")[..20], Email = ($"t562-{Guid.NewGuid():N}")[..14] + "@example.test",
            FirstName = "Invoice", LastName = "Owner", DomainName = ($"t562-{Guid.NewGuid():N}")[..24], PackageId = rule.Id,
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
