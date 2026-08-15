using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// The BillingRules + Invoices money-column repairs, shared by both apps -- auditor 5's F4.
//
// This list lived only in IPRO.Web/Program.cs; IPRO.Admin carried a hand-copied SUBSET (it had the
// trial/waiver/template columns but was missing all ten base pricing columns: MonthlyPrice,
// QuarterlyPrice, AnnualPrice, SetupFee, both PayPal plan ids, MaxClients, MaxNewsletters, IsActive,
// CreatedAt). That is a direct INVARIANTS.md rule-4 breach and the exact drift EmailDeliverySchema
// was created to prevent: if one of those columns were ever lost, Web would silently heal it and
// Admin's Packages/Revenue screens would break with no repair of their own. Same cure -- the list
// lives here once, each Program.cs makes one call.
public static class BillingRuleSchema
{
    private static readonly (string Column, string Definition)[] BillingRuleColumns =
    {
        ("MonthlyPrice",            "decimal(10,2) NOT NULL DEFAULT 0"),
        ("QuarterlyPrice",          "decimal(10,2) NOT NULL DEFAULT 0"),
        ("AnnualPrice",             "decimal(10,2) NOT NULL DEFAULT 0"),
        ("SetupFee",                "decimal(10,2) NOT NULL DEFAULT 0"),
        ("PayPalMonthlyPlanId",     "longtext CHARACTER SET utf8mb4 NULL"),
        ("PayPalAnnualPlanId",      "longtext CHARACTER SET utf8mb4 NULL"),
        ("MaxClients",              "int NOT NULL DEFAULT 500"),
        ("MaxNewsletters",          "int NOT NULL DEFAULT 12"),
        ("DefaultWebsiteTemplateId","int NULL"),
        ("IsActive",                "tinyint(1) NOT NULL DEFAULT TRUE"),
        ("CreatedAt",               "datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)"),
        ("IsTrialPackage",          "tinyint(1) NOT NULL DEFAULT FALSE"),
        ("TrialDurationDays",       "int NULL"),
        ("TrialReminderDayOffsets", "varchar(120) CHARACTER SET utf8mb4 NULL"),
        ("IsHiddenTestPackage",     "tinyint(1) NOT NULL DEFAULT FALSE"),
        ("SetupFeeWaived",          "tinyint(1) NOT NULL DEFAULT FALSE"),
        ("SetupFeeWaivedUntil",     "datetime(6) NULL"),
        // 422b: the price each PayPal plan was created at, so the Packages screen can warn when the
        // editable price diverges from what subscribers are actually charged.
        ("PayPalMonthlyPlanPrice",  "decimal(10,2) NULL"),
        ("PayPalAnnualPlanPrice",   "decimal(10,2) NULL"),
    };

    // Bill-to snapshot: invoices are financial records retained after their agent is deleted, so the
    // bill-to must live ON the invoice.
    private static readonly (string Column, string Definition)[] InvoiceBillToColumns =
    {
        ("BillToName",    "varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''"),
        ("BillToCompany", "varchar(200) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''"),
        ("BillToEmail",   "varchar(255) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''"),
        ("BillToAddress", "varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''"),
    };

    public static async Task EnsureAsync(IPRODbContext db)
    {
        var ownsConnection = db.Database.GetDbConnection().State != ConnectionState.Open;
        if (ownsConnection) await db.Database.OpenConnectionAsync();
        try
        {
            if (await TableExistsAsync(db, "BillingRules"))
            {
                foreach (var (column, definition) in BillingRuleColumns)
                {
                    await EnsureColumnAsync(db, "BillingRules", column, definition);
                }
            }

            if (await TableExistsAsync(db, "Invoices"))
            {
                // Quebec's 14.975% needs 5 decimals as a fraction (0.14975); the original
                // decimal(7,4) column rounded it to 0.1498, so invoices displayed "14.980 %"
                // beside a region label saying 14.975%.
                await EnsureTaxRateScaleAsync(db);

                foreach (var (column, definition) in InvoiceBillToColumns)
                {
                    await EnsureColumnAsync(db, "Invoices", column, definition);
                }

                // Backfill fills blanks from AgentUsers while the row still exists; it runs every
                // startup and touches only invoices whose snapshot is empty.
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE `Invoices` i JOIN `AgentUsers` a ON a.Id = i.AgentUserId SET " +
                    "i.BillToName = CASE WHEN TRIM(CONCAT(COALESCE(a.FirstName,''),' ',COALESCE(a.LastName,''))) = '' THEN COALESCE(a.UserName,'') ELSE TRIM(CONCAT(COALESCE(a.FirstName,''),' ',COALESCE(a.LastName,''))) END, " +
                    "i.BillToCompany = COALESCE(a.CompanyName,''), " +
                    "i.BillToEmail = COALESCE(a.Email,''), " +
                    "i.BillToAddress = CONCAT_WS('\\n', NULLIF(a.CompanyAddress,''), NULLIF(a.City,''), NULLIF(TRIM(CONCAT(COALESCE(a.Province,''),' ',COALESCE(a.PostalCode,''))),''), NULLIF(a.Country,'')) " +
                    "WHERE i.BillToName = ''");
            }
        }
        finally
        {
            if (ownsConnection) await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureColumnAsync(IPRODbContext db, string table, string column, string definition)
    {
        if (await ColumnExistsAsync(db, table, column)) return;

        try
        {
            await using var alter = db.Database.GetDbConnection().CreateCommand();
            // Identifiers are compile-time constants from the arrays above, never user input.
            alter.CommandText = $"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition};";
            await alter.ExecuteNonQueryAsync();
        }
        catch (MySqlConnector.MySqlException ex)
            when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateFieldName)
        {
            // The other app added it between our check and this ALTER -- both start from the same
            // push and repair the same database. The end state is what we wanted.
        }
    }

    private static async Task EnsureTaxRateScaleAsync(IPRODbContext db)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT COALESCE(MAX(NUMERIC_SCALE), -1) FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Invoices' AND COLUMN_NAME = 'TaxRate';";
        var scale = Convert.ToInt32(await command.ExecuteScalarAsync());
        if (scale >= 0 && scale < 5)
        {
            // No race catch on purpose: unlike ADD COLUMN, a MODIFY that loses the web/admin startup
            // race simply re-applies the same definition and succeeds.
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE `Invoices` MODIFY COLUMN `TaxRate` decimal(7,5) NOT NULL");
        }
    }

    private static async Task<bool> TableExistsAsync(IPRODbContext db, string table)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM information_schema.TABLES " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@table";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(IPRODbContext db, string table, string column)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM information_schema.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @column;";
        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "@table";
        tableParameter.Value = table;
        command.Parameters.Add(tableParameter);
        var columnParameter = command.CreateParameter();
        columnParameter.ParameterName = "@column";
        columnParameter.Value = column;
        command.Parameters.Add(columnParameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }
}
