using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

public static class ClientInvoiceReminderStages
{
    public const string BeforeDue = "before";
    public const string OnDue = "due";
    public const string OverdueFirst = "overdue1";
    public const string Overdue7 = "overdue7";
    public const string Overdue14 = "overdue14";
    public const string Overdue30 = "overdue30";

    public static string Label(string stage) => stage switch
    {
        BeforeDue => "before the due date",
        OnDue => "on the due date",
        OverdueFirst => "the day after the due date",
        Overdue7 => "7 days overdue",
        Overdue14 => "14 days overdue",
        Overdue30 => "30 days overdue",
        _ => stage
    };
}

// 523 (2026-09-26), slice 3: the reminder schedule the adviser controls, and the words each stage
// says. Pure where it can be (which stage is due today; the wording filled in), set-based where it
// touches the database (load, save), like FollowUpReminderPreference.
public static class ClientInvoiceReminderSchedule
{
    public const string DefaultBeforeDueMessage = "A friendly note that invoice {invoice} for {amount} is due on {due}, in {days} days. You can view it online below.";
    public const string DefaultOnDueMessage = "Invoice {invoice} for {amount} is due today. You can view it online below.";
    public const string DefaultOverdueMessage = "This is a reminder that invoice {invoice} for {amount} is now overdue.";
    public const int MessageMaxLength = 1000;
    public const int MinimumDaysBetweenReminders = 5;

    public static ClientInvoiceReminderSettings Defaults(int agentUserId) => new() { AgentUserId = agentUserId };

    // The one stage due today for an invoice, or null. The overdue windows do not overlap (1-6,
    // 7-13, 14-29 and 30-59 days past due), a stage already sent never repeats, and a stage whose
    // window passed unsent is simply missed -- nothing piles up when the feature arrives or after
    // a quiet day. Before the due date the window is the chosen day and the two after it.
    public static string? StageDue(ClientInvoiceReminderSettings settings, DateTime dueDate, DateTime today, IReadOnlySet<string> sent)
    {
        var days = (int)(today.Date - dueDate.Date).TotalDays;     // negative before the due date
        var beforeDays = Math.Clamp(settings.BeforeDueDays, 1, 30);
        var stage =
            days < 0 ? (settings.BeforeDueEnabled && -days <= beforeDays && -days >= Math.Max(1, beforeDays - 2) ? ClientInvoiceReminderStages.BeforeDue : null)
            : days == 0 ? (settings.OnDueEnabled ? ClientInvoiceReminderStages.OnDue : null)
            : days <= 6 ? (settings.OverdueFirstEnabled ? ClientInvoiceReminderStages.OverdueFirst : null)
            : days <= 13 ? (settings.Overdue7Enabled ? ClientInvoiceReminderStages.Overdue7 : null)
            : days <= 29 ? (settings.Overdue14Enabled ? ClientInvoiceReminderStages.Overdue14 : null)
            : days <= 59 ? (settings.Overdue30Enabled ? ClientInvoiceReminderStages.Overdue30 : null)
            : null;
        return stage != null && !sent.Contains(stage) ? stage : null;
    }

    public static string MessageFor(ClientInvoiceReminderSettings settings, string stage) => stage switch
    {
        ClientInvoiceReminderStages.BeforeDue => Or(settings.BeforeDueMessage, DefaultBeforeDueMessage),
        ClientInvoiceReminderStages.OnDue => Or(settings.OnDueMessage, DefaultOnDueMessage),
        _ => Or(settings.OverdueMessage, DefaultOverdueMessage)
    };

    public static string SubjectFor(string stage, string documentNumber, int daysFromDue)
    {
        var days = Math.Abs(daysFromDue);
        return stage switch
        {
            ClientInvoiceReminderStages.BeforeDue => $"Invoice {documentNumber} is due in {days} {(days == 1 ? "day" : "days")}",
            ClientInvoiceReminderStages.OnDue => $"Invoice {documentNumber} is due today",
            _ => $"Reminder: Invoice {documentNumber} is overdue"
        };
    }

    // The wording with its placeholders filled, as HTML. The text is encoded FIRST and the values
    // after it, so nothing an adviser or a client typed becomes markup; line breaks become <br>.
    public static string Fill(string template, ClientInvoice invoice, int daysFromDue)
    {
        static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        return E(template)
            .Replace("{invoice}", E(invoice.DocumentNumber))
            .Replace("{amount}", E($"${invoice.Total:N2} {invoice.Currency}"))
            .Replace("{due}", invoice.DueDate.HasValue ? invoice.DueDate.Value.ToString("MMMM d, yyyy") : string.Empty)
            .Replace("{days}", Math.Abs(daysFromDue).ToString())
            .Replace("{client}", E(invoice.Client?.FirstName))
            .Replace("{company}", E(invoice.AgentUser?.CompanyName))
            .Replace("\r\n", "\n")
            .Replace("\n", "<br>");
    }

    public static async Task<ClientInvoiceReminderSettings> LoadAsync(IPRODbContext db, int agentUserId) =>
        await db.ClientInvoiceReminderSettings.AsNoTracking().FirstOrDefaultAsync(s => s.AgentUserId == agentUserId)
        ?? Defaults(agentUserId);

    // Set-based on purpose: nothing is tracked, so it cannot collide with whatever else the calling
    // page's DbContext is in the middle of, and saving the same answer twice is not an error.
    public static async Task SaveAsync(IPRODbContext db, ClientInvoiceReminderSettings settings)
    {
        var agentId = settings.AgentUserId;
        var now = DateTime.UtcNow;
        var beforeEnabled = settings.BeforeDueEnabled;
        var beforeDays = Math.Clamp(settings.BeforeDueDays, 1, 30);
        var onDue = settings.OnDueEnabled;
        var first = settings.OverdueFirstEnabled;
        var d7 = settings.Overdue7Enabled;
        var d14 = settings.Overdue14Enabled;
        var d30 = settings.Overdue30Enabled;
        var beforeMessage = Clip(settings.BeforeDueMessage);
        var onDueMessage = Clip(settings.OnDueMessage);
        var overdueMessage = Clip(settings.OverdueMessage);

        var updated = await db.ClientInvoiceReminderSettings
            .Where(x => x.AgentUserId == agentId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.BeforeDueEnabled, beforeEnabled)
                .SetProperty(x => x.BeforeDueDays, beforeDays)
                .SetProperty(x => x.OnDueEnabled, onDue)
                .SetProperty(x => x.OverdueFirstEnabled, first)
                .SetProperty(x => x.Overdue7Enabled, d7)
                .SetProperty(x => x.Overdue14Enabled, d14)
                .SetProperty(x => x.Overdue30Enabled, d30)
                .SetProperty(x => x.BeforeDueMessage, beforeMessage)
                .SetProperty(x => x.OnDueMessage, onDueMessage)
                .SetProperty(x => x.OverdueMessage, overdueMessage)
                .SetProperty(x => x.UpdatedAt, now));
        if (updated > 0) return;

        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO `ClientInvoiceReminderSettings`
            (`AgentUserId`, `BeforeDueEnabled`, `BeforeDueDays`, `OnDueEnabled`, `OverdueFirstEnabled`, `Overdue7Enabled`, `Overdue14Enabled`, `Overdue30Enabled`, `BeforeDueMessage`, `OnDueMessage`, `OverdueMessage`, `UpdatedAt`)
            VALUES ({agentId}, {beforeEnabled}, {beforeDays}, {onDue}, {first}, {d7}, {d14}, {d30}, {beforeMessage}, {onDueMessage}, {overdueMessage}, {now})
            ON DUPLICATE KEY UPDATE `BeforeDueEnabled` = {beforeEnabled}, `BeforeDueDays` = {beforeDays}, `OnDueEnabled` = {onDue}, `OverdueFirstEnabled` = {first}, `Overdue7Enabled` = {d7}, `Overdue14Enabled` = {d14}, `Overdue30Enabled` = {d30}, `BeforeDueMessage` = {beforeMessage}, `OnDueMessage` = {onDueMessage}, `OverdueMessage` = {overdueMessage}, `UpdatedAt` = {now}");
    }

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Clip(string? value)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= MessageMaxLength ? value : value[..MessageMaxLength];
    }
}
