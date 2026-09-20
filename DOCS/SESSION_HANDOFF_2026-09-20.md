# Session handoff — 2026-09-20 (go-live day; launch is tomorrow)

## What happened

- **7:05 a.m.: the follow-ups email reached all three accounts that had something due** (3 sent, 3
  delivered by the ACS metrics), including bobmoore, the account created the day before -- the
  brand-new-account case.
- **PayPal is LIVE.** The whole cutover ran stage by stage with the owner, his clicks throughout; the
  record (the live app, the webhook id, the six plan ids, the two comped demo accounts on `OLD_DEMO`,
  the $3.39 real-money test on `LIVECHECK`, its refund) is at the top of `DOCS/PAYPAL_LIVE_CUTOVER.md`.
  The owner keeps his own account and MichaelTran forever, for demos and testing.
- **The real-money test found 503:** after paying $3.39 on a one-cycle code, the Billing page read
  "Next billing: October 20 - $3.00" for a subscription PayPal would bill $60.00 plus tax. Fixed the
  same day (`NextCharge`; TODO 503). The owner was told to publish no limited-cycle code until it is live.
- **The four public names.** Three traps the rehearsal could not show, all written into
  `DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md` ("Three traps...") and `DOCS/DNS_ZONE_RUNBOOK.md`:
  mail followed the bare name (MX and `mail` pinned to the old server first, both zones); the zones'
  CAA records did not allow DigiCert, so the first two certificate orders never issued and the two
  `www` names showed a certificate warning for about fifty minutes until the owner added
  `0 issue "digicert.com"` (my miss: I did not check CAA before asking him to switch); and the legacy
  host's nameservers hand out old and new answers side by side for a while after every save.
  **State at this writing:** all four names bound to ipro-prod-web; `App__AliasHosts` carries the six
  entries (the two mortgage names plus the four); `www.iproadvisers.com` (301 to the platform) and
  `www.iproaccountants.com` (the Accountants page under its own name) are switched, with managed
  certificates valid to 20 March 2027, proved from outside with full certificate checking. **The two
  bare names move in the evening** (the owner's two A records to `40.89.19.0`, TTL 300, then their
  certificates), some hours after the MX change so that no mail arrives late. Until then the old
  server answers them (it redirects bare `iproaccountants.com` to `www`, which is already the new site).
- **The July certificate mystery is solved by the same finding** (`DOCS/20_CERTIFICATES.md`): `app.` and
  `admin.` can now move to certificates that renew themselves -- TODO 505, before 5 October.
- **SOC 2 (the owner asked):** I can do the gap assessment, the policies, the technical controls, the
  evidence and the system description; I cannot be the auditor or do the human controls. After
  launch, and first find out who is asking and which report they need.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `6fa6434` | **503** the Billing page names the next real charge when a limited promotion runs out | 1000/1000 |

## Do this first tomorrow

1. **Launch morning (Monday 21 September), with the owner:** `/health/version` on both hosts; the Job
   Scheduler (18 recurring jobs; the certificate row is red until 505 or the hand renewal); Email
   Activity while the first sends go out; the 7:05 follow-ups email; PayPal's webhook event log after
   the first real sign-up (each event **Success**).
2. **All four public names from outside**, with full certificate checking (`DOCS/14`, step 5), and one
   email from a Gmail address to support@iproadvisers.com to prove incoming mail after the bare names
   moved.
3. **The owner's three PayPal tidy-ups** if still open: delete Boby Moore with the financial tick; untick
   "Package is active" on the three QA daily packages; `PayPal__BaseUrl` on ipro-prod-admin.
4. **Calendar:** 505 or the hand renewal before 5 October (certificates expire 19 October); clear
   `Email__TrackingSigningKeyPrevious` around 17 October; Microsoft quota mid-October; .NET 10 in October.
5. **Open:** TODO 504 (the polish list); the truth sweep's open items (`DOCS/TRUTH_SWEEP_2026-09-18.md`);
   Google Calendar sync reads a follow-up's date as UTC (needs the owner's Google account); a
   site-language option, French first, before selling to Quebec advisers; SOC 2 after launch.

Related: `DOCS/TODO.md` 503-505; `DOCS/PAYPAL_LIVE_CUTOVER.md`; `DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md`;
`DOCS/DNS_ZONE_RUNBOOK.md`; `DOCS/20_CERTIFICATES.md`; `DOCS/SESSION_HANDOFF_2026-09-19.md`.
