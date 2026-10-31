using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// 2026-10-31, from the full review of the to-do file: promises the product made and did not quite keep.
//  624  an opt-out outlives the contact: delete someone who unsubscribed, add them again, and they stay out.
//  625  client invoices know "Yukon Territory", which is what the sign-up and profile forms store.
//  626  the truth sweep's leftovers: examples in the adviser's own words, exports open to a limited account,
//       the setup fee on the Billing cards, labels on invented content, three stale library lines.
//  627  neither app takes the committed placeholder for the preview secret.
// 623 is in GoogleCalendarDisconnectTests, 622 in BuilderBeta585Tests.
public class FirstBatch624To627Tests
{
    [Fact]
    public async Task Someone_who_opted_out_and_was_deleted_stays_out_when_added_again()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var otherAgentId = await SeedAgentAsync(db);
        var optedOutAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var client = await SeedClientAsync(db, agentId, "john.smith@gmail.com", optedOutAt, "link:unsubscribe");

        await ContactOptOuts.RememberAsync(db, client.Id);
        await ClientRecycleBin.DeleteToBinAsync(db, client.Id);
        db.ChangeTracker.Clear();

        // The same mailbox, typed the way Gmail treats as the same, with the newsletter box ticked.
        var again = new Client { AgentUserId = agentId, Email = "johnsmith+crm@gmail.com", IsNewsletterSubscribed = true };
        Assert.True(ContactOptOuts.Apply(again, await ContactOptOuts.ForAgentAsync(db, agentId)));
        Assert.Equal(optedOutAt, again.EmailOptOutAt);
        Assert.Equal("link:unsubscribe", again.EmailOptOutSource);
        Assert.False(again.IsNewsletterSubscribed);

        // Another adviser's contact with that address is another relationship.
        var elsewhere = new Client { AgentUserId = otherAgentId, Email = "john.smith@gmail.com", IsNewsletterSubscribed = true };
        Assert.False(ContactOptOuts.Apply(elsewhere, await ContactOptOuts.ForAgentAsync(db, otherAgentId)));
        Assert.True(elsewhere.IsNewsletterSubscribed);

        // Nothing readable is kept.
        var row = await db.ContactEmailSuppressions.SingleAsync();
        Assert.Matches("^[0-9a-f]{64}$", row.EmailHash);
    }

    [Fact]
    public async Task Someone_who_came_back_before_being_deleted_is_not_held_to_the_old_word()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var email = $"{Guid.NewGuid():N}@example.test";
        db.ContactEmailSuppressions.Add(new ContactEmailSuppression
        {
            AgentUserId = agentId, EmailHash = ContactOptOuts.Hash(agentId, email), OptedOutAt = DateTime.UtcNow.AddDays(-90), Source = "link"
        });
        await db.SaveChangesAsync();
        var client = await SeedClientAsync(db, agentId, email, null, string.Empty);

        await ContactOptOuts.RememberAsync(db, client.Id);

        db.ChangeTracker.Clear();
        Assert.False(await db.ContactEmailSuppressions.AnyAsync());
    }

    [Fact]
    public async Task A_contact_deleted_before_this_was_built_is_read_from_its_snapshot()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var agentId = await SeedAgentAsync(db);
        var email = $"{Guid.NewGuid():N}@example.test";
        var optedOutAt = new DateTime(2026, 9, 2, 8, 30, 0, DateTimeKind.Utc);
        var client = await SeedClientAsync(db, agentId, email, optedOutAt, "bounced:hard");
        var subscribed = await SeedClientAsync(db, agentId, $"{Guid.NewGuid():N}@example.test", null, string.Empty);

        await ClientRecycleBin.DeleteToBinAsync(db, client.Id);
        await ClientRecycleBin.DeleteToBinAsync(db, subscribed.Id);
        db.ChangeTracker.Clear();

        var kept = await ContactOptOuts.ForAgentAsync(db, agentId);
        var entry = Assert.Single(kept);
        Assert.Equal(ContactOptOuts.Hash(agentId, email), entry.Key);
        Assert.Equal(optedOutAt, entry.Value.OptedOutAt);
        Assert.Equal("bounced:hard", entry.Value.Source);
    }

    [Fact]
    public void Adding_by_hand_importing_and_deleting_all_go_through_the_kept_opt_outs()
    {
        var source = Read("src/IPRO.Web/Controllers/ClientsController.cs");
        Assert.Contains("ContactOptOuts.Apply(client, await IPRO.Business.Services.ContactOptOuts.ForAgentAsync(_db, AgentId))", source);
        Assert.Contains("ContactOptOuts.Apply(c, keptOptOuts)", source);
        Assert.Contains("ContactOptOuts.RememberAsync(_db, id)", source);

        // INVARIANTS rule 7: EmailOptOutAt has one writer, and this is not a second one.
        Assert.DoesNotContain("EmailOptOutAt =", Read("src/IPRO.Business/Services/ContactOptOuts.cs"));
        Assert.Contains("public static void CarryOptOut(", Read("src/IPRO.Business/Services/EmailConsentService.cs"));
    }

    [Theory]
    [InlineData("Yukon Territory", "YT")]
    [InlineData("Yukon", "YT")]
    [InlineData("yukon territory", "YT")]
    [InlineData("Newfoundland and Labrador", "NL")]
    [InlineData("Northwest Territories", "NT")]
    [InlineData("Prince Edward Island", "PE")]
    public void Client_invoices_know_the_provinces_the_forms_store(string stored, string expected) =>
        Assert.Equal(expected, ClientInvoiceTaxRules.NormalizeProvince(stored));

    [Fact]
    public void The_preview_placeholder_is_not_a_secret_in_either_app()
    {
        const string placeholder = "CHANGE_THIS_SHARED_SECRET";
        var forged = AdminPreviewToken.Create(placeholder, 12, 3, false, TimeSpan.FromMinutes(5));
        Assert.False(AdminPreviewToken.TryValidate(forged, placeholder, out _, out _, out _));

        var real = AdminPreviewToken.Create("a-real-secret-from-azure", 12, 3, true, TimeSpan.FromMinutes(5));
        Assert.True(AdminPreviewToken.TryValidate(real, "a-real-secret-from-azure", out var agentId, out var templateId, out var useDefaults));
        Assert.Equal((12, 3, true), (agentId, templateId, useDefaults));

        Assert.True(AdminPreviewToken.IsUnset(null));
        Assert.True(AdminPreviewToken.IsUnset("  "));
        Assert.Contains("AdminPreviewToken.IsUnset(secret)", Read("src/IPRO.Admin/Controllers/WebsiteTemplatesController.cs"));
    }

    [Theory]
    [InlineData("Accountants")]
    [InlineData("Mortgage")]
    [InlineData("Generic")]
    [InlineData("")]
    [InlineData(null)]
    public void Only_an_insurance_adviser_sees_insurance_examples(string? businessType)
    {
        var words = BusinessWords.For(businessType);
        foreach (var text in new[] { words.FollowUpExample, words.RenewalName, words.RenewalExample, words.RenewalsPhrase, words.GroupsExample, words.SiteTitleExample, words.AiWriter })
        {
            Assert.DoesNotContain("insurance", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("policy", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_forms_and_the_ai_take_their_examples_from_the_business_type()
    {
        Assert.Contains("insurance", BusinessWords.For("Insurance / Financial").GroupsExample);
        Assert.Equal("an accountant", BusinessWords.For("Accountants").AiWriter);

        foreach (var view in new[] { "Clients/Details", "Clients/FollowUpQueue", "Clients/AccountTypes", "Website/Index" })
            Assert.Contains("BusinessWords", Read($"src/IPRO.Web/Views/{view}.cshtml"));
        Assert.DoesNotContain("Call about policy renewal", Read("src/IPRO.Web/Views/Clients/FollowUpQueue.cshtml"));
        Assert.DoesNotContain("financial/insurance advisor", Read("src/IPRO.Business/Services/AnthropicAiSuggestionService.cs"));
    }

    [Fact]
    public void A_limited_account_can_still_take_its_data()
    {
        var program = Read("src/IPRO.Web/Program.cs");
        foreach (var path in new[] { "/Clients/ExportCsv", "/ClientInvoices/Export", "/WebsiteLeads/Export" })
        {
            Assert.Contains($"path.Equals(\"{path}\", StringComparison.OrdinalIgnoreCase)", program);
            Assert.Contains($"href=\"/portal{path}\"", Read("src/IPRO.Web/Views/Billing/Index.cshtml"));
        }
        Assert.Contains("&& !canExportOwnData)", program);
    }

    [Fact]
    public void The_billing_cards_show_the_setup_fee_and_invented_content_says_so()
    {
        Assert.Contains("one-time setup", Read("src/IPRO.Web/Views/Billing/Index.cshtml"));
        Assert.Contains("\" (sample)\"", Read("src/IPRO.Web/Infrastructure/ProspectWebsitePreviewBuilder.cs"));
        Assert.Contains("Shown: the Platinum package.", Read("src/IPRO.Web/Views/Home/Index.cshtml"));
    }

    [Fact]
    public async Task The_accountants_library_loses_its_stale_lines_and_only_those()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        const string keep = "<li>Rental income</li>";
        const string stale = "<ul><li>Transit pass credit</li>" + keep + "<li>Sales and use tax returns for retail clients</li></ul>";
        db.WebsiteStarterArticles.Add(new WebsiteStarterArticle { BusinessType = "Accountants", Title = "Owner's copy", Summary = "s", Content = stale });
        db.WebsiteStarterArticles.Add(new WebsiteStarterArticle { BusinessType = "Mortgage", Title = "Not an accountant's", Summary = "s", Content = stale });
        await db.SaveChangesAsync();

        await WebsiteStarterArticleSeeder.SeedAsync(db);

        db.ChangeTracker.Clear();
        var fixedRow = await db.WebsiteStarterArticles.SingleAsync(a => a.Title == "Owner's copy");
        Assert.Equal("<ul>" + keep + "<li>GST/HST and PST returns for businesses</li></ul>", fixedRow.Content);
        Assert.Equal(stale, (await db.WebsiteStarterArticles.SingleAsync(a => a.Title == "Not an accountant's")).Content);

        var library = await db.WebsiteStarterArticles.Where(a => a.BusinessType == "Accountants").Select(a => a.Content).ToListAsync();
        Assert.DoesNotContain(library, content => content.Contains("Transit pass") || content.Contains("Sales and use tax") || content.Contains("bi-weekly"));
    }

    private static async Task<int> SeedAgentAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = ($"T624-{Guid.NewGuid():N}")[..20], MonthlyPrice = 40m };
        db.Add(rule);
        await db.SaveChangesAsync();
        var agent = new AgentUser
        {
            UserName = ($"t624-{Guid.NewGuid():N}")[..20], Email = $"{Guid.NewGuid():N}@example.test",
            FirstName = "Opt", LastName = "Out", CompanyName = "Opt Co",
            DomainName = ($"t624-{Guid.NewGuid():N}")[..24], PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<Client> SeedClientAsync(IPRODbContext db, int agentId, string email, DateTime? optedOutAt, string source)
    {
        var client = new Client
        {
            AgentUserId = agentId, FirstName = "Con", LastName = "Tact", Email = email,
            EmailOptOutAt = optedOutAt, EmailOptOutSource = source, IsNewsletterSubscribed = optedOutAt == null
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
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
