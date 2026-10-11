using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 565 (2026-10-11): the Email Activity list a person can use. It showed every email ever sent on one
// page; two months in, the owner scrolled back to August and said "if there is few emails a day and
// for 12 months it will be pages and pages and hard to manage". Agreed with him the same day: the
// list opens on the last 30 days, 25 to a page, with a tab for what needs attention and a search by
// subject or recipient. Nothing is deleted: older mail is one click away.
public class EmailActivityList565Tests
{
    private static readonly DateTime Now = new(2026, 10, 11, 20, 0, 0, DateTimeKind.Utc);

    private static EmailActivityRow Row(int id, int daysAgo, string type = "ecard", string subject = "Happy Birthday", string detail = "simple-birthday",
        string status = "Sent", int failed = 0, bool undated = false) =>
        new(type == "invoice" ? "Invoice" : "E-Card", type, id, subject, detail, status, undated ? null : Now.AddDays(-daysAgo), 1, 1, 1, 0, failed);

    private static EmailActivityListing List(IEnumerable<EmailActivityRow> rows, string? type = null, string? period = null, string? q = null, int page = 1,
        ISet<(string, int)>? reached = null)
    {
        var chosen = EmailActivityListing.PeriodFor(period);
        return EmailActivityListing.Build(EmailActivityListing.InPeriod(rows, EmailActivityListing.Since(chosen, Now)), type, chosen, q, page, reached);
    }

    // ---- the period ----------------------------------------------------------------------------------

    [Fact]
    public void The_list_opens_on_the_last_30_days_and_older_mail_is_one_click_away()
    {
        Assert.Equal(new[] { "7", "30", "90", "365", "all" }, EmailActivityListing.Periods.Select(p => p.Key).ToArray());
        Assert.Equal(new[] { "7 days", "30 days", "90 days", "12 months", "All" }, EmailActivityListing.Periods.Select(p => p.Label).ToArray());
        Assert.Equal("30", EmailActivityListing.PeriodFor(null).Key);
        Assert.Equal("30", EmailActivityListing.PeriodFor("forever").Key);       // an address someone typed by hand
        Assert.Equal("all", EmailActivityListing.PeriodFor(" ALL ").Key);
        Assert.Equal(Now.AddDays(-30), EmailActivityListing.Since(EmailActivityListing.PeriodFor("30"), Now));
        Assert.Null(EmailActivityListing.Since(EmailActivityListing.PeriodFor("all"), Now));

        var rows = new[] { Row(1, 2), Row(2, 29), Row(3, 31), Row(4, 80), Row(5, 200), Row(6, 400) };
        Assert.Equal(new[] { 1, 2 }, List(rows).Rows.Select(r => r.Id).ToArray());
        Assert.Equal(new[] { 1 }, List(rows, period: "7").Rows.Select(r => r.Id).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4 }, List(rows, period: "90").Rows.Select(r => r.Id).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, List(rows, period: "365").Rows.Select(r => r.Id).ToArray());
        Assert.Equal(6, List(rows, period: "all").Total);                         // nothing is ever dropped
        Assert.Equal("the last 30 days", List(rows).Period.Words);
    }

    [Fact]
    public void An_email_with_no_date_yet_or_one_scheduled_for_later_is_not_old()
    {
        var rows = new[] { Row(1, 100), Row(2, 0, undated: true), Row(3, -5, status: "Scheduled") };
        Assert.Equal(new[] { 3, 2 }, List(rows, period: "7").Rows.Select(r => r.Id).ToArray());   // newest first; the undated one last
    }

    // ---- 25 to a page --------------------------------------------------------------------------------

    [Fact]
    public void The_list_is_cut_into_pages_of_25_newest_first()
    {
        var rows = Enumerable.Range(1, 60).Select(i => Row(i, 0, subject: $"Card {i}") with { When = Now.AddMinutes(-i) }).Reverse().ToList();

        var first = List(rows);
        Assert.Equal((25, 60, 1, 3, 1, 25), (first.Rows.Count, first.Total, first.Page, first.PageCount, first.From, first.To));
        Assert.Equal(Enumerable.Range(1, 25), first.Rows.Select(r => r.Id));

        var last = List(rows, page: 3);
        Assert.Equal((10, 3, 51, 60), (last.Rows.Count, last.Page, last.From, last.To));

        Assert.Equal(3, List(rows, page: 99).Page);                               // a page past the end is the last page
        Assert.Equal(1, List(rows, page: -4).Page);
        var none = List(Array.Empty<EmailActivityRow>());
        Assert.Equal((0, 1, 1, 0, 0), (none.Total, none.Page, none.PageCount, none.From, none.To));
    }

    // ---- needs attention -----------------------------------------------------------------------------

    [Fact]
    public void A_tab_holds_what_failed_or_bounced_and_the_badges_do_not_change_with_the_tab()
    {
        var rows = new[]
        {
            Row(1, 1), Row(2, 2, failed: 1), Row(3, 3, type: "invoice", subject: "Invoice INV-1001", failed: 1),
            Row(4, 4, status: "Failed"), Row(5, 50, failed: 3),
        };

        var all = List(rows);
        Assert.Equal((4, 3), (all.AllCount, all.AttentionCount));                 // the one from 50 days ago is outside the period
        Assert.Equal(3, all.CountFor("ecard"));
        Assert.Equal(1, all.CountFor("invoice"));
        Assert.Equal(0, all.CountFor("poll"));

        var attention = List(rows, type: EmailActivityListing.Attention);
        Assert.Equal(new[] { 2, 3, 4 }, attention.Rows.Select(r => r.Id).ToArray());
        Assert.Equal((4, 3, 3), (attention.AllCount, attention.AttentionCount, attention.CountFor("ecard")));   // as on the Everything tab

        Assert.Equal(4, List(rows, type: EmailActivityListing.Attention, period: "90").Total);
        Assert.Equal("all", List(rows, type: "<script>").Type);                   // an unknown tab is Everything
    }

    // ---- the search ----------------------------------------------------------------------------------

    [Fact]
    public void The_search_reads_the_subject_the_line_under_it_and_who_received_the_email()
    {
        var rows = new[]
        {
            Row(1, 1, subject: "Happy Birthday"),
            Row(2, 2, subject: "Welcome aboard", detail: "welcome"),
            Row(3, 3, type: "invoice", subject: "Invoice INV-1001", detail: "INV-1001 - Dana Smith - dana@example.test"),
            Row(4, 4, subject: "Thank You", detail: "simple-thank-you"),
        };
        var reached = new HashSet<(string, int)> { ("ecard", 4) };                // card 4 went to someone called Smith

        Assert.Equal(new[] { 1 }, List(rows, q: "birthday").Rows.Select(r => r.Id).ToArray());          // any case
        Assert.Equal(new[] { 2 }, List(rows, q: "  WELCOME ").Rows.Select(r => r.Id).ToArray());
        var smith = List(rows, q: "smith", reached: reached);
        Assert.Equal(new[] { 3, 4 }, smith.Rows.Select(r => r.Id).ToArray());     // the invoice by its own line, the card by its recipient
        Assert.Equal((2, 1, 1, "smith"), (smith.AllCount, smith.CountFor("ecard"), smith.CountFor("invoice"), smith.Query));   // the badges follow the search
        Assert.Empty(List(rows, q: "smith", type: "newsletter", reached: reached).Rows);

        Assert.Equal(string.Empty, EmailActivityListing.CleanQuery(" a "));       // one letter would match almost everything
        Assert.Equal("dana smith", EmailActivityListing.CleanQuery("  dana \t smith  "));
        Assert.Equal(80, EmailActivityListing.CleanQuery(new string('x', 300)).Length);
        Assert.Equal(4, List(rows, q: "a").Total);                                // so it is no search at all
    }

    // ---- the addresses -------------------------------------------------------------------------------

    [Fact]
    public void The_lists_address_leaves_out_what_is_at_its_default_and_a_change_starts_at_page_one()
    {
        var rows = Enumerable.Range(1, 60).Select(i => Row(i, 1)).ToList();
        Assert.Equal("/portal/EmailActivity", List(rows).Url());
        Assert.Equal("/portal/EmailActivity?page=2", List(rows).Url(page: 2));

        var deep = List(rows, type: "ecard", period: "90", q: "dana & co", page: 2, reached: new HashSet<(string, int)>(rows.Select(r => ("ecard", r.Id))));
        Assert.Equal("/portal/EmailActivity?type=ecard&period=90&q=dana%20%26%20co&page=2", deep.Url(page: deep.Page));
        Assert.Equal("/portal/EmailActivity?type=ecard&period=90&q=dana%20%26%20co", deep.Url());                 // the same list, first page
        Assert.Equal("/portal/EmailActivity?period=90&q=dana%20%26%20co", deep.Url(type: "all"));
        Assert.Equal("/portal/EmailActivity?type=ecard&q=dana%20%26%20co", deep.Url(period: "30"));
        Assert.Equal("/portal/EmailActivity?type=ecard&period=90", deep.Url(query: string.Empty));
    }

    [Fact]
    public void Back_on_the_detail_page_returns_to_the_list_as_it_was_left_and_echoes_nothing_it_was_handed()
    {
        const string host = "app.example.test";
        Assert.Equal("/portal/EmailActivity?type=attention&period=90&q=smith&page=3",
            EmailActivityListing.BackAddress("https://app.example.test/portal/EmailActivity?type=attention&period=90&q=smith&page=3", host));
        Assert.Equal("/portal/EmailActivity", EmailActivityListing.BackAddress("https://app.example.test/portal/EmailActivity", host));

        // Another site, another screen, or no address at all: the plain list.
        Assert.Equal("/portal/EmailActivity", EmailActivityListing.BackAddress("https://elsewhere.example/portal/EmailActivity?period=all", host));
        Assert.Equal("/portal/EmailActivity", EmailActivityListing.BackAddress("https://app.example.test/portal/Clients?period=all", host));
        Assert.Equal("/portal/EmailActivity", EmailActivityListing.BackAddress(null, host));
        Assert.Equal("/portal/EmailActivity", EmailActivityListing.BackAddress("not an address", host));

        // Whatever the address carried is read as the four known values and cleaned; nothing is copied through.
        var cleaned = EmailActivityListing.BackAddress("https://app.example.test/portal/EmailActivity?type=%22%3E%3Cscript%3E&period=9999&page=-7&q=%3Cb%3E&next=https://evil.example", host);
        Assert.Equal("/portal/EmailActivity?q=%3Cb%3E", cleaned);
    }

    // ---- against MySQL -------------------------------------------------------------------------------

    [Fact]
    public async Task The_screen_shows_the_advisers_recent_mail_and_finds_an_email_by_its_recipient()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var otherId = await SeedAgentAsync(db);
        var now = DateTime.UtcNow;

        var recent = await SeedCardAsync(db, agentId, "Happy Birthday", now.AddDays(-3), "Dana Smith", "dana@example.test");
        var old = await SeedCardAsync(db, agentId, "Happy New Year", now.AddDays(-45), "Old Timer", "old@example.test");
        var failed = await SeedCardAsync(db, agentId, "Thank You", now.AddDays(-2), "Bounce Back", "gone@example.test", failed: true);
        var theirs = await SeedCardAsync(db, otherId, "Happy Birthday", now.AddDays(-1), "Dana Smith", "dana@example.test");
        var invoiceEmail = await SeedInvoiceEmailAsync(db, agentId, "Ivy", "Ledger", now.AddDays(-4));
        var oldInvoiceEmail = await SeedInvoiceEmailAsync(db, agentId, "Ancient", "Bill", now.AddDays(-200));
        db.ChangeTracker.Clear();

        // The plain screen: the last 30 days, this adviser's own.
        var (rows, listing) = await IndexAsync(db, agentId);
        Assert.Equal(new[] { ("ecard", failed), ("ecard", recent), ("invoice", invoiceEmail) }, rows.Select(r => (r.TypeKey, r.Id)).ToArray());
        Assert.Equal((3, 1, "30"), (listing.AllCount, listing.AttentionCount, listing.Period.Key));
        Assert.DoesNotContain(rows, r => r.TypeKey == "ecard" && r.Id == theirs);        // another adviser's card to the same person

        // Wider periods bring the older mail back.
        Assert.Contains((await IndexAsync(db, agentId, period: "90")).Rows, r => r.TypeKey == "ecard" && r.Id == old);
        Assert.DoesNotContain((await IndexAsync(db, agentId, period: "90")).Rows, r => r.TypeKey == "invoice" && r.Id == oldInvoiceEmail);
        Assert.Equal(5, (await IndexAsync(db, agentId, period: "all")).Listing.Total);

        // What needs attention.
        Assert.Equal(new[] { failed }, (await IndexAsync(db, agentId, type: "attention")).Rows.Select(r => r.Id).ToArray());

        // By recipient: a name, part of an address; an invoice by its client. Never another adviser's mail.
        Assert.Equal(new[] { ("ecard", recent) }, (await IndexAsync(db, agentId, q: "smith")).Rows.Select(r => (r.TypeKey, r.Id)).ToArray());
        Assert.Equal(new[] { ("ecard", recent) }, (await IndexAsync(db, agentId, q: "Dana Smith")).Rows.Select(r => (r.TypeKey, r.Id)).ToArray());
        Assert.Equal(new[] { ("ecard", failed) }, (await IndexAsync(db, agentId, q: "gone@example")).Rows.Select(r => (r.TypeKey, r.Id)).ToArray());
        Assert.Equal(new[] { ("invoice", invoiceEmail) }, (await IndexAsync(db, agentId, q: "ledger")).Rows.Select(r => (r.TypeKey, r.Id)).ToArray());
        Assert.Empty((await IndexAsync(db, agentId, q: "old timer")).Rows);                       // outside the 30 days
        Assert.Equal(new[] { old }, (await IndexAsync(db, agentId, q: "old timer", period: "all")).Rows.Select(r => r.Id).ToArray());
        Assert.Equal(new[] { ("ecard", theirs) }, (await IndexAsync(db, otherId, q: "smith")).Rows.Select(r => (r.TypeKey, r.Id)).ToArray());

        // The detail page still opens a send from before the period, and Back knows where the reader came from.
        var controller = NewController(db, agentId);
        controller.Request.Headers.Referer = "https://app.example.test/portal/EmailActivity?period=90&page=2";
        controller.Request.Host = new HostString("app.example.test");
        Assert.IsType<ViewResult>(await controller.Details("ecard", old));
        Assert.Equal("/portal/EmailActivity?period=90&page=2", (string)controller.ViewBag.BackAddress);
        Assert.IsType<NotFoundResult>(await NewController(db, agentId).Details("ecard", theirs));
    }

    [Fact]
    public async Task Thirty_cards_in_a_month_are_two_pages()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var now = DateTime.UtcNow;
        for (var i = 1; i <= 30; i++)
            await SeedCardAsync(db, agentId, $"Card {i}", now.AddHours(-i), $"Reader {i}", $"reader{i}@example.test");
        db.ChangeTracker.Clear();

        var first = await IndexAsync(db, agentId);
        Assert.Equal((25, 30, 2), (first.Rows.Count, first.Listing.Total, first.Listing.PageCount));
        Assert.Equal("Card 1", first.Rows[0].Subject);
        var second = await IndexAsync(db, agentId, page: 2);
        Assert.Equal((5, 26, 30), (second.Rows.Count, second.Listing.From, second.Listing.To));
        Assert.Equal("Card 30", second.Rows[^1].Subject);
    }

    // ---- what the screen says ------------------------------------------------------------------------

    [Fact]
    public void The_screen_has_the_period_the_tab_the_search_and_the_pages()
    {
        var list = Read(@"src\IPRO.Web\Views\EmailActivity\Index.cshtml");
        Assert.Contains("@foreach (var p in EmailActivityListing.Periods)", list);
        Assert.Contains("href=\"@listing.Url(period: p.Key)\">@p.Label</a>", list);
        Assert.Contains("(EmailActivityListing.Attention, \"Needs attention\"),", list);
        Assert.Contains("<input type=\"search\" name=\"q\" value=\"@listing.Query\"", list);
        Assert.Contains("href=\"@listing.Url(page: listing.Page + 1)\"", list);
        Assert.Contains("Showing @listing.From&ndash;@listing.To of @listing.Total from @listing.Period.Words", list);
        Assert.Contains("<h5 class=\"fw-bold\">Nothing needs attention</h5>", list);
        Assert.DoesNotContain("href=\"/portal/EmailActivity?type=@key\"", list);     // the old tab link dropped the period and the search

        var controller = Read(@"src\IPRO.Web\Controllers\EmailActivityController.cs");
        Assert.Contains("public async Task<IActionResult> Index(string type = \"all\", string period = EmailActivityListing.DefaultPeriod, string? q = null, int page = 1)", controller);
        // The detail page looks a send up whatever its age: no period there.
        Assert.Contains("var send = (await LoadSendsAsync()).FirstOrDefault(r => r.TypeKey == normalizedType && r.Id == id);", controller);

        Assert.Contains("<a href=\"@(ViewBag.BackAddress as string ?? \"/portal/EmailActivity\")\"", Read(@"src\IPRO.Web\Views\EmailActivity\Details.cshtml"));
        Assert.Contains("## Finding an Email", Read(@"DOCS\23_EMAIL_ACTIVITY.md"));
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static async Task<(List<EmailActivityRow> Rows, EmailActivityListing Listing)> IndexAsync(IPRODbContext db, int agentId,
        string type = "all", string period = EmailActivityListing.DefaultPeriod, string? q = null, int page = 1)
    {
        var controller = NewController(db, agentId);
        var view = Assert.IsType<ViewResult>(await controller.Index(type, period, q, page));
        var rows = Assert.IsAssignableFrom<IEnumerable<EmailActivityRow>>(view.Model).ToList();
        return (rows, Assert.IsType<EmailActivityListing>((object)controller.ViewBag.Listing));
    }

    private static EmailActivityController NewController(IPRODbContext db, int agentId)
    {
        var controller = new EmailActivityController(db, Options.Create(new EmailSettings()));
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return controller;
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T565-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t565-{Guid.NewGuid():N}")[..20],
            Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "List",
            LastName = "Reader",
            CompanyName = "Activity Co",
            DomainName = ($"t565-{Guid.NewGuid():N}")[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<int> SeedCardAsync(IPRODbContext db, int agentId, string subject, DateTime sentAt, string recipientName, string recipientEmail, bool failed = false)
    {
        var client = new Client { AgentUserId = agentId, FirstName = recipientName.Split(' ')[0], LastName = recipientName.Split(' ')[^1], Email = $"{Guid.NewGuid():N}@example.test" };
        db.Add(client);
        var card = new ECard
        {
            AgentUserId = agentId, Occasion = "simple-birthday", Subject = subject, Status = ECardStatuses.Sent,
            ScheduledAt = sentAt, SentAt = sentAt, TotalRecipients = 1, TotalSent = failed ? 0 : 1
        };
        db.Add(card);
        await db.SaveChangesAsync();
        db.Add(new ECardRecipient
        {
            ECardId = card.Id, ClientId = client.Id, Email = recipientEmail, RecipientName = recipientName,
            Status = failed ? ECardRecipientStatuses.Failed : ECardRecipientStatuses.Sent,
            SentAt = failed ? null : sentAt, DeliveredAt = failed ? null : sentAt
        });
        await db.SaveChangesAsync();
        return card.Id;
    }

    private static async Task<int> SeedInvoiceEmailAsync(IPRODbContext db, int agentId, string first, string last, DateTime sentAt)
    {
        var client = new Client { AgentUserId = agentId, FirstName = first, LastName = last, Email = $"{first.ToLowerInvariant()}@example.test" };
        db.Add(client);
        await db.SaveChangesAsync();
        var invoice = new ClientInvoice
        {
            AgentUserId = agentId, ClientId = client.Id, DocumentType = ClientInvoiceDocumentType.Invoice, Status = ClientInvoiceStatus.Sent,
            DocumentNumber = $"INV-{Guid.NewGuid():N}"[..12], Total = 250m, Currency = "CAD", ViewToken = Guid.NewGuid().ToString("N"),
            IssueDate = sentAt.Date, DueDate = sentAt.Date.AddDays(14), SentAt = sentAt
        };
        db.Add(invoice);
        await db.SaveChangesAsync();
        var email = new ClientInvoiceEmail
        {
            ClientInvoiceId = invoice.Id, AgentUserId = agentId, ClientId = client.Id, Kind = ClientInvoiceEmailKind.Send,
            Status = ClientInvoiceEmailStatus.Delivered, ToEmail = client.Email, Subject = "Invoice " + invoice.DocumentNumber, SentAt = sentAt, DeliveredAt = sentAt
        };
        db.Add(email);
        await db.SaveChangesAsync();
        return email.Id;
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
