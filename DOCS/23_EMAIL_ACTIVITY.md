# Email Activity

## What It Is

One screen that lists every email your account has sent to clients and whether it arrived: newsletters, drip campaign steps, e-cards, e-letters, polls, Did You Know emails, and invoice emails. Click any row to see it recipient by recipient.

## Open It

Select **Email Activity** in the Agent Portal menu (under Marketing). The list opens on the **Everything** tab showing the last 30 days, newest first, 25 emails to a page.

## Read the List

Use the tabs to narrow the list: **Everything, Needs attention, Newsletters, Campaigns, E-Cards, E-Letters, Polls, Did You Know, Invoices**. Each tab shows how many rows it holds for the period on the screen.

Each row shows:

- **Type**, with its icon.
- **Subject**, with a grey second line: the audience for a newsletter or poll, the occasion for an e-card, the campaign name and step number for a drip step, the invoice number and client for an invoice email.
- **Status**.
- **Recipients / Sent / Delivered / Opened / Failed** counts.
- **When**, in your own time zone.
- A **Detail** button.

## Finding an Email

- **Show: 7 days, 30 days, 90 days, 12 months, All.** The list opens on the last 30 days so it stays short. Older email is never removed: pick a longer period to see it. An email that is scheduled for later, or not yet scheduled, shows in every period.
- **Needs attention** holds only the emails in that period with a failure or a bounce. Its number turns red when there is something to look at, so a problem is not buried under newer mail.
- **Search** looks at each email's subject, at the grey line under it, and at the name and email address of everyone it went to. Type a client's name to see what they were sent, for example whether their birthday card went out. It searches within the period and the tab on the screen; **Search all time** widens it, and **Clear the search** removes it.
- **Previous** and **Next** under the list move between pages of 25. The line above the list says which rows you are looking at.
- After you open a row's **Detail**, **Back to Email Activity** returns you to the same period, tab, search and page.

## Read One Send in Detail

1. Click **Detail** on a row.
2. The five tiles at the top show **Recipients, Delivered, Opened, Clicked, Failed**, each with a percentage. A recipient who reported the email as spam is not counted as Failed: the email arrived.
3. Below is one line per recipient: name, email, **Status** (the furthest point reached: Sent, Delivered, Opened, Clicked, or Failed), the time of each step, and **Issue**, which carries the mail provider's reason when something failed or bounced. A recipient who marked the email as spam reads **Reported spam**: the email arrived, and they are now unsubscribed from all your marketing email.
4. **Back to Email Activity** returns to the list.

E-Cards and E-Letters also link straight into this detail page from their own list pages.

## Where the Numbers Come From

- **Sent** is our side: the mail provider accepted the message.
- **Delivered** comes from the mail provider's delivery reports, matched back to each recipient. **Opened** and **Clicked** are recorded by the platform itself since 2026-09-14 (488): a one-pixel image loads when the message is opened, and each link passes through a short redirect on app.iproadvisers.com that notes the click before sending the reader on. They usually appear within a minute or two of the action, so a row you open immediately can still be filling in.
- **Opened** depends on the recipient's mail app loading images, so real opens are always at least as high as the number shown. Images loaded in the first minute after sending are not counted as an open: some mail systems (Gmail, for a picture-heavy email such as a card) load them the moment the email arrives, before anyone has looked, so counting them would report emails as opened that were never read.
- If open and click tracking is switched off (`Email__PlatformTrackingEnabled=false`, and no provider tracking either), Opened and Clicked read **not tracked** and the detail page says so. Invoice emails carry no open or click counting: the invoice page records each time the client views the invoice, which says more. Since 2026-10-11 the screen shows that instead of "not tracked": the list reads **invoice viewed** or **not viewed yet** (a view counts for an email when it happened after that email went out), and the detail page has an **Invoice viewed** tile with the time and a link to the invoice. Delivered is unaffected. Sends made before tracking was switched on stay blank.

## What Is Not Here

- There is no date filter, search box or export on this screen today; the tabs are the only filter.
- Only mail you send to clients is listed. System mail (billing receipts, trial reminders, password resets) is not.
- Nothing can be resent or deleted from here. Resend an invoice from the invoice itself; resend a newsletter from Newsletters.
- **Did You Know** rows are grouped per article, so one row can cover many staggered emails.
- Invoice emails are one row each: an original, a resend and an overdue reminder are three rows.

## Package Availability

Email Activity is available on every package, deliberately, so you can always see what left your account, even for a feature your current package no longer includes.
