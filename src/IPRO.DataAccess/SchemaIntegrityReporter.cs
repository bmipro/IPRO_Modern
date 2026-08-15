using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// Auditor 5, F14: the EF model declares delete behaviour (Cascade/SetNull/Restrict) for every
// relationship, but only ~36 tables carry real foreign keys -- the ~40 created by the startup
// schema repairs declare none. The model's delete semantics are therefore enforced only when EF has
// LOADED the dependents; every raw-SQL path bypasses them entirely. That split is what made client
// deletion orphan twelve tables (F6) and what let the ledger cascade eat invoice 000008.
//
// Fixing it properly means adding the missing constraints -- deliberate schema surgery that belongs
// with the migration-recovery work (TODO 425), not a quiet side effect. Until then this reporter is
// the RATCHET: at every startup it compares the model's relationships against the database's real
// constraints and screams about any gap that is not in the baseline below. The baseline is the debt
// we know about; anything new fails loudly on the first boot that introduces it.
//
// Shrink KnownUnenforced as constraints are added. Never grow it without understanding why the new
// relationship cannot have its FK.
public static class SchemaIntegrityReporter
{
    // "DependentTable->PrincipalTable" pairs whose model-declared relationship has no database
    // foreign key today. Generated empirically against the production-shaped schema on 2026-08-15.
    private static readonly HashSet<string> KnownUnenforced = new(StringComparer.OrdinalIgnoreCase)
    {
        // DELIBERATE, PERMANENT: FinancialLedgerSchemaGuard drops these three on purpose so nothing
        // can ever cascade into the financial ledger again (the 2026-08-14 invoice loss). They must
        // never be re-added; AgentDataEraser owns their delete semantics.
        "Billings->AgentUsers",
        "Invoices->Billings",
        "SubscriptionChanges->AgentUsers",

        // DEBT, captured 2026-08-15 against the production-shaped schema: tables created by the
        // startup schema repairs, whose CREATE TABLE bodies declare no FOREIGN KEY clauses. Their
        // delete semantics are enforced only by AgentDataEraser/ClientDataEraser (which is why those
        // have coverage tests). Shrink this list by adding real constraints as part of the
        // migration-recovery work (TODO 425/427); never grow it silently.
        "AgentDomains->AgentUsers",
        "AgentDomains->AgentWebsites",
        "ClientInvoiceLineItems->ClientInvoices",
        "ClientInvoices->AgentUsers",
        "ClientInvoices->Clients",
        "ClientLifeEvents->Clients",
        "DripCampaignStepSends->DripCampaignEnrollments",
        "DripCampaignStepSends->DripCampaignSteps",
        "ECardRecipients->Clients",
        "ECardRecipients->ECards",
        "ECards->AgentUsers",
        "ELetterRecipients->Clients",
        "ELetterRecipients->ELetters",
        "ELetters->AgentUsers",
        "PortalAppointmentRequests->Clients",
        "PortalDocuments->Clients",
        "PortalMessages->Clients",
        "PromotionCodeRedemptions->AgentUsers",
        "PromotionCodeRedemptions->BillingRules",
        "PromotionCodeRedemptions->PromotionCodes",
        "PromotionCodes->BillingRules",
        "RecurringInvoiceLineItems->RecurringInvoiceSchedules",
        "RecurringInvoiceSchedules->AgentUsers",
        "RecurringInvoiceSchedules->Clients",
        "SupportTicketMessages->SupportTickets",
        "SupportTickets->AgentUsers",
        "TeamMembers->AgentUsers",
        "TrialInviteCodeRedemptions->AgentUsers",
        "TrialInviteCodeRedemptions->TrialInviteCodes",
        "TrialInviteCodes->BillingRules",
    };

    public sealed record MissingConstraint(string DependentTable, string PrincipalTable, string DeleteBehavior);

    public static async Task<List<MissingConstraint>> FindNewGapsAsync(IPRODbContext db)
    {
        var enforced = await DatabaseForeignKeyPairsAsync(db);
        var gaps = new List<MissingConstraint>();

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var dependentTable = entity.GetTableName();
            if (string.IsNullOrEmpty(dependentTable)) continue;

            foreach (var fk in entity.GetForeignKeys())
            {
                var principalTable = fk.PrincipalEntityType.GetTableName();
                if (string.IsNullOrEmpty(principalTable)) continue;
                // Self-references and owned types don't map to a constraint the same way; skip noise.
                if (string.Equals(dependentTable, principalTable, StringComparison.OrdinalIgnoreCase)) continue;

                var pair = $"{dependentTable}->{principalTable}";
                if (enforced.Contains(pair)) continue;
                if (KnownUnenforced.Contains(pair)) continue;

                gaps.Add(new MissingConstraint(dependentTable, principalTable, fk.DeleteBehavior.ToString()));
            }
        }

        return gaps;
    }

    // Call at startup AFTER all schema work. Never throws, never blocks boot -- a reporting failure
    // must not be an outage -- but any NEW unenforced relationship goes to stderr where the container
    // log surfaces it.
    public static async Task ReportAsync(IPRODbContext db, string appName)
    {
        try
        {
            var gaps = await FindNewGapsAsync(db);
            if (gaps.Count == 0) return;

            Console.Error.WriteLine(
                $"[SchemaIntegrity] {appName}: {gaps.Count} NEW model relationship(s) have no database foreign key " +
                "and are not in the known baseline. The model's delete behaviour is NOT enforced for these -- raw " +
                "SQL and cascades will not see them (this is the F6/F14 orphaning class):");
            foreach (var gap in gaps.OrderBy(g => g.DependentTable, StringComparer.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"[SchemaIntegrity]   {gap.DependentTable} -> {gap.PrincipalTable} (model says {gap.DeleteBehavior})");
            }
            Console.Error.WriteLine(
                "[SchemaIntegrity] Either add the constraint in the schema repair that creates the dependent " +
                "table, or add the pair to SchemaIntegrityReporter.KnownUnenforced with a reason.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SchemaIntegrity] Reporter failed (non-fatal): {ex.Message}");
        }
    }

    private static async Task<HashSet<string>> DatabaseForeignKeyPairsAsync(IPRODbContext db)
    {
        var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wasOpen = db.Database.GetDbConnection().State == System.Data.ConnectionState.Open;
        if (!wasOpen) await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                "SELECT TABLE_NAME, REFERENCED_TABLE_NAME FROM information_schema.KEY_COLUMN_USAGE " +
                "WHERE TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_NAME IS NOT NULL " +
                "GROUP BY TABLE_NAME, REFERENCED_TABLE_NAME;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                pairs.Add($"{reader.GetString(0)}->{reader.GetString(1)}");
            }
        }
        finally
        {
            if (!wasOpen) await db.Database.CloseConnectionAsync();
        }
        return pairs;
    }
}
