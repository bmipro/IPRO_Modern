# Clients, Account Types, Notes, and Follow-ups

## View and Search Clients

1. Select **Clients** in the Agent Portal menu.
2. Search by name, company, email, phone, city, or account type.
3. Optionally filter by account type or newsletter subscription.
4. Click the eye icon to open the complete client record.
5. Click the edit icon to change the client.

## Add a Client

1. Open **Clients**.
2. Click **Add Client**.
3. Enter the client's name and email. These are required.
4. Add any available address, company, phone, personal, or business information.
5. Assign one or more account types if appropriate.
6. Choose whether the client is subscribed to newsletters.
7. Save the client.

The email address is normalized to lowercase and must be unique for that agent. Package contact limits are enforced before creating the client.

## Edit or Delete a Client

1. Open **Clients**.
2. Click the edit icon beside the client.
3. Make changes and save.

To delete a client, click the trash icon and confirm. Deletion also removes related activity that depends on that client, so use it carefully.

## Import Clients from CSV

1. Open **Clients**.
2. Click **Import CSV**.
3. Select a `.csv` file containing First Name, Last Name, and Email columns.
4. Start the import.
5. Review the result message for imported, skipped, or duplicate contacts.

The import respects package access and the remaining contact allowance.

## Export Clients

1. Open **Clients**.
2. Apply filters if needed.
3. Click **Export**.
4. Open the downloaded CSV in Excel or another spreadsheet tool.

## Manage Account Types

Account types group clients for search, newsletters, and campaigns. Examples include `2026 Seminar`, `Life Insurance Prospect`, or `Mortgage Renewal`.

1. Open **Clients**.
2. Click **Account Types**.
3. Enter a name and optional description.
4. Save the account type.
5. Assign it while creating or editing clients.

Existing account types can be renamed. Delete one only when it is no longer needed.

## Add a Client Note

1. Open the client record.
2. Find **Add note**.
3. Enter the note or comment.
4. Click the plus button.
5. Confirm it appears in the client's activity timeline.

Website inquiries are also added to this timeline automatically when the lead is connected to a CRM client.

## Add a Follow-up

1. Open the client record.
2. Open the follow-up area.
3. Enter a title, due date and time, and optional notes.
4. Save the follow-up.

## Review Follow-ups

Use either:

- **Follow-ups** for a queue filtered by open, overdue, today, upcoming, completed, or all.
- **Calendar** for a monthly calendar view.
- The client record for follow-ups belonging to one client.

Follow-up history uses pagination when the list becomes long.

## Complete or Delete a Follow-up

1. Open the follow-up queue, calendar, dashboard, or client record.
2. Click the check icon to mark it complete.
3. Use the delete action only if the follow-up should not remain in history. If the follow-up was synced to Google Calendar, deleting it also removes the matching Google event immediately.

## Connect Google Calendar (optional, two-way sync)

By default the Calendar runs entirely on IPRO. Agents whose package includes it can also connect their own Google Calendar from **My Profile → Calendar Source**:

1. Click **Connect Google Calendar** and sign in to the Google account to link.
2. Once connected, every follow-up you add in IPRO is pushed to that Google Calendar automatically, and events you create, edit, or delete directly in Google appear on the IPRO Calendar too (synced roughly every 15 minutes, not instantly).
3. Events from Google that aren't tied to any IPRO client show on the Calendar for context (marked with a Google icon) but aren't linked to a client record and can't be marked complete — they're just there so your day looks complete in one place.
4. If a Google event linked to an IPRO follow-up is deleted directly in Google, IPRO unlinks it rather than deleting the follow-up itself — a follow-up is part of a client's history, so it's never silently removed just because the calendar entry vanished.
5. Click **Disconnect** at any time to stop syncing; anything already synced stays as-is.

This is gated by a package feature ("Google Calendar two-way sync") that Super Admin can enable on any package, same as other premium features.

## Client Life-Event Reminders (birthdays, policy renewals, anniversaries)

Agents whose package includes this feature see a **Life Events** card on each client record:

1. If the client has a **Date of Birth** on file, their birthday is covered automatically — no setup needed. A reminder follow-up ("🎂 Birthday: ...") is created 7 days before it every year.
2. For anything else that repeats yearly — a policy renewal, an anniversary, or a custom date — add it from the **Life Events** card: choose a type, a label (e.g. "Auto Policy Renewal"), the date, and how many days ahead to be reminded (default 7).
3. A client can have multiple life events (e.g. separate auto, home, and life policy renewal dates).
4. Once the reminder window is reached, a normal follow-up is created automatically — it shows up on the Follow-up queue, the Calendar, the Dashboard, and syncs to Google Calendar exactly like any other follow-up, since it's the same underlying record.
5. Each reminder is created once per year per event; deleting the generated follow-up doesn't stop future years' reminders, but deleting the life event itself does.

This is gated by a package feature ("Client life-event reminders") that Super Admin can enable on any package, same as other premium features.

