# Calendar and Google Calendar

## What It Is

The **Follow-up Calendar** is a month grid of every client follow-up that is due, so you can see and clear your week. Connect Google Calendar and it becomes two-way: follow-ups you add here appear in Google, and events you add in Google appear here.

## Open It

Select **Calendar** in the Agent Portal menu (under Clients). The heading reads **Follow-up Calendar** with the month's count of scheduled follow-ups.

## Read the Calendar

- Move months with the previous and next buttons.
- Colours: **Overdue** (red), **Today** (amber), **Upcoming** (blue), **Completed** (green). Grey entries are **from your connected Google Calendar**.
- Each follow-up shows its title, linked to the client, and the client's name.
- The tick button marks a follow-up complete. That is the only action on the calendar itself.

## Add or Change a Follow-up

Follow-ups are created on the client's record, not on the calendar:

1. Open the client from **Clients** and find the **Follow-ups** panel.
2. Enter **What to do**, a **Due date** and optional **Notes**, then add it.
3. The **Follow-ups** page (under Clients) shows every follow-up across all clients with filters **Open, Overdue, Today, Upcoming, Completed, All**, and lets you add one with a client picker.

Follow-ups can be added, completed and deleted. A due date cannot be edited once created; delete and re-add instead.

## Follow-ups the Platform Creates for You

- Scheduling a client's appointment request from **Portal Requests** creates a follow-up titled "Appointment: client name".
- Life-event reminders (renewals, anniversaries, birthdays) create follow-ups ahead of the date, on the packages that include them.

## Connect Google Calendar

1. Go to **My Profile** (click your name at the bottom of the left menu) and find the **Calendar Source** section.
2. Click **Connect Google Calendar** and approve access on Google's screen. You return to Profile with "Connected as your address".
3. From then on a sync runs every 15 minutes: open follow-ups are created as events on your primary Google calendar, and Google events appear on the Follow-up Calendar as grey, read-only entries.
4. Renaming or moving a synced event in Google updates the follow-up's title and due date here. Cancelling it in Google unlinks the follow-up rather than deleting it. Deleting a synced follow-up here removes the Google event; completing one leaves the event in place. When the follow-up is a client's appointment from **Portal Requests**, moving it in Google also emails the client the new time (not for a move into the past, or for an appointment already marked done).
5. Times follow the **Time Zone** on your Profile. A follow-up with a time (a scheduled appointment) appears in Google at that time; one with only a date appears as an all-day event on that date. An event you move in Google comes back here at the same moment, in your Profile time zone. Keep that time zone the same as your Google Calendar's, and both show the same clock.

**Disconnect** is in the same section, and asks you to confirm. It removes the events copied in from Google from your iPro calendar and stops the sync. Your follow-ups stay in iPro, and the events already on your Google Calendar stay in Google.

The connect step always runs on the main portal address, so if you started from your own domain the address bar changes during the Google approval and returns afterwards.

### When the Sync Stops

If the sync has failed for two hours (usually because Google's permission was removed or has expired), the **Calendar Source** section on Profile shows "Your Google Calendar has stopped syncing", and you are emailed once. Until it is fixed, follow-ups you add here do not reach Google and changes in Google do not come back; your follow-ups here are safe.

1. Open **My Profile** (click your name at the bottom of the left menu) and find **Calendar Source**.
2. Click **Reconnect Google Calendar** and approve access on Google's screen.

If it was a passing outage at Google, the sync starts again by itself and the notice goes away.

### Switch to a Different Google Account

1. Open **My Profile** (click your name at the bottom of the left menu) and find **Calendar Source**.
2. Click **Disconnect** and confirm.
3. Click **Connect Google Calendar** and choose the other account on Google's screen.

Profile says the account is a different one. Within 15 minutes your open follow-ups are added to the new account's calendar, and that calendar's events appear here. The events copied from the old account are removed from your iPro calendar, and the events iPro added to the old account's calendar stay there. A follow-up you had cancelled in Google stays off Google.

Connecting the same account again sends nothing twice: follow-ups already on that calendar stay linked to their events.

### If Your Package Changes

Google Calendar sync is part of **Platinum** and **Broker**. On another package, the sync pauses:

- follow-ups you add do not reach Google, and changes in Google do not come back;
- the grey events copied from Google no longer show on the Follow-up Calendar;
- **Calendar Source** on Profile says the sync is paused, and **Disconnect** is still there if you want iPro to forget the connection.

Move back to Platinum or Broker and the sync starts again by itself within 15 minutes, with nothing to reconnect. A suspended account is not synced either.

## Reminders

iPro emails you your follow-ups each morning, around 7 a.m. in the **Time Zone** on your Profile. One email lists the follow-ups due today and the ones overdue. It goes:

- on days when a follow-up is due today or became overdue in the last week;
- every Monday while anything at all is still overdue, however old.

On other days, nothing is sent. To stop these emails, open **My Profile** (click your name at the bottom of the left menu), untick **Email me my follow-ups each morning** (under **Time Zone**) and click **Save Profile**. They are not sent while your access is limited to the Billing page.

The Dashboard also shows overdue, today and upcoming counts, and the AI Daily Assistant (on the packages that include it) names the most overdue follow-up as the day's first action.

## Package Availability

The Follow-up Calendar is included with every package. Google Calendar sync and life-event reminders are included with **Platinum** and **Broker**. Client appointment requests need the Client Portal, also Platinum and Broker.
