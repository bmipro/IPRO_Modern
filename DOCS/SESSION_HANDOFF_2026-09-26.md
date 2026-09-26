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
- **Two product questions answered and decided** while the 522 gate ran: invoicing analytics,
  adviser-controlled reminders and an accountant's statement go first (TODO 523, four slices, ~8-10
  days); voice dictation is parked as TODO 524 with the owner's three conditions (stated in the privacy
  policy, a switch to turn it off, a package feature so packages can include or exclude it).

## Pushed today

| Code | Item | Gate |
|---|---|---|
| _see log 522_ | **522** the SuperAdmin agent page: the package's name, and a sign-up counts as a login | whole tree |

## Do this first tomorrow

1. Both health endpoints, the Job Scheduler (`certificate-expiry` green at 3:00 a.m. Eastern over eight
   names), Email Activity, Reports -> Visitors.
2. **The new customer:** his first renewal is 25 October; PayPal's PAYMENT.SALE.COMPLETED notice settles
   it on its own (the path that appended the sale id on the 25th). Nothing to do until then.
3. **523, slice 1:** the "at a glance" strip on the invoices page and the dashboard (outstanding,
   overdue with count, paid this month against last month, average days to pay), red first, its own
   gate; then slices 2-4 in the row's order. 524 (dictation) waits for the owner's word.
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
