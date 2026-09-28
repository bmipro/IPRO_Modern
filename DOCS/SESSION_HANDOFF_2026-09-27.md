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

- **TODO 527 written, on the owner's word to start:** advisers collect payment from their clients through
  their own processor (Stripe first, then PayPal, then Square), from a Squarespace "Payment Processors"
  panel the owner showed. The Squarespace template tour (the public demos, one by one) was paused after
  the first template, Common Tongue.

- **527 slice 1 built and deployed (Stripe Connect):** the two tables, the Payments page with its three
  connect cards (Stripe live; PayPal awaiting the partner approval; Square next week, the owner's word),
  the Stripe connection itself (Stripe's own sign-in and consent; the account id is all we keep, iPro's
  key acts for the account), the disconnect, and the webhook receiver that settles an invoice once
  whatever Stripe resends. Nothing charges yet: the Pay button and the receipt are slice 2, which needs
  the owner's Stripe account first (item 4 below, and the TODO 527 row).

- **TODO 528 and 529 written, for after the template builder (the owner's word):** an announcement bar
  across the top of every page of an adviser's site, and a promotional pop-up when a visitor opens it
  (newsletter sign-up through the lead path we have, or buttons; layouts; display and timing), from two
  Squarespace panels he showed. The rows carry the shape, the package question and the estimates.

- **Close-out in the evening, a reboot coming:** everything is pushed and on both hosts (`5a79fdc`, then this
  handoff); backups of the pushed HEAD in OneDrive and Documents; build servers down; MySQL is a Windows
  service and needs nothing. After the reboot: start MySQL before any gate (`DOCS/16_LOCAL_DEV.md`), and
  `ops\Start-LocalEnv.ps1` only when a local preview is wanted. The owner's last words of the day were the
  Stripe-account question in item 4 below -- start there.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `b9fc44f` | **525** "SSL included", "Powered by iPro" on sites and documents, "Sent with iPro" on the emails | 1269/1269 |
| `3526329` | **526** the client portal and the newsletter footer carry the line too | 1271/1271 |
| `1b90e49` | **527a** Stripe: the tables, the Payments page, the connection, the webhook receiver | 1280/1280 |

## Do this first tomorrow

1. Both health endpoints, the Job Scheduler (`certificate-expiry`, the follow-ups, the invoice reminders at
   9:00 a.m. Eastern), Email Activity, Reports -> Visitors.
2. **The owner's glance:** the SSL line under SuperAdmin -> Packages (tick or untick per package, move it up
   into the landing cards if wanted); the footer of any adviser site; the foot of a client invoice.
3. **The new customer:** his first renewal is 25 October; PayPal's payment notice settles it on its own.
4. **527, talk first:** at the end of the day the owner asked why iPro needs a Stripe account at all ("I dont
   understand why we have to have stripe account"). The short answer for that talk: the adviser's clients pay
   the adviser's own Stripe account, and that does not change; but for our software to open a checkout for
   an invoice and hear back that it was paid, Stripe requires the software maker to be registered as a
   "platform" (Connect). The platform account is iPro's identity toward Stripe: it holds the keys the app
   uses, the client id behind the adviser's Connect Stripe button, and the webhook Stripe calls. It holds no
   money (each charge lands directly in the adviser's account), has no monthly fee (Stripe's per-transaction
   fee is charged to the adviser), and Squarespace's own Connect Stripe button is the same arrangement. The
   only way round it is each adviser pasting a secret key of their own into iPro and registering our webhook
   in their dashboard by hand -- the security shape TODO 527 rules out. PayPal's "partner" application is
   the same idea. Opening the account is about an hour of business details and verification on his side.
   **If he agrees, his steps before slice 2:** open iPro's Stripe account and turn on Connect; register the redirect
   URI `https://app.iproadvisers.com/Payments/StripeCallback` and the Connect webhook endpoint
   `https://app.iproadvisers.com/payments/stripe/webhook` (event `checkout.session.completed`); paste the
   test keys into the local `appsettings.Development.json` (`DOCS/16_LOCAL_DEV.md` has the block) and, on
   his go, the live keys as App Service settings `Stripe__SecretKey`, `Stripe__ClientId`,
   `Stripe__WebhookSecret` (verified by name and length, never shown); send the PayPal partner application.
   Then the Pay button and the receipt get built.
5. **Decide:** TODO 520 (bring-your-own-website package); 524 (dictation, with the owner's three
   conditions in its row).
6. **Open:** 506 (an adviser's domain with CAA records); retention for the two page-view tables; an
   adviser's own icon and logo; the client-invoice email's look; the comped plans' renewal date (8 July
   2027); 519 when the owner returns to it; the Girard demo if he wants it; the client Details page's
   three-collection query (`AsSplitQuery`); a Sage file for the accountant's statement the day an adviser
   brings a real template; e-cards and e-letters share a different footer (`EmailUnsubscribeFooter`) that
   does not carry the line yet.
7. **The builder review:** the two local commits on `feature/builder-ux-refresh` stay unpushed; on the
   owner's go, pull one export ZIP and validate a real export with assets.
8. **Calendar:** clear `Email__TrackingSigningKeyPrevious` around 17 October; the two unbound Let's
   Encrypt certificate resources lapse 19 October (nothing to do); Microsoft quota mid-October;
   .NET 10 in October.

Related: `DOCS/TODO.md` 520, 523, 524, 525; `DOCS/SESSION_HANDOFF_2026-09-26.md`.
