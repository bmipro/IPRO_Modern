using System;

namespace IPRO.Entities;

// 632 (2026-11-24, the 2026-09-17 audit's E-5): which Google account the adviser's follow-ups were written to.
// A follow-up keeps its GoogleEventId when the adviser disconnects (490), so connecting the SAME account again
// does not write it to Google a second time. Connecting a DIFFERENT account must instead forget those ids, or
// nothing would ever be written to the new calendar: the push only sends follow-ups that have no GoogleEventId.
// The connection row, and the address on it, leaves with Disconnect, so the account is remembered here, as a
// SHA-256 of the adviser's id and the address (GoogleCalendarAccountMemory.Hash): enough to tell "same or
// different", not the address itself.
//
// Its own small table, NOT a column on AgentUsers, for AgentFollowUpReminder's reason: a column the model expects
// and the table lacks fails every AgentUsers query, sign-in included. No row = not known (a connection made, and
// ended, before this): treated as the same account, which is how every reconnect behaved before.
public class AgentGoogleCalendarAccount
{
    public int AgentUserId { get; set; }
    public string AccountHash { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public AgentUser? AgentUser { get; set; }
}
