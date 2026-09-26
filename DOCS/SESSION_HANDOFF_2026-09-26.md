# Session handoff — 2026-09-26

## What happened

- **The first real customer signed up on 2026-09-25 at 6:24 p.m.** (Saeed Masoudian, Fortress Connect
  Systems, promotion code SAEED: Platinum at Gold's price, $60.00 + 13% HST = $67.80 a month; PayPal
  subscription I-C47DAJ87T6RM; invoice IPRO-2026-000029 paid). Checked from every side without signing
  in: the amounts agree at PayPal, on the invoice and on the subscription; the next payment is 25 October
  on both sides; PayPal's payment notice reached `/billing/webhook`, passed the signature check and
  appended the sale id to the invoice -- the production webhook's first proof with a real customer;
  three emails in the hour, all delivered; the site answers; no warning in the web app's log. The promo
  plan has a single price tier at PayPal, so the price holds for the life of the subscription.
- **522 built and deployed:** the two cosmetic defects that check found on the SuperAdmin agent page --
  "Package 3" instead of the name, and "Last Login: Never" for a customer who had been in the portal.
- The owner deleted the five old failed certificate rows on the Job Scheduler (Failed: 0); the
  `certificate-expiry` job ran green at 3:00 a.m. over the eight names (521).
- **523 slice 1 built and deployed:** the money at a glance on the invoices page and the dashboard
  (outstanding, overdue, paid this month against last month, average days to pay; the TODO row has
  the detail). Not looked at in a browser: both pages sit behind the adviser's sign-in.
- **523 slice 2 built and deployed:** "Who owes what", the unpaid invoices by client and by how long
  they are past due, with a one-click reminder per overdue invoice (the nightly job's email, shared),
  and an Overdue filter on the invoices list. Not looked at in a browser: behind the adviser's sign-in.
- **523 slice 3 built and deployed:** "Invoice reminders", the schedule the adviser controls (six
  stages, three wordings, a preview), with the daily job as its engine in the adviser's own day and
  the job moved to 9:00 a.m. Eastern. Two new small tables (settings, stages sent), created at
  start-up in both apps. Not looked at in a browser: behind the adviser's sign-in.
- **523 slice 4 built and deployed, and 523 is done:** "For your accountant", a statement for a month or
  a quarter (invoiced, tax by rate, received, owed at the end), printable, with a CSV and Xero's import
  file. No Sage file: neither Sage template was to hand to check against (the TODO row says so).
  Not looked at in a browser: behind the adviser's sign-in.
- **Two product questions answered and decided** while the 522 gate ran: invoicing analytics,
  adviser-controlled reminders and an accountant's statement go first (TODO 523, four slices, ~8-10
  days); voice dictation is parked as TODO 524 with the owner's three conditions (stated in the privacy
  policy, a switch to turn it off, a package feature so packages can include or exclude it).

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `e7fff2e` | **522** the SuperAdmin agent page: the package's name, and a sign-up counts as a login | 1233/1233 |
| `80a981e` | **523 slice 1** the money at a glance on the invoices page and the dashboard | 1240/1240 |
| `1dea9a4` | **523 slice 2** who owes what, with a one-click reminder; the Overdue filter | 1248/1248 |
| `b70bf63` | **523 slice 3** the reminder schedule the adviser controls, with the daily job as its engine | 1258/1258 |
| `b81d173` | **523 slice 4** the statement for the accountant, with a CSV and Xero's import file | 1265/1265 |

## Close-out 2026-09-26

Final build on both hosts before this close-out: `33cdf92`. Tree clean and pushed; nothing half-built and
nothing waiting for a deploy. The day's items: 522 (the SuperAdmin agent page, after the first real
customer's sign-up was checked from every side) and 523 in four slices, each red first and behind its
own full gate (1240, 1248, 1258, 1265): the money at a glance; "Who owes what" with the one-click
reminder and the Overdue filter; the reminder schedule the adviser controls with the daily job as its
engine (now 9:00 a.m. Eastern; two new tables, created at start-up in both apps, the logs clean); and
"For your accountant" with a CSV and Xero's import file. 524 (dictation) is parked with the owner's
three conditions; 520 waits for his decision.

**State left:** the first real customer (Saeed Masoudian, Platinum at Gold's price under SAEED) active,
his first renewal 25 October; the reminder job's first run under the new rules Sunday 9:00 a.m.
Eastern; the certificate watchdog green over eight names; the five old failed rows deleted by the
owner; both apps on managed certificates to March 2027.

Backups of the pushed HEAD: `IPRO_Modern_backup_<stamp>.zip` in `C:\Users\admin\OneDrive\Codex_Code_Bkup`
and `C:\Users\admin\Documents\IPRO_Backups`. Build servers shut down; no local app, emulator or test
process running; MySQL is the Windows service and needs nothing. Reboot-ready.

## Do this first tomorrow

1. Both health endpoints, the Job Scheduler (`certificate-expiry` green at 3:00 a.m. Eastern over eight
   names), Email Activity, Reports -> Visitors.
2. **The new customer:** his first renewal is 25 October; PayPal's PAYMENT.SALE.COMPLETED notice settles
   it on its own (the path that appended the sale id on the 25th). Nothing to do until then.
3. **523 is complete.** The owner should glance, signed in, at Dashboard, Client Invoices, "Who owes
   what", "Invoice reminders" and "For your accountant" for the look, since none of the five could be
   seen from outside; the reminder job's first run under the new rules is Sunday 9:00 a.m. Eastern.
   524 (dictation) waits for the owner's word, with his three conditions in its row.
4. **Decide:** TODO 520 (bring-your-own-website package) -- name, price, setup fee, whether the
   unpublished site stays; then the build order in the row.
5. **Open:** 506 (an adviser's domain with CAA records); retention for the two page-view tables; an
   adviser's own icon and logo; the client-invoice email's look; the comped plans' renewal date (8 July
   2027); 519 when the owner returns to it; the Girard demo if he wants it; the client Details page's
   three-collection query (`AsSplitQuery`, a one-line polish seen in the 09-24 health check).
6. **The builder review:** the two local commits on `feature/builder-ux-refresh` stay unpushed; on the
   owner's go, pull one export ZIP and validate a real export with assets.
7. **Calendar:** clear `Email__TrackingSigningKeyPrevious` around 17 October; the two unbound Let's
   Encrypt certificate resources lapse 19 October (nothing to do); Microsoft quota mid-October;
   .NET 10 in October.

Related: `DOCS/TODO.md` 518, 521, 522, 523, 524; `DOCS/SESSION_HANDOFF_2026-09-25.md`.
