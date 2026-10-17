using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace IPRO.IntegrationTests;

// 588 (2026-10-17). 583's security review found the new-contact form still took the whole posted record:
// 573 had emptied its relations and 583 its portal and lock columns, but the email-preferences link,
// the opt-out record, the greetings opt-in and the birthday-reminder markers still came from the
// request. A browser never sends them; a signed-in person with a crafted post can. The same sweep found
// a newsletter's sidebar buttons and send date, and a newsletter article added with whatever the request
// named around it (another newsletter, an account). Each form now builds the stored record from the
// fields a person types, and the server sets the rest.
//
// The second half: the client profile showed "Invite emailed" in Eastern time and its timeline and notes
// in the server's UTC; every time there is now in the adviser's own zone, like the rest of the portal.
public class PostedFields588Tests
{
    private const string Pacific = "(GMT-08:00) Pacific Time (US & Canada)";

    // ---------------------------------------------------------------- a new contact

    [Fact]
    public async Task A_new_contact_is_the_fields_a_person_types_and_the_server_sets_the_rest()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "creator588", "creator588@example.com");
        var otherAgent = await Lock583.SeedAgentAsync(testDb, "other588", "other588@example.com");

        // What the form sends, plus what only a crafted request sends.
        var posted = await BindAsync<Client>(new Dictionary<string, StringValues>
        {
            ["FirstName"] = " Ada ", ["LastName"] = "Lovelace", ["Email"] = "Ada@Example.com", ["Email2"] = "ada2@example.com",
            ["CompanyName"] = "Analytical Ltd", ["DateOfBirth"] = "1990-12-10", ["Phone"] = "416-555-0101",
            ["HomePhone2"] = "416-555-0102", ["BusinessPhone"] = "416-555-0103", ["BusinessPhone2"] = "416-555-0104",
            ["CellPhone"] = "416-555-0105", ["CellPhone2"] = "416-555-0106", ["Fax"] = "416-555-0107", ["Fax2"] = "416-555-0108",
            ["Address"] = "1 Engine St", ["UnitNumber"] = "4", ["City"] = "Toronto", ["Province"] = "Ontario",
            ["PostalCode"] = "M5V 1A1", ["Country"] = "Canada", ["IsNewsletterSubscribed"] = "true", ["Notes"] = "Met at the seminar",

            ["Id"] = "987654",
            ["AgentUserId"] = otherAgent.ToString(CultureInfo.InvariantCulture),
            ["EmailPreferencesToken"] = "planted-preferences-token",
            ["EmailOptOutAt"] = "2026-01-02T03:04:05",
            ["EmailOptOutSource"] = "ses:bounced:invoice",
            ["GreetingsOptInAt"] = "2026-01-03T03:04:05",
            ["LastBirthdayReminderYear"] = "2026",
            ["BirthdayReminderLastCheckedAt"] = "2026-01-04T03:04:05",
            ["CreatedAt"] = "2001-01-01T00:00:00",
            ["UpdatedAt"] = "2001-01-01T00:00:00",
            ["PortalInviteToken"] = "ffffffffffffffffffffffffffffffff",
            ["PortalActivatedAt"] = "2026-01-05T03:04:05",
            ["FailedSignInCount"] = "-1000",
            // Related records the request names (573's way in).
            ["PortalDocuments[0].FileName"] = "x.pdf", ["PortalDocuments[0].BlobUrl"] = "https://acct.example.test/portal-documents/0002bbbb_theirs.pdf",
            ["Categories[0].Name"] = "Planted", ["Categories[0].AgentUserId"] = otherAgent.ToString(CultureInfo.InvariantCulture),
            ["Comments[0].Comment"] = "Planted note",
            ["AgentUser.UserName"] = "made-up-588c", ["AgentUser.Email"] = "made-up-588c@example.test", ["AgentUser.DomainName"] = "made-up-588c.example.test",
        });
        Assert.Equal("planted-preferences-token", posted.EmailPreferencesToken);   // the binder fills whatever is named
        Assert.Single(posted.PortalDocuments);

        int accounts;
        await using (var db = testDb.CreateContext()) accounts = await db.AgentUsers.CountAsync();
        await using (var db = testDb.CreateContext())
        {
            var result = await Clients(db, agentId).Create(posted, Array.Empty<int>());
            Assert.Equal("Index", Assert.IsType<RedirectToActionResult>(result).ActionName);
        }

        await using var check = testDb.CreateContext();
        Assert.Equal(accounts, await check.AgentUsers.CountAsync());
        Assert.Equal(0, await check.PortalDocuments.CountAsync());
        Assert.Equal(0, await check.ClientCategories.CountAsync());
        Assert.Equal(0, await check.ClientComments.CountAsync());
        var row = await check.Clients.AsNoTracking().SingleAsync(c => c.Email == "ada@example.com");
        // Typed: kept, tidied the usual way.
        Assert.Equal(agentId, row.AgentUserId);
        Assert.Equal("Ada", row.FirstName);
        Assert.Equal("Lovelace", row.LastName);
        Assert.Equal("ada2@example.com", row.Email2);
        Assert.Equal("Analytical Ltd", row.CompanyName);
        Assert.Equal(new DateTime(1990, 12, 10), row.DateOfBirth);
        Assert.Equal(("416-555-0101", "416-555-0102", "416-555-0103", "416-555-0104", "416-555-0105", "416-555-0106", "416-555-0107", "416-555-0108"),
            (row.Phone, row.HomePhone2, row.BusinessPhone, row.BusinessPhone2, row.CellPhone, row.CellPhone2, row.Fax, row.Fax2));
        Assert.Equal(("1 Engine St", "4", "Toronto", "Ontario", "M5V 1A1", "Canada"),
            (row.Address, row.UnitNumber, row.City, row.Province, row.PostalCode, row.Country));
        Assert.True(row.IsNewsletterSubscribed);
        Assert.Equal("Met at the seminar", row.Notes);
        // Server-set: none of it from the request.
        Assert.NotEqual(987654, row.Id);
        Assert.Equal(string.Empty, row.EmailPreferencesToken);   // made on the first email (EmailConsentService.GetOrCreateTokenAsync)
        Assert.Null(row.EmailOptOutAt);
        Assert.Equal(string.Empty, row.EmailOptOutSource);
        Assert.Null(row.GreetingsOptInAt);
        Assert.Null(row.LastBirthdayReminderYear);
        Assert.Null(row.BirthdayReminderLastCheckedAt);
        Assert.True(row.CreatedAt > DateTime.UtcNow.AddMinutes(-10), $"CreatedAt {row.CreatedAt:o} came from the request");
        Assert.True(row.UpdatedAt > DateTime.UtcNow.AddMinutes(-10), $"UpdatedAt {row.UpdatedAt:o} came from the request");
        Assert.Null(row.PortalInviteToken);
        Assert.Null(row.PortalActivatedAt);
        Assert.Equal(0, row.FailedSignInCount);
        Assert.Equal(0, await check.Clients.CountAsync(c => c.AgentUserId == otherAgent));
    }

    [Fact]
    public async Task A_planted_preferences_token_cannot_take_over_another_contacts_unsubscribe_link()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var victimAgent = await Lock583.SeedAgentAsync(testDb, "victim588", "victim588@example.com");
        var agentId = await Lock583.SeedAgentAsync(testDb, "planter588", "planter588@example.com");
        int victimClient;
        await using (var db = testDb.CreateContext())
        {
            var client = new Client { AgentUserId = victimAgent, FirstName = "Real", LastName = "Person", Email = "real@example.com", EmailPreferencesToken = "the-real-persons-token" };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            victimClient = client.Id;
        }

        await using (var db = testDb.CreateContext())
        {
            var posted = new Client { FirstName = "Copy", LastName = "Cat", Email = "copy@example.com", Country = "Canada", EmailPreferencesToken = "the-real-persons-token" };
            Assert.IsType<RedirectToActionResult>(await Clients(db, agentId).Create(posted, Array.Empty<int>()));
        }

        // The link in the real person's inbox still finds the real person, and only them.
        await using var check = testDb.CreateContext();
        var holders = await check.Clients.AsNoTracking().Where(c => c.EmailPreferencesToken == "the-real-persons-token").Select(c => c.Id).ToListAsync();
        Assert.Equal(victimClient, Assert.Single(holders));
    }

    [Fact]
    public async Task Editing_a_contact_changes_the_fields_a_person_types_and_nothing_the_server_keeps()
    {
        // Edit already copied field by field (ApplyClientFields); this holds it there.
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "editor588", "editor588@example.com");
        var created = new DateTime(2026, 3, 1, 12, 0, 0);
        int clientId;
        await using (var db = testDb.CreateContext())
        {
            var client = new Client
            {
                AgentUserId = agentId, FirstName = "Old", LastName = "Name", Email = "kept@example.com", EmailPreferencesToken = "kept-token",
                LastBirthdayReminderYear = 2025, CreatedAt = created, UpdatedAt = created,
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            clientId = client.Id;
        }

        var posted = await BindAsync<Client>(new Dictionary<string, StringValues>
        {
            ["Id"] = clientId.ToString(CultureInfo.InvariantCulture), ["FirstName"] = "New", ["LastName"] = "Name", ["Email"] = "kept@example.com",
            ["Country"] = "Canada", ["City"] = "Ottawa",
            ["EmailPreferencesToken"] = "planted-preferences-token", ["EmailOptOutAt"] = "2026-01-02T03:04:05",
            ["EmailOptOutSource"] = "one-click", ["GreetingsOptInAt"] = "2026-01-03T03:04:05", ["LastBirthdayReminderYear"] = "2030",
            ["CreatedAt"] = "2001-01-01T00:00:00", ["PortalInviteToken"] = "ffffffffffffffffffffffffffffffff",
            ["PortalPasswordHash"] = "AQAAAAplanted", ["SignInLockedUntil"] = "2030-01-01T00:00:00",
        });
        await using (var db = testDb.CreateContext())
        {
            Assert.IsType<RedirectToActionResult>(await Clients(db, agentId).Edit(posted, Array.Empty<int>()));
        }

        await using var check = testDb.CreateContext();
        var row = await check.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId);
        Assert.Equal("New", row.FirstName);
        Assert.Equal("Ottawa", row.City);
        Assert.Equal("kept-token", row.EmailPreferencesToken);
        Assert.Null(row.EmailOptOutAt);
        Assert.Equal(string.Empty, row.EmailOptOutSource);
        Assert.Null(row.GreetingsOptInAt);
        Assert.Equal(2025, row.LastBirthdayReminderYear);
        Assert.Equal(created, row.CreatedAt);
        Assert.Null(row.PortalInviteToken);
        Assert.Null(row.PortalPasswordHash);
        Assert.Null(row.SignInLockedUntil);
    }

    // ---------------------------------------------------------------- newsletters

    [Fact]
    public async Task A_new_newsletter_is_the_fields_a_person_types_and_its_buttons_and_send_date_are_the_servers()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "writer588", "writer588@example.com");

        var posted = await BindAsync<NewsLetter>(new Dictionary<string, StringValues>
        {
            ["Subject"] = " October news ", ["Edition"] = "Fall 2026", ["TextBody"] = "Hello everyone", ["HtmlBody"] = "<p>Hello everyone</p>",
            ["BannerUrl"] = "/images/starter-banners/autumn.jpg",

            // A sidebar button is added on its own form, which takes http and https addresses only.
            ["SidebarCtasJson"] = "[{\"Label\":\"Click\",\"Url\":\"javascript:alert(document.cookie)\"}]",
            ["ScheduledAt"] = "2030-01-01T09:00:00", ["Status"] = "Sent", ["SentAt"] = "2026-01-01T00:00:00",
            ["TotalSent"] = "999", ["CreatedAt"] = "2001-01-01T00:00:00", ["Id"] = "424242",
            ["Articles[0].Title"] = "Planted article", ["Articles[0].Content"] = "<p>x</p>",
            ["AgentUser.UserName"] = "made-up-588w", ["AgentUser.Email"] = "made-up-588w@example.test", ["AgentUser.DomainName"] = "made-up-588w.example.test",
        });
        int accounts;
        await using (var db = testDb.CreateContext()) accounts = await db.AgentUsers.CountAsync();
        await using (var db = testDb.CreateContext())
        {
            var result = await Newsletters(db, agentId).Create(posted);
            Assert.Equal("Preview", Assert.IsType<RedirectToActionResult>(result).ActionName);
        }

        await using var check = testDb.CreateContext();
        Assert.Equal(accounts, await check.AgentUsers.CountAsync());
        Assert.Equal(0, await check.NewsLetterArticles.CountAsync());
        var row = await check.NewsLetters.AsNoTracking().SingleAsync(n => n.AgentUserId == agentId);
        Assert.Equal("October news", row.Subject);
        Assert.Equal("Fall 2026", row.Edition);
        Assert.Equal("Hello everyone", row.TextBody);
        Assert.Contains("Hello everyone", row.HtmlBody);
        Assert.Equal("/images/starter-banners/autumn.jpg", row.BannerUrl);
        Assert.NotEqual(424242, row.Id);
        Assert.True(string.IsNullOrEmpty(row.SidebarCtasJson), row.SidebarCtasJson);
        Assert.Null(row.ScheduledAt);
        Assert.Null(row.SentAt);
        Assert.Equal(NewsLetterStatus.Draft, row.Status);
        Assert.Equal(0, row.TotalSent);
        Assert.True(row.CreatedAt > DateTime.UtcNow.AddMinutes(-10), $"CreatedAt {row.CreatedAt:o} came from the request");
    }

    [Fact]
    public async Task An_article_added_to_a_newsletter_is_that_article_and_nothing_the_request_names_around_it()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "editor588n", "editor588n@example.com");
        var otherAgent = await Lock583.SeedAgentAsync(testDb, "other588n", "other588n@example.com");
        int ownId;
        await using (var db = testDb.CreateContext())
        {
            var own = new NewsLetter { AgentUserId = agentId, Subject = "Mine", HtmlBody = "<p>x</p>" };
            db.NewsLetters.Add(own);
            await db.SaveChangesAsync();
            ownId = own.Id;
        }
        int accounts, newsletters;
        await using (var db = testDb.CreateContext())
        {
            accounts = await db.AgentUsers.CountAsync();
            newsletters = await db.NewsLetters.CountAsync();
        }

        // One post plants a newsletter in another adviser's account; the next an account nobody signed up for.
        var intoAnotherAccount = await BindAsync<NewsLetterArticle>(new Dictionary<string, StringValues>
        {
            ["NewsLetterId"] = ownId.ToString(CultureInfo.InvariantCulture), ["Title"] = "First", ["Content"] = "<p>One</p>",
            ["ImageUrl"] = "https://images.example.test/one.jpg", ["SortOrder"] = "99",
            ["NewsLetter.AgentUserId"] = otherAgent.ToString(CultureInfo.InvariantCulture), ["NewsLetter.Subject"] = "Planted", ["NewsLetter.HtmlBody"] = "<p>Planted</p>",
        });
        Assert.NotNull(intoAnotherAccount.NewsLetter);   // the binder fills whatever is named
        var withAnAccount = await BindAsync<NewsLetterArticle>(new Dictionary<string, StringValues>
        {
            ["NewsLetterId"] = ownId.ToString(CultureInfo.InvariantCulture), ["Title"] = "Second", ["Content"] = "<p>Two</p>",
            ["NewsLetter.Subject"] = "Planted", ["NewsLetter.HtmlBody"] = "<p>Planted</p>",
            ["NewsLetter.AgentUser.UserName"] = "made-up-588", ["NewsLetter.AgentUser.Email"] = "made-up-588@example.test",
            ["NewsLetter.AgentUser.FirstName"] = "Made", ["NewsLetter.AgentUser.LastName"] = "Up",
            ["NewsLetter.AgentUser.DomainName"] = "made-up-588.example.test",
        });

        foreach (var posted in new[] { intoAnotherAccount, withAnAccount })
        {
            await using var db = testDb.CreateContext();
            var result = await Newsletters(db, agentId).AddArticle(posted);
            Assert.Equal("Edit", Assert.IsType<RedirectToActionResult>(result).ActionName);
        }

        await using var check = testDb.CreateContext();
        Assert.Equal(accounts, await check.AgentUsers.CountAsync());
        Assert.Equal(newsletters, await check.NewsLetters.CountAsync());
        Assert.Equal(0, await check.NewsLetters.CountAsync(n => n.AgentUserId == otherAgent));
        var articles = await check.NewsLetterArticles.AsNoTracking().OrderBy(a => a.SortOrder).ToListAsync();
        Assert.Equal(new[] { ("First", ownId, 0), ("Second", ownId, 1) }, articles.Select(a => (a.Title, a.NewsLetterId, a.SortOrder)));
        Assert.Equal("https://images.example.test/one.jpg", articles[0].ImageUrl);
        Assert.Contains("One", articles[0].Content);
    }

    // ---------------------------------------------------------------- every action that takes a stored record

    // An action whose parameter is a stored record gets whatever the request names. Each one below was read
    // and copies the fields a person types onto a record it made or loaded; a new one fails here until it is
    // read the same way and added. These two tests are tripwires, not proofs: they see IPRO.Web's action
    // parameters (two levels into a view model) and the posted variable stored in the action's own body,
    // not a helper that stores it or a single server-owned field copied from it. IPRO.Admin is not covered.
    private static readonly string[] Reviewed =
    {
        "ArticlesController.Create(Article)",
        "ArticlesController.Edit(Article)",
        "ClientsController.Create(Client)",
        "ClientsController.Edit(Client)",
        "NewsletterController.AddArticle(NewsLetterArticle)",
        "NewsletterController.Create(NewsLetter)",
        "NewsletterController.Edit(NewsLetter)",
        "SocialPostsController.Create(SocialPostDraft)",
        "SocialPostsController.Edit(SocialPostDraft)",
        "WebsiteController.Save(AgentWebsite)",
    };

    [Fact]
    public async Task Every_portal_action_that_takes_a_stored_record_from_the_request_has_been_read_for_this()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var stored = db.Model.GetEntityTypes().Select(e => e.ClrType).ToHashSet();

        var found = new List<string>();
        foreach (var controller in typeof(ClientsController).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() == null))
            {
                foreach (var parameter in action.GetParameters())
                {
                    var record = StoredRecordIn(parameter.ParameterType, stored, depth: 0);
                    if (record != null) found.Add($"{controller.Name}.{action.Name}({record.Name})");
                }
            }
        }

        Assert.Equal(Reviewed, found.Distinct().OrderBy(f => f, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void No_action_that_takes_a_stored_record_adds_or_updates_the_posted_one_itself()
    {
        var folder = Path.Combine(Lock583.RepoRoot(), "src", "IPRO.Web", "Controllers");
        foreach (var entry in Reviewed)
        {
            var parts = Regex.Match(entry, @"^(\w+)\.(\w+)\((\w+)\)$").Groups;
            var source = File.ReadAllText(Path.Combine(folder, parts[1].Value + ".cs"));
            var signatures = Regex.Matches(source, $@"public async Task<IActionResult> {parts[2].Value}\({parts[3].Value} (\w+)");
            Assert.True(signatures.Count > 0, $"{entry} not found");
            foreach (Match signature in signatures)
            {
                var end = source.IndexOf("\n    public ", signature.Index + 1, StringComparison.Ordinal);
                var body = source[signature.Index..(end < 0 ? source.Length : end)];
                var posted = signature.Groups[1].Value;
                Assert.False(Regex.IsMatch(body, $@"(CreateAsync|AddAsync|AddArticleAsync|AddRange|\.Add|\.Update|UpdateAsync|Attach)\({posted}\)"),
                    $"{entry} stores the posted {posted} itself; copy the typed fields onto a record the code made or loaded.");
            }
        }

        var contacts = File.ReadAllText(Path.Combine(folder, "ClientsController.cs"));
        var newsletters = File.ReadAllText(Path.Combine(folder, "NewsletterController.cs"));
        Assert.Contains("var client = NewContactFrom(model);", contacts);
        Assert.Contains("await _db.Clients.AddAsync(client);", contacts);
        Assert.Contains("var newsletter = new NewsLetter", newsletters);
        Assert.Contains("await _newsletters.AddArticleAsync(new NewsLetterArticle", newsletters);
    }

    // ---------------------------------------------------------------- the client profile's times

    [Fact]
    public async Task The_client_profile_shows_every_time_in_the_advisers_own_zone()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "vancouver588", "vancouver588@example.com");
        int clientId;
        await using (var db = testDb.CreateContext())
        {
            var agent = await db.AgentUsers.SingleAsync(a => a.Id == agentId);
            agent.TimeZone = Pacific;
            // Added at 7:10 p.m. on July 15 in Vancouver: already July 16 in UTC.
            var added = new DateTime(2026, 7, 16, 2, 10, 0, DateTimeKind.Utc);
            var client = new Client
            {
                AgentUserId = agentId, FirstName = "Wes", LastName = "Coast", Email = "wes@example.com", CreatedAt = added, UpdatedAt = added,
                PortalInviteToken = "0123456789abcdef0123456789abcdef", PortalInviteTokenExpiresAt = added.AddDays(7),
                PortalInviteEmailedAt = new DateTime(2026, 7, 15, 18, 30, 0, DateTimeKind.Utc),   // 11:30 a.m. in Vancouver, 2:30 p.m. in Toronto
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            clientId = client.Id;
            db.ClientComments.Add(new ClientComment { ClientId = clientId, Comment = "Called about RRSP", CreatedAt = new DateTime(2026, 7, 15, 19, 45, 0, DateTimeKind.Utc) });
            db.ClientFollowUps.Add(new ClientFollowUp
            {
                ClientId = clientId, Title = "Send the forms", DueAt = new DateTime(2026, 7, 20), IsCompleted = true,
                CreatedAt = new DateTime(2026, 7, 15, 16, 0, 0, DateTimeKind.Utc),
                CompletedAt = new DateTime(2026, 7, 16, 3, 0, 0, DateTimeKind.Utc),   // 8 p.m. on July 15 in Vancouver
            });
            await db.SaveChangesAsync();
        }

        string html;
        await using (var db = testDb.CreateContext())
        {
            var controller = Clients(db, agentId);
            var view = Assert.IsType<ViewResult>(await controller.Details(clientId));
            html = await RenderAsync("~/Views/Clients/Details.cshtml", view.ViewData, controller.HttpContext.User);
        }

        static string At(int day, int hour, int minute) => new DateTime(2026, 7, day, hour, minute, 0).ToString("MMM d, yyyy h:mm tt");
        static string On(int day) => new DateTime(2026, 7, day).ToString("MMM d, yyyy");

        Assert.Contains("Invite emailed " + At(15, 11, 30), html);
        Assert.DoesNotContain(At(15, 14, 30), html);                           // Eastern
        Assert.Contains(At(15, 12, 45), html);                                 // the note, in the list and on the timeline
        Assert.DoesNotContain(At(15, 19, 45), html);                           // UTC
        Assert.Contains(At(15, 19, 10), html);                                 // "Client created" on the timeline
        Assert.Contains(At(15, 9, 0), html);                                   // "Follow-up added"
        Assert.Contains(At(15, 20, 0), html);                                  // "Follow-up completed"
        Assert.Contains("Completed on " + On(15), html);
        Assert.DoesNotContain(On(16), html);                                   // no UTC date anywhere on the page
        Assert.Matches(new Regex("Added</dt>\\s*<dd class=\"col-7\">" + Regex.Escape(On(15)) + "</dd>"), html);
        Assert.Contains("Due " + new DateTime(2026, 7, 20).ToString("MMM d, yyyy"), html);   // a due date is the adviser's own day already
    }

    [Fact]
    public async Task In_winter_too_the_profile_the_client_list_the_edit_form_and_the_follow_ups_give_the_advisers_own_day()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        var agentId = await Lock583.SeedAgentAsync(testDb, "winter588", "winter588@example.com");
        int clientId;
        await using (var db = testDb.CreateContext())
        {
            (await db.AgentUsers.SingleAsync(a => a.Id == agentId)).TimeZone = Pacific;
            // Every stamp is late on January 15 in Vancouver (standard time, UTC-8) and already January 16 in Toronto
            // and in UTC, so a page that fell back to Eastern, or printed UTC, shows the wrong day.
            var client = new Client
            {
                AgentUserId = agentId, FirstName = "Win", LastName = "Ter", Email = "win@example.com",
                CreatedAt = new DateTime(2026, 1, 16, 6, 30, 0, DateTimeKind.Utc), UpdatedAt = new DateTime(2026, 1, 16, 6, 30, 0, DateTimeKind.Utc),
                EmailOptOutAt = new DateTime(2026, 1, 16, 6, 0, 0, DateTimeKind.Utc), EmailOptOutSource = "one-click",
                PortalActivatedAt = new DateTime(2026, 1, 16, 6, 15, 0, DateTimeKind.Utc),
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            clientId = client.Id;
            db.TestimonialSubmissions.Add(new TestimonialSubmission
            {
                AgentUserId = agentId, ClientId = clientId, FirstName = "Win", LastName = "Ter", Email = "win@example.com",
                Status = TestimonialStatus.Pending, SubmittedAt = new DateTime(2026, 1, 16, 7, 0, 0, DateTimeKind.Utc),
            });
            db.ClientComments.Add(new ClientComment { ClientId = clientId, Comment = "Winter call", CreatedAt = new DateTime(2026, 1, 15, 21, 0, 0, DateTimeKind.Utc) });
            db.ClientFollowUps.Add(new ClientFollowUp { ClientId = clientId, Title = "Year-end", DueAt = new DateTime(2026, 2, 1), CreatedAt = new DateTime(2026, 1, 16, 6, 45, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
        }

        var pages = new Dictionary<string, string>();
        // A fresh controller for each page: one keeps its ViewBag, which would hide a page that sets no zone.
        foreach (var (name, view, action) in new (string, string, Func<ClientsController, Task<IActionResult>>)[]
        {
            ("profile", "Details", c => c.Details(clientId)),
            ("list", "Index", c => c.Index(null, null, null)),
            ("edit", "Edit", c => c.Edit(clientId)),
            ("follow-ups", "FollowUps", c => c.FollowUps(clientId)),
        })
        {
            await using var db = testDb.CreateContext();
            var controller = Clients(db, agentId);
            pages[name] = await RenderAsync($"~/Views/Clients/{view}.cshtml", Assert.IsType<ViewResult>(await action(controller)).ViewData, controller.HttpContext.User);
        }

        var day = new DateTime(2026, 1, 15).ToString("MMM d, yyyy");
        foreach (var (name, html) in pages)
        {
            Assert.True(html.Contains(day, StringComparison.Ordinal), $"{name}: {day} not shown");
            Assert.False(html.Contains(new DateTime(2026, 1, 16).ToString("MMM d, yyyy"), StringComparison.Ordinal), $"{name} shows the UTC day");
        }
        Assert.Contains(day + " &middot;", pages["profile"]);                  // the unsubscribe line
        Assert.Contains("Active since " + day, pages["profile"]);
        Assert.Contains("Awaiting response, sent " + day, pages["profile"]);
        Assert.Contains(new DateTime(2026, 1, 15, 13, 0, 0).ToString("MMM d, yyyy h:mm tt"), pages["profile"]);    // the note: 1 p.m., not Toronto's 4 p.m.
        Assert.DoesNotContain(new DateTime(2026, 1, 15, 16, 0, 0).ToString("MMM d, yyyy h:mm tt"), pages["profile"]);
        Assert.Contains("unsubscribed from all email on " + day, pages["edit"]);
    }

    // ---------------------------------------------------------------- harness

    private static ClientsController Clients(IPRODbContext db, int agentId) =>
        Lock583.Prepare(new ClientsController(new ClientService(new UnitOfWork(db), db), null!, new UnitOfWork(db), db, new Lock583.GrantAll(), null!, null!, null!,
                new EphemeralDataProtectionProvider(), Lock583.Config, Consent(db)),
            Lock583.User(new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture))));

    private static NewsletterController Newsletters(IPRODbContext db, int agentId)
    {
        var uow = new UnitOfWork(db);
        return Lock583.Prepare(new NewsletterController(new NewsLetterService(uow, Consent(db), db), new ClientService(uow, db), new Lock583.GrantAll(), uow, db,
                null!, null!, null!, null!, Consent(db), Lock583.Config, NullLogger<NewsletterController>.Instance),
            Lock583.User(new Claim(ClaimTypes.NameIdentifier, agentId.ToString(CultureInfo.InvariantCulture))));
    }

    private static EmailConsentService Consent(IPRODbContext db) =>
        new(db, new ConfigurationBuilder().Build(), NullLogger<EmailConsentService>.Instance, Array.Empty<IUnsubscribeNotifier>());

    // A stored record, or a view model carrying one, that a request can fill.
    private static Type? StoredRecordIn(Type type, HashSet<Type> stored, int depth)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray) type = type.GetElementType()!;
        else if (type.IsGenericType && type.GetGenericArguments().Length == 1 && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            type = type.GetGenericArguments()[0];
        if (stored.Contains(type)) return type;
        if (depth >= 2 || type.IsPrimitive || type.IsEnum || type == typeof(string) || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("Microsoft", StringComparison.Ordinal) == true)
            return null;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.GetSetMethod() != null))
        {
            var inner = StoredRecordIn(property.PropertyType, stored, depth + 1);
            if (inner != null) return inner;
        }
        return null;
    }

    // The request binder exactly as an action's parameter gets it (as in PostedRecord573Tests).
    private static async Task<T> BindAsync<T>(Dictionary<string, StringValues> form) where T : class
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithViews();
        await using var provider = services.BuildServiceProvider();
        var metadata = provider.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(T));
        var binder = provider.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext { Metadata = metadata });
        var action = new ActionContext(new DefaultHttpContext { RequestServices = provider }, new RouteData(), new ActionDescriptor());
        var values = new FormValueProvider(BindingSource.Form, new FormCollection(form), CultureInfo.InvariantCulture);
        var parameter = new ParameterDescriptor { Name = "model", ParameterType = typeof(T) };
        var result = await provider.GetRequiredService<ParameterBinder>().BindModelAsync(action, binder, values, parameter, metadata, value: null);
        Assert.True(result.IsModelSet);
        return (T)result.Model!;
    }

    // The portal's own compiled view, rendered with what the action handed it (no layout).
    private static async Task<string> RenderAsync(string viewPath, ViewDataDictionary viewData, ClaimsPrincipal user)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions
        {
            ApplicationName = typeof(ClientsController).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(Lock583.RepoRoot(), "src", "IPRO.Web"),
            EnvironmentName = "Production"
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(ClientsController).Assembly);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = user };
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "render"));
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var engine = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Mvc.Razor.IRazorViewEngine>();
        var found = engine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        Assert.True(found.Success, $"{viewPath} was not found among the portal's compiled views");
        await using var writer = new StringWriter();
        await found.View!.RenderAsync(new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(
            actionContext, found.View, viewData, new TempDataDictionary(http, new NoTempData()), writer, new Microsoft.AspNetCore.Mvc.ViewFeatures.HtmlHelperOptions()));
        return writer.ToString();
    }

    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
