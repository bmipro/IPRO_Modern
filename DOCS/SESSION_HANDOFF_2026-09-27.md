# Session handoff — 2026-09-27

## What happened

- **Health check, all clear** (the owner back after the reboot): both hosts Healthy on the close-out build;
  every public page 200; certificates 166-177 days; sitemaps, robots and all three DNS zones right; zero
  server errors on either app in 24 hours; one email (the 7:05 follow-ups), delivered; the web log empty,
  the admin log only its benign https-port line. One thing to know: during the slice-4 deploy on the 26th
  the admin container missed its 230-second start-up probe once, Azure restarted it, and the second start
  took 35 seconds -- the only such case in two days of restarts (30-50 s each). If it recurs, a longer
  start-up limit is an App Service setting, the owner's change. The Job Scheduler (his) showed the
  certificate job, the follow-ups and the invoice reminders' first 9:00 a.m. run all green.
- **525 built and deployed:** "SSL included" as a package catalogue line; "Powered by iPro" under every
  adviser site and at the foot of every client document, linked to the brand for the adviser's business;
  "Sent with iPro" on the document emails (the TODO row has the detail). Seen in the local preview on a
  document and a site.

- **526 built and deployed:** the two spots 525 left out -- "Powered by iPro" under every client-portal
  page and "Sent with iPro" in the newsletter footer (drip emails share it) -- through the same
  `PoweredBy` seam, after the owner asked that none of it be hard-coded for the day white-labelling
  comes: one class, and a switch there closes every spot at once.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `b9fc44f` | **525** "SSL included", "Powered by iPro" on sites and documents, "Sent with iPro" on the emails | 1269/1269 |
| _see log 526_ | **526** the client portal and the newsletter footer carry the line too | whole tree |

## Do this first tomorrow

1. Both health endpoints, the Job Scheduler (`certificate-expiry`, the follow-ups, the invoice reminders at
   9:00 a.m. Eastern), Email Activity, Reports -> Visitors.
2. **The owner's glance:** the SSL line under SuperAdmin -> Packages (tick or untick per package, move it up
   into the landing cards if wanted); the footer of any adviser site; the foot of a client invoice.
3. **The new customer:** his first renewal is 25 October; PayPal's payment notice settles it on its own.
4. **Decide:** TODO 520 (bring-your-own-website package); 524 (dictation, with the owner's three
   conditions in its row).
5. **Open:** 506 (an adviser's domain with CAA records); retention for the two page-view tables; an
   adviser's own icon and logo; the client-invoice email's look; the comped plans' renewal date (8 July
   2027); 519 when the owner returns to it; the Girard demo if he wants it; the client Details page's
   three-collection query (`AsSplitQuery`); a Sage file for the accountant's statement the day an adviser
   brings a real template; e-cards and e-letters share a different footer (`EmailUnsubscribeFooter`) that
   does not carry the line yet.
6. **The builder review:** the two local commits on `feature/builder-ux-refresh` stay unpushed; on the
   owner's go, pull one export ZIP and validate a real export with assets.
7. **Calendar:** clear `Email__TrackingSigningKeyPrevious` around 17 October; the two unbound Let's
   Encrypt certificate resources lapse 19 October (nothing to do); Microsoft quota mid-October;
   .NET 10 in October.

Related: `DOCS/TODO.md` 520, 523, 524, 525; `DOCS/SESSION_HANDOFF_2026-09-26.md`.
