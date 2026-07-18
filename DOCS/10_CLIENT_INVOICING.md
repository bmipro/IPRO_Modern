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

- A read-only, printable page showing the agent's business details, the client's bill-to information, line items, tax, and total.
- For an **estimate**: **Approve** and **Decline** buttons. Approving does not charge anything — it just flags the estimate as approved.
- For an **invoice**: a **Pay Now** button, shown only if the agent has set a payment link on their Profile page (see below). The client can also arrange payment another way (cheque, e-transfer, cash) directly with the agent.

IPRO does not process the payment itself — the agent always confirms and records payment manually (see **Mark Paid** below).

## Converting an Approved Estimate to an Invoice

1. Once a client approves an estimate, open it from **Client Invoices**.
2. Click **Convert to Invoice**.
3. The document becomes an invoice with a new `INV-####` number and a Draft status, ready to send.

## Marking a Document Paid

1. Open the invoice from **Client Invoices**.
2. Click **Mark Paid** and choose how it was paid (Online, Cheque, Cash, EFT, or Other).
3. The invoice status updates to Paid and the payment date/method is recorded.

## Setting a Payment Link

1. Select **Profile** in the Agent Portal.
2. Under **Client Invoicing**, enter a payment link (for example a PayPal.me link or a Stripe payment link).
3. Save. This link appears as **Pay Now** on every invoice sent afterward — no need to reissue past invoices when it changes.

## Recurring Invoices

1. Select **Client Invoices**, then **Recurring Schedules**.
2. Click **New Schedule**, choose a client, frequency (Monthly/Quarterly/Annually), the next run date, and line items.
3. Each time the schedule runs, a new **Draft** invoice is created automatically — it is never sent to the client automatically. Review it and click **Send to Client** yourself.
4. Use **Pause**/**Resume** to temporarily stop or restart a schedule, or **Delete** to remove it.

## Exporting

Click **Export CSV** on the Client Invoices list to download every document matching the current filters (document number, type, status, client, dates, and totals).
