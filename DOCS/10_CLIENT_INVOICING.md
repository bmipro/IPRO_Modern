# Client Invoicing: Estimates, Invoices, and Recurring Billing

Client Invoicing lets an agent bill their own clients directly from IPRO — separate from the agent's own IPRO subscription billing. It is a Platinum/Broker package feature by default; Super Admin can change which packages include it under **Packages**.

## Create an Estimate or Invoice

1. Select **Client Invoices** in the Agent Portal.
2. Click **New Estimate** or **New Invoice**.
3. Choose the client, issue date, and (for invoices) due date.
4. Add one or more line items with a description, quantity, and unit price.
5. Add optional notes — these are shown to the client.
6. Click **Save Draft**.

Tax is calculated automatically from the client's own province/country (not the agent's), using the same Canadian provincial tax table Super Admin manages under **Tax Rates**. US and other non-Canadian clients are not taxed.

## Send a Document to a Client

1. Open the draft from **Client Invoices**.
2. Click **Send to Client**.
3. The client receives an email with a link to view the document — no account or login required.

A document number (`EST-####` for estimates, `INV-####` for invoices) is assigned once and does not change, even if the document is later edited while still a draft.

## What the Client Sees

- A read-only, printable page in the agent's branding: their company name, address, phone and email, and their website logo when they have one; the client's bill-to information; each line with quantity, unit price, tax and amount; the tax summary, subtotal, tax and total; for an invoice, the due date, payment received and balance due, and once paid, the date and method. The status reads in words (Awaiting payment, Awaiting your reply, Paid, Approved, Declined, Void).
- For an **estimate**: **Approve** and **Decline** buttons. Approving does not charge anything — it just flags the estimate as approved.
- For an **invoice**: a **Pay Now** button, shown only if the agent has set a payment link on their Profile page (see below). The client can also arrange payment another way (cheque, e-transfer, cash) directly with the agent.

IPRO does not process the payment itself — the agent always confirms and records payment manually (see **Mark Paid** below).

## Knowing Whether the Client Received It

Every email an invoice generates is tracked (added 2026-09-02):

- **Send** mails the document first and only then marks it **Sent**. If the mail provider refuses the address, the document stays a draft, the Send button stays, and the banner tells you why.
- The invoice page lists each email under the public link: sent, **delivered**, **bounced** (with the reason), or **could not be sent**. Delivery events come from the mail provider and usually arrive within a minute.
- **Viewed by client** appears the moment the client opens the invoice link, with the time and how many times. Your own preview does not count.
- The invoice list has a **Delivery** column: Viewed, Delivered, Sent, Bounced, Send failed, or Not sent yet.
- **Resend email** on a sent document sends it again, for a bounce or a changed address. Resending a paid invoice sends a copy and does not reopen it.
- **Email Activity** has an **Invoices** tab listing every invoice email and reminder.

A hard bounce automatically stops all email to that client until you resubscribe them from their profile, the same as every other channel.

## Converting an Approved Estimate to an Invoice

1. Once a client approves an estimate, open it from **Client Invoices**.
2. Click **Convert to Invoice**.
3. The document becomes an invoice with a new `INV-####` number and a Draft status, ready to send.

## Marking a Document Paid

1. Open the invoice from **Client Invoices**.
2. Click **Mark Paid** and choose how it was paid (Online, Cheque, Cash, EFT, or Other).
3. The invoice status updates to Paid and the payment date/method is recorded.

## Your Money at a Glance, and Who Owes What

The **Client Invoices** page opens with four cards: what is outstanding, what is overdue (with a link to
exactly those invoices), what was paid this month against last month, and the average number of days
your clients take to pay. The dashboard shows the same four numbers when your package includes
invoicing. Only invoices count as money: estimates, drafts and void documents are left out, and a
payment counts in the month it was received in your own time zone.

**Who owes what** (a button on the Client Invoices page) lists every unpaid invoice by client and by
how long it is past due: current, 1-30, 31-60, 61-90 and over 90 days. Each overdue invoice has a
**Send reminder** button, which sends the same reminder email the schedule below sends, records it on
the invoice, and will not send a second one within a day. The status filter on the invoices list also
has **Overdue**.

## Invoice Reminders You Control

**Invoice reminders** (a button on the Client Invoices page) is the schedule your clients are reminded
on. Six stages, each a switch: a set number of days before the due date, on the due date, the day
after, then 7, 14 and 30 days overdue. Until you change anything, nothing goes before or on the due
date and every overdue stage is on. Three wordings (before, on the day, overdue) can be yours; leave a
box empty for the stock words, and use `{invoice}`, `{amount}`, `{due}`, `{days}`, `{client}` and
`{company}` where you want them filled in. The page previews each email as a sample invoice would
read it.

Reminders go out each morning (9:00 a.m. Eastern), only for invoices that are sent and unpaid. Each
stage is sent once per invoice, no two reminders go within five days of each other (your own Send
reminder button counts), and after 30 days the automatic reminders stop; "Who owes what" keeps the
button for anything older. Every reminder links to the invoice and is recorded on it, so **Knowing
Whether the Client Received It** covers reminders too.

## Setting a Payment Link

1. Select **Profile** in the Agent Portal.
2. Under **Client Invoicing**, enter a payment link (for example a PayPal.me link or a Stripe payment link).
3. Save. This link appears as **Pay Now** on every invoice sent afterward — no need to reissue past invoices when it changes.

If the link is a PayPal.me link, IPRO automatically appends the invoice's exact total and currency to the URL (PayPal.me's own supported format, e.g. `paypal.me/yourname/113.00CAD`), so the amount is pre-filled for the client and they don't have to type it in themselves. Other payment links (Stripe, etc.) open as-is, since there's no equivalent standard for passing an amount in the URL.

## Recurring Invoices

1. Select **Client Invoices**, then **Recurring Schedules**.
2. Click **New Schedule**, choose a client, frequency (Monthly/Quarterly/Annually), the next run date, and line items.
3. Each time the schedule runs, a new **Draft** invoice is created automatically — it is never sent to the client automatically. Review it and click **Send to Client** yourself.
4. Use **Pause**/**Resume** to temporarily stop or restart a schedule, or **Delete** to remove it.

## For Your Accountant

**For your accountant** (a button on the Client Invoices page) is a statement for one month or one
quarter: what you invoiced (by invoice date), the tax you collected by rate, what came in (by the day
you recorded each payment), and what was still owed on the period's last day, all in your own time
zone. Drafts, void documents and estimates are not included. Print it or save it as a PDF, download
the rows as a CSV, or download the period's invoices in Xero's sales-invoice import layout (one row
per line item; check the tax type and the account code on your first import, Xero's wizard lets you
map them). Your accountant decides which basis your return uses.

## Exporting

Click **Export CSV** on the Client Invoices list to download every document matching the current filters (document number, type, status, client, dates, and totals).

### Exporting to QuickBooks

Click **Export for QuickBooks** on the Client Invoices list to download a CSV formatted for QuickBooks Online's own **Import Invoices** wizard (in QuickBooks: **Settings → Import Data → Invoices**). A few things to know:

- Only **invoices** are included — estimates aren't a QuickBooks concept, so they're left out.
- The file has one row per line item, with the invoice number, customer, dates, and terms repeated on each row — this is the exact shape QuickBooks' import wizard expects.
- Paid/unpaid status is included in the file for your own reference, but importing it does **not** mark anything as paid inside QuickBooks. After importing, reconcile payments there the same way you normally would.
