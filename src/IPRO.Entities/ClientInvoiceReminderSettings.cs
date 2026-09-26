using System;

namespace IPRO.Entities;

// 523 (2026-09-26), slice 3: the adviser's reminder schedule for the invoices they send their
// clients -- which stages are on (a set number of days before the due date, on it, the day after,
// then 7, 14 and 30 days overdue) and what each stage says. No row means the defaults in
// ClientInvoiceReminderSchedule: nothing before or on the due date, every overdue stage on, the
// stock wording.
//
// Its own small table, deliberately NOT columns on AgentUsers, for AgentFollowUpReminder's reason
// (498): a column the model expects and the table lacks fails every AgentUsers query, sign-in
// included, while a table nobody else reads fails only this feature.
public class ClientInvoiceReminderSettings
{
    public int AgentUserId { get; set; }
    public bool BeforeDueEnabled { get; set; }
    public int BeforeDueDays { get; set; } = 3;
    public bool OnDueEnabled { get; set; }
    public bool OverdueFirstEnabled { get; set; } = true;
    public bool Overdue7Enabled { get; set; } = true;
    public bool Overdue14Enabled { get; set; } = true;
    public bool Overdue30Enabled { get; set; } = true;
    // Empty means the stock wording. Placeholders: {invoice} {amount} {due} {days} {client} {company}.
    public string BeforeDueMessage { get; set; } = string.Empty;
    public string OnDueMessage { get; set; } = string.Empty;
    public string OverdueMessage { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser AgentUser { get; set; } = null!;
}
