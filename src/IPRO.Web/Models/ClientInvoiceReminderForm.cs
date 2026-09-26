using System.ComponentModel.DataAnnotations;
using IPRO.DataAccess;
using IPRO.Entities;

namespace IPRO.Web.Models;

// 523 (2026-09-26), slice 3: the reminder-schedule form, kept apart from the entity so nothing but
// these fields binds from the post (the entity carries the adviser's id and a navigation).
public class ClientInvoiceReminderForm
{
    public bool BeforeDueEnabled { get; set; }
    [Range(1, 30, ErrorMessage = "Days before the due date must be between 1 and 30.")]
    public int BeforeDueDays { get; set; } = 3;
    public bool OnDueEnabled { get; set; }
    public bool OverdueFirstEnabled { get; set; } = true;
    public bool Overdue7Enabled { get; set; } = true;
    public bool Overdue14Enabled { get; set; } = true;
    public bool Overdue30Enabled { get; set; } = true;
    [MaxLength(ClientInvoiceReminderSchedule.MessageMaxLength)]
    public string? BeforeDueMessage { get; set; }
    [MaxLength(ClientInvoiceReminderSchedule.MessageMaxLength)]
    public string? OnDueMessage { get; set; }
    [MaxLength(ClientInvoiceReminderSchedule.MessageMaxLength)]
    public string? OverdueMessage { get; set; }

    public static ClientInvoiceReminderForm From(ClientInvoiceReminderSettings s) => new()
    {
        BeforeDueEnabled = s.BeforeDueEnabled,
        BeforeDueDays = s.BeforeDueDays,
        OnDueEnabled = s.OnDueEnabled,
        OverdueFirstEnabled = s.OverdueFirstEnabled,
        Overdue7Enabled = s.Overdue7Enabled,
        Overdue14Enabled = s.Overdue14Enabled,
        Overdue30Enabled = s.Overdue30Enabled,
        BeforeDueMessage = s.BeforeDueMessage,
        OnDueMessage = s.OnDueMessage,
        OverdueMessage = s.OverdueMessage
    };

    public ClientInvoiceReminderSettings ToSettings(int agentUserId) => new()
    {
        AgentUserId = agentUserId,
        BeforeDueEnabled = BeforeDueEnabled,
        BeforeDueDays = BeforeDueDays,
        OnDueEnabled = OnDueEnabled,
        OverdueFirstEnabled = OverdueFirstEnabled,
        Overdue7Enabled = Overdue7Enabled,
        Overdue14Enabled = Overdue14Enabled,
        Overdue30Enabled = Overdue30Enabled,
        BeforeDueMessage = BeforeDueMessage ?? string.Empty,
        OnDueMessage = OnDueMessage ?? string.Empty,
        OverdueMessage = OverdueMessage ?? string.Empty
    };
}
