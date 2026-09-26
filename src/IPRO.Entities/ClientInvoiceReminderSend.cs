using System;

namespace IPRO.Entities;

// 523 (2026-09-26), slice 3: one row per reminder stage sent for an invoice -- the send-once guard
// per stage, which the nightly job reads before it sends and writes after. Its own table, not a
// column per stage on ClientInvoices, for the same reason as the settings table; unique on
// (invoice, stage) so the database keeps the promise even if two runs overlap.
public class ClientInvoiceReminderSend
{
    public int Id { get; set; }
    public int ClientInvoiceId { get; set; }
    public int AgentUserId { get; set; }
    public string Stage { get; set; } = string.Empty;   // a ClientInvoiceReminderStages value
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public ClientInvoice ClientInvoice { get; set; } = null!;
}
