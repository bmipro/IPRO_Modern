# Session handoff — 2026-09-21 (launch day)

## What happened

- **Launch morning, 8:35 a.m.:** both hosts healthy on `01b9062`; all four public names and the
  mortgage pair answer from the new site over ordinary public DNS with full certificate checking; both
  bare names resolve to `40.89.19.0` and both MX records to `mail.<domain>` on Google's resolver. The
  owner reported the 7 a.m. follow-ups emails himself: "13 follow-ups" for MichaelTran and "4 overdue"
  for his own account, the same counts as the day before.
- **507, asked the evening before:** typing `www.iproadvisers.com` turned into `app.iproadvisers.com`
  and "it should stay as www.iproadvisers.com". His answers in the morning: the bare name stays too;
  `www.iproadvisers.com` is the address Google should treat as the main one. Built as `name=/` in
  `App:AliasHosts` (TODO 507). The forwards had no cache lifetime, so browsers that visited since
  Sunday afternoon may keep landing on app. for a while: check in a private window.
- **Promotion codes (next, TODO 508):** the owner will send codes to prospects and wants "one month
  free or more". A code's cycle is whatever billing period the customer picks, so "100% off, 1
  cycle" is a free YEAR on annual billing; he was told to publish no free-month code until a code can
  be limited to monthly billing. Any number of months works (the Duration box); Expires and Max
  Redemptions already exist. After it ships: one live free-month sign-up together before he sends any
  (a $0 first cycle has never run on live PayPal).

## Pushed today

| Code | Item | Gate |
|---|---|---|
| _see log 507_ | **507** the home page under `www.iproadvisers.com` and the bare name; forwards carry a cache lifetime | whole tree |

## Do this first tomorrow

1. Both health endpoints, the Job Scheduler, Email Activity, PayPal's webhook event log for any real
   sign-up (each event **Success**).
2. **Calendar:** TODO 505 or the hand renewal before 5 October (certificates expire 19 October); clear
   `Email__TrackingSigningKeyPrevious` around 17 October; Microsoft quota mid-October; .NET 10 in October.
3. **Open:** TODO 504 (the polish list), 506 (an adviser's domain with CAA records); the truth sweep's
   open items; Google Calendar sync reads a follow-up's date as UTC; a site-language option, French
   first; SOC 2 after launch; whether the vertical pages, Terms and Privacy should also live under
   `www.iproadvisers.com` (507 moved the home page only).

Related: `DOCS/TODO.md` 507-508; `DOCS/SESSION_HANDOFF_2026-09-20.md`; `DOCS/DOMAIN_SWITCH_RUNBOOK.md`.
