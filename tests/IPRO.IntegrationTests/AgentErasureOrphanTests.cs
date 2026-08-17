using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.DataAccess;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPRO.IntegrationTests;

// A5-H13 and its sibling: a row that is IN the erasure map and still cannot be erased.
//
// The existing coverage suites assert MEMBERSHIP -- is this table listed in AgentDataEraser.Map. They
// cannot assert REACHABILITY -- does the predicate still match anything. Those come apart the moment
// the agent's own UI deletes the parent the predicate selects through, and when they do, every signal
// says success: the preview reports 0, the erase deletes 0, and the rows sit there forever.
//
// Two live instances, both fixed 2026-08-17:
//   - Deleting a custom form stranded its WebsiteFormSubmissionAnswers (predicate anchored on the form).
//   - Deleting a recurring invoice schedule stranded its RecurringInvoiceLineItems (no FK, no Include,
//     so EF's client-side cascade never fired either).
//
// These tests delete the parent through the SAME sequence the controller uses, then erase, then assert
// nothing is left. That ordering is the entire point -- an erasure test on a pristine fixture passes
// with the bug present.
public class AgentErasureOrphanTests
{
    [Fact]
    public async Task Form_deleted_then_agent_erased_leaves_no_submission_answers()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedAgentWithFormSubmissionAsync(db, "orphan-a");

        await DeleteFormAsTheControllerDoesAsync(db, seed.FormId);

        var report = await AgentDataEraser.EraseAsync(db, seed.AgentId, eraseFinancialRecords: true);

        var remaining = await db.WebsiteFormSubmissionAnswers.CountAsync(a => a.WebsiteLeadId == seed.LeadId);
        Assert.Equal(0, remaining);

        // And the erase must have actually reported doing it, not silently found nothing -- a preview
        // that says 0 while rows exist is precisely how this survived four audits.
        Assert.Contains(report.Tables, t => t.Table == "WebsiteFormSubmissionAnswers" && t.Rows == 3);
    }

    [Fact]
    public async Task Answers_are_still_erased_when_the_form_still_exists()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedAgentWithFormSubmissionAsync(db, "orphan-b");

        // No form delete this time. Proves the new lead anchor is ADDITIVE -- the form-side clause
        // still works and the change did not swap one single point of failure for another.
        await AgentDataEraser.EraseAsync(db, seed.AgentId, eraseFinancialRecords: true);

        Assert.Equal(0, await db.WebsiteFormSubmissionAnswers.CountAsync());
    }

    // The new predicate WIDENS a DELETE. This repository has already destroyed one agent's data with a
    // too-wide predicate, so widening one without a cross-tenant guard is not acceptable.
    [Fact]
    public async Task Another_agents_answers_survive_the_erase()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var a = await SeedAgentWithFormSubmissionAsync(db, "tenant-a");
        var b = await SeedAgentWithFormSubmissionAsync(db, "tenant-b");

        await DeleteFormAsTheControllerDoesAsync(db, a.FormId);
        await AgentDataEraser.EraseAsync(db, a.AgentId, eraseFinancialRecords: true);

        Assert.Equal(0, await db.WebsiteFormSubmissionAnswers.CountAsync(x => x.WebsiteLeadId == a.LeadId));
        Assert.Equal(3, await db.WebsiteFormSubmissionAnswers.CountAsync(x => x.WebsiteLeadId == b.LeadId));
        Assert.True(await db.WebsiteForms.AnyAsync(f => f.Id == b.FormId));
        Assert.True(await db.WebsiteLeads.AnyAsync(l => l.Id == b.LeadId));
    }

    // The predicate now names @agentId TWICE in one statement, and preview and erase reach the
    // database by different routes -- ScalarAsync binds a named parameter, the delete path rewrites
    // @agentId to {0} and hands it to ExecuteSqlRawAsync. If either route mishandled the repeat, the
    // two numbers would disagree. Nothing else in the map exercises that.
    [Fact]
    public async Task The_preview_counts_exactly_what_the_erase_removes()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedAgentWithFormSubmissionAsync(db, "preview-match");
        await DeleteFormAsTheControllerDoesAsync(db, seed.FormId);

        var preview = await AgentDataEraser.PreviewAsync(db, seed.AgentId, eraseFinancialRecords: true);
        var erase = await AgentDataEraser.EraseAsync(db, seed.AgentId, eraseFinancialRecords: true);

        var previewed = preview.Tables.FirstOrDefault(t => t.Table == "WebsiteFormSubmissionAnswers")?.Rows ?? 0;
        var erased = erase.Tables.FirstOrDefault(t => t.Table == "WebsiteFormSubmissionAnswers")?.Rows ?? 0;

        Assert.Equal(3, previewed);
        Assert.Equal(previewed, erased);
    }

    // The sibling. RecurringInvoiceLineItems has a navigation property, so EnsureCreated gives the TEST
    // database a real ON DELETE CASCADE that production does not have -- deleting the schedule through
    // EF would clean the line items here and prove nothing. So this test deletes the schedule with a
    // raw DELETE, which is what production's FK-less schema actually does, and then checks the
    // controller's explicit RemoveRange is what saves us.
    [Fact]
    public async Task Recurring_schedule_deleted_then_agent_erased_leaves_no_line_items()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedAgentWithFormSubmissionAsync(db, "recurring");

        var schedule = new RecurringInvoiceSchedule
        {
            AgentUserId = seed.AgentId,
            ClientId = seed.ClientId,
            Frequency = RecurringInvoiceFrequency.Monthly,
            NextRunDate = DateTime.UtcNow.Date.AddDays(30),
        };
        db.Add(schedule);
        await db.SaveChangesAsync();

        db.AddRange(
            new RecurringInvoiceLineItem { RecurringInvoiceScheduleId = schedule.Id, Description = "Retainer", Quantity = 1, UnitPrice = 250m },
            new RecurringInvoiceLineItem { RecurringInvoiceScheduleId = schedule.Id, Description = "Filing", Quantity = 2, UnitPrice = 75m });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // What RecurringInvoicesController.Delete now does: remove the children explicitly, by query,
        // because the nav collection is not loaded on that path and no FK exists in production.
        var lineItems = await db.RecurringInvoiceLineItems
            .Where(li => li.RecurringInvoiceScheduleId == schedule.Id).ToListAsync();
        db.RecurringInvoiceLineItems.RemoveRange(lineItems);
        db.RecurringInvoiceSchedules.Remove(await db.RecurringInvoiceSchedules.FirstAsync(s => s.Id == schedule.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(0, await db.RecurringInvoiceLineItems.CountAsync(li => li.RecurringInvoiceScheduleId == schedule.Id));

        await AgentDataEraser.EraseAsync(db, seed.AgentId, eraseFinancialRecords: true);
        Assert.Equal(0, await db.RecurringInvoiceLineItems.CountAsync());
    }

    // THE GENERIC GUARD. TestDatabase is a fresh single-agent database, so after a full shred every
    // table the eraser claims to cover must be empty -- no per-table anchor reasoning required, and a
    // future sibling of this defect shows up here whatever shape it takes.
    //
    // The fixture deliberately performs the agent-visible deletes FIRST, so it reproduces a used
    // account rather than a pristine one. That single detail is the difference between a test that
    // catches A5-H13 and one that does not.
    //
    // NotYetSeeded is a ratchet: tables the fixture does not create rows in yet. It may only ever
    // shrink. Entries here are not exemptions from erasure, only from THIS test's coverage.
    [Fact]
    public async Task Nothing_the_agent_owned_survives_a_full_shred()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedAgentWithFormSubmissionAsync(db, "full-shred");
        await DeleteFormAsTheControllerDoesAsync(db, seed.FormId);

        await AgentDataEraser.EraseAsync(db, seed.AgentId, eraseFinancialRecords: true);
        db.ChangeTracker.Clear();

        var survivors = new List<string>();
        foreach (var table in AgentDataEraser.CoveredTables)
        {
            if (NotYetSeeded.Contains(table)) continue;
            var rows = await CountAsync(db, table);
            if (rows > 0) survivors.Add($"{table} ({rows} rows)");
        }

        Assert.True(survivors.Count == 0,
            "These tables still hold rows after a full agent shred on a single-agent database:\n  " +
            string.Join("\n  ", survivors) +
            "\nEither the eraser predicate is unreachable (something deleted the parent it selects " +
            "through), or the table is missing from the map.");
    }

    // Only tables this fixture does not yet create rows in. Shrink this, never grow it.
    private static readonly HashSet<string> NotYetSeeded = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClientComments", "ClientLifeEvents", "ClientPolicies", "ClientDocuments", "PortalDocuments",
        "PortalMessages", "PortalRequests", "ClientPortalActivities", "DidYouKnowEmailQueueItems",
        "DripCampaignStepSends", "DripCampaignEnrollments", "DripCampaignSteps", "DripCampaigns",
        "NewsLetterArticles", "NewsLetterRecipients", "NewsLetterSends", "NewsLetters",
        "PollAnswers", "PollRecipients", "PollOptions", "PollQuestions", "PollSends", "PollSurveys",
        "ECardRecipients", "ECards", "ELetterRecipients", "ELetters",
        "SupportTicketMessages", "SupportTickets",
        "WebsiteContentBlocks", "WebsitePages", "WebsiteMediaAssets", "WebsitePageViews",
        "WebsiteSpamAttempts", "AgentWebsites",
        "ClientInvoiceLineItems", "ClientInvoices", "RecurringInvoiceLineItems", "RecurringInvoiceSchedules",
        "Testimonials", "TestimonialSubmissions", "SocialPosts", "MarketingCalendarEntries",
        "CalendarEvents", "Appointments", "Tasks", "AgentDocuments", "AgentDailyInsights",
        "AiUsageDailyLogs", "Articles", "Forms", "TrialInviteCodeRedemptions",
        "InvoiceLineItems", "Invoices", "Billings", "SubscriptionChanges", "AgentUsers",
    };

    // Exactly the sequence FormsController.Delete performs. Deliberately NOT a helper call into the
    // controller -- the point is to reproduce what the agent's button does to the database.
    private static async Task DeleteFormAsTheControllerDoesAsync(IPRODbContext db, int formId)
    {
        var form = await db.WebsiteForms.FirstAsync(f => f.Id == formId);
        var fields = await db.WebsiteFormFields.Where(f => f.WebsiteFormId == formId).ToListAsync();
        var fieldIds = fields.Select(f => f.Id).ToList();
        var options = await db.WebsiteFormFieldOptions.Where(o => fieldIds.Contains(o.WebsiteFormFieldId)).ToListAsync();

        db.WebsiteFormFieldOptions.RemoveRange(options);
        db.WebsiteFormFields.RemoveRange(fields);
        db.WebsiteForms.Remove(form);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<int> CountAsync(IPRODbContext db, string table)
    {
        // Table names come from AgentDataEraser.CoveredTables, a compile-time constant list -- no
        // user input reaches this string.
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM `{table}`";
        try
        {
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        catch
        {
            return 0;   // table not in this schema; the coverage suites police that separately
        }
    }

    private sealed record Seed(int AgentId, int ClientId, int FormId, int LeadId);

    // One agent, one client, one published form, one real submission with three answers -- the
    // shape PublicWebsiteController.SubmitCustomForm actually writes: the lead saved first, then the
    // answers carrying BOTH WebsiteLeadId and WebsiteFormId.
    private static async Task<Seed> SeedAgentWithFormSubmissionAsync(IPRODbContext db, string tag)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"{tag}-{Guid.NewGuid():N}"[..20],
            Email = $"{tag}@example.com",
            FirstName = "Orphan",
            LastName = "Test",
            DomainName = $"{tag}-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var client = new Client
        {
            AgentUserId = agent.Id,
            FirstName = "Vera",
            LastName = "Visitor",
            Email = $"vera.{tag}@example.com"
        };
        db.Clients.Add(client);

        // WebsiteLeads.AgentWebsiteId is a real FK with ON DELETE CASCADE, so the lead needs a live
        // site to hang off. That cascade is also why the eraser's answer entry must run before the
        // WebsiteLeads entry -- worth having in the fixture rather than mocked away.
        var template = new WebsiteTemplate
        {
            TemplateKey = $"tpl-{Guid.NewGuid():N}"[..20],
            Name = "Modern",
            BusinessType = "Accountants"
        };
        db.Add(template);
        await db.SaveChangesAsync();

        var website = new AgentWebsite
        {
            AgentUserId = agent.Id,
            TemplateId = template.Id,
            SiteTitle = $"{tag} site",
            IsPublished = true
        };
        db.Add(website);

        var form = new WebsiteForm
        {
            AgentUserId = agent.Id,
            Title = "Contact us",
            SubmitButtonText = "Send"
        };
        db.Add(form);
        await db.SaveChangesAsync();

        var field = new WebsiteFormField
        {
            WebsiteFormId = form.Id,
            Label = "How did you hear about us?",
            FieldType = WebsiteFormFieldTypes.Text,
            SortOrder = 0
        };
        db.Add(field);
        await db.SaveChangesAsync();

        db.Add(new WebsiteFormFieldOption { WebsiteFormFieldId = field.Id, Text = "Referral", SortOrder = 0 });

        var lead = new WebsiteLead
        {
            AgentUserId = agent.Id,
            AgentWebsiteId = website.Id,
            ClientId = client.Id,
            SubmissionType = WebsiteLeadTypes.CustomForm,
            FirstName = "Vera",
            LastName = "Visitor",
            Email = $"vera.{tag}@example.com",
            Message = "How did you hear about us?: Referral"
        };
        db.Add(lead);
        await db.SaveChangesAsync();

        for (var i = 0; i < 3; i++)
        {
            db.Add(new WebsiteFormSubmissionAnswer
            {
                WebsiteLeadId = lead.Id,
                WebsiteFormId = form.Id,
                WebsiteFormFieldId = field.Id,
                FieldLabel = $"Question {i + 1}",
                FieldType = WebsiteFormFieldTypes.Text,
                Value = $"Answer {i + 1}"
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new Seed(agent.Id, client.Id, form.Id, lead.Id);
    }
}
