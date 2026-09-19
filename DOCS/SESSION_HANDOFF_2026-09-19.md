# Session handoff — 2026-09-19 (2 days to launch)

## What happened

- **The owner's test morning.** He asked to be guided through testing from his side, and did it:
  the first real run of the follow-ups email (498) at 7:05 -- two sent, two delivered (ACS metrics),
  one of them in MichaelTran's inbox with thirteen overdue items, the button opening the list, the
  switch on the profile, the job green on the Job Scheduler; the three Deactivate clicks on his legacy
  starter articles (verified from the live preview); a real Mortgage sign-up with a promotion code he
  made for the purpose (site live at once, the 499/500 menu, invoice arithmetic right: 27.00 + 13% =
  30.51); the lead path end to end (both forms, both leads, both notification emails); a Generic
  sign-up; password reset; a support ticket in both directions; and a call to the published number.
  I looked at the new mortgage site at phone width: the long Resources menu opens and scrolls, an
  article page reads well.
- **501, from that morning:** no starter calculators on a Generic site (his question, his yes); the
  home page's calculator sentence narrowed to stay true; the welcome email's `https://` link, its
  sign-in sentence, and its plain-text version, which was still the legacy "CONGRATULATIONS!" letter.
- **The red row on the Job Scheduler is the certificate check doing its job:** the two platform
  certificates (app, admin) expire on **19 October 2026**; today is day 30, the first day it turns
  red, and it will be red every morning until they are renewed (`DOCS/20_CERTIFICATES.md`, by hand,
  on this machine). Not a launch problem. Do it in the first quiet week after launch, before 5 October.
- **PayPal live cutover moved to Sunday morning** (owner). The runbook is `DOCS/PAYPAL_LIVE_CUTOVER.md`,
  written from the billing code: clear out the sandbox-era accounts WHILE STILL IN SANDBOX, the live
  app and its six webhook events, four settings on both apps, Sync PayPal plans on three packages, a
  comped code for the two kept demo accounts (the owner's own, MichaelTran), one real-money check.
  The DNS switch for the four public names is independent; all four still point at the old site.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `bcf557a` | **501** no Generic starter calculators; the welcome email's link, sign-in sentence and text version | 968/968 |

## Do this first tomorrow

1. **7:05 a.m.:** did bobmoore (created 19 September, one follow-up dated the 20th) get the morning email?
   That is the brand-new-account case. ACS metrics answer it read-only if the owner is not at his inbox.
2. **PayPal live cutover**, `DOCS/PAYPAL_LIVE_CUTOVER.md`, top to bottom. No deploys while it runs.
3. **DNS switch for the four names** (`DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md`, "Launch-day domain
   switch"), the same Sunday, right after the cutover (owner, 19 September: "we will try to do
   everything tomorrow"), so that Monday is for watching. Rollback is the old records.
4. **Launch morning (Monday 21 September):** both health endpoints, the Job Scheduler, Email Activity,
   with the owner, while the first sends go out.
5. **Calendar:** certificates before 5 October (expire 19 October); clear
   `Email__TrackingSigningKeyPrevious` around 17 October; Microsoft quota mid-October; .NET 10 in October.
6. **Still open from the truth sweep** (`DOCS/TRUTH_SWEEP_2026-09-18.md`): portal wording by business
   type; preview template vs real template; the Accountants library's three stale lines; exports for a
   lapsed account; Google Calendar sync reads a follow-up's date as UTC (with the owner's Google
   account); the follow-up list's "today" is the server's date. And the owner's other five legacy
   starter articles: no Category, and not read against the library's rules.

Related: `DOCS/TODO.md` 501-502; `DOCS/SESSION_HANDOFF_2026-09-18.md`; `DOCS/PAYPAL_LIVE_CUTOVER.md`.
