# Session handoff — 2026-09-28

## What happened

- **No reboot happened.** The owner came back with the question that reset 527: he never wanted an account with
  Stripe; he wanted advisers to pick a service and enter their code or link so clients pay from the invoice.
  Yesterday's slice 1 (Stripe Connect, the platform-account model behind Squarespace's panel) was a
  misunderstanding on my side: the requirement was in the row but never put to him as the one question it was.
  Cost about a day, most of it kept (the Payments page, the payments table); the connection and the webhook
  receiver stay dormant behind the platform keys.
- **527 rewritten around links and built (527b):** the Payments page with a card per service -- PayPal.me name,
  Stripe Payment Link, Square link, Interac e-Transfer email (with a note for the client), any other link --
  each with where to get it; every invoice shows a Pay button per method (PayPal with the exact total; the
  Stripe link with the invoice number and the client's email; Square as created; Pay Now for another link)
  and the e-Transfer line. The Profile's single link is retired: gone from the Profile form, still the
  fallback until the page is saved once, pre-filled on the page the first time. Seen in the local preview
  on a sent invoice.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| _see log 527b_ | **527b** payment methods by link or code; a Pay button per method on the invoice | whole tree |

## Do this first tomorrow

1. Both health endpoints, the Job Scheduler (`certificate-expiry`, the follow-ups, the invoice reminders at
   9:00 a.m. Eastern), Email Activity, Reports -> Visitors.
2. **The owner's glance:** Client Invoices -> Payments (enter his own methods, then open a sent invoice's public
   link to see the buttons); the Profile's Client Invoicing block now points at that page.
3. **The new customer:** his first renewal is 25 October; PayPal's payment notice settles it on its own.
4. **Decide:** TODO 520 (bring-your-own-website package); 524 (dictation, with the owner's three conditions
   in its row); 528 and 529 (announcement bar, promotional pop-up) after the template builder.
5. **Open:** 506 (an adviser's domain with CAA records); retention for the two page-view tables; an adviser's
   own icon and logo; the client-invoice email's look; the comped plans' renewal date (8 July 2027); 519 when
   the owner returns to it; the Girard demo if he wants it; the client Details page's three-collection query
   (`AsSplitQuery`); a Sage file for the accountant's statement the day an adviser brings a real template;
   e-cards and e-letters share a different footer (`EmailUnsubscribeFooter`) that does not carry the line yet;
   the Squarespace template tour, paused after Common Tongue.
6. **The builder review:** the two local commits on `feature/builder-ux-refresh` stay unpushed; on the owner's
   go, pull one export ZIP and validate a real export with assets.
7. **Calendar:** clear `Email__TrackingSigningKeyPrevious` around 17 October; the two unbound Let's Encrypt
   certificate resources lapse 19 October (nothing to do); Microsoft quota mid-October; .NET 10 in October.

Related: `DOCS/TODO.md` 520, 524, 527, 528, 529; `DOCS/SESSION_HANDOFF_2026-09-27.md`.
