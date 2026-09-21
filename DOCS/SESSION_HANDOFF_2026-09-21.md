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
- **507 live:** the setting rewritten at 9:35 a.m. on the owner's go (the advisers pair as `www.iproadvisers.com=/,iproadvisers.com=/`; the app back within a minute); proved from outside with full certificate checking: both names 200 with the home page, canonical and og:url `https://www.iproadvisers.com/` (also when the page is opened at app.), its 8 files 200, deep paths 301 to the platform with `Cache-Control: public, max-age=3600`, the accountants and mortgage names unchanged, the accountants page's links to the home page's sections now at www.iproadvisers.com.
- **Promotion codes (TODO 508):** the owner will send codes to prospects and wants "one month free or
  more". A code's cycle is whatever billing period the customer picks, so "100% off, 1 cycle" was a
  free YEAR on annual billing. Built the same morning: a code can be limited to monthly or to annual
  billing (its own table; enforced at checkout, said on the registration page, **Applies to** on
  SuperAdmin's form). A "months free" code is: Recurring 100% off, Duration = the months, one package
  per code, Applies to = Monthly billing only, plus Expires and Max Redemptions (both already existed).
  **Before he sends any:** he opens SuperAdmin -> Promotion Codes once (it reads the new table, which
  proves the table exists in production), then one live free-month sign-up together -- a $0 first
  cycle has never run on live PayPal: watch activation, the $0 invoice, the hourly reconcile moving
  the next billing date, and the Billing page's next charge (503 counts cycles by dates for this case).

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `05b2dbd` | **507** the home page under `www.iproadvisers.com` and the bare name; forwards carry a cache lifetime | 1014/1014 |
| _see log 508_ | **508** a promotion code can be limited to monthly or to annual billing | whole tree |

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
