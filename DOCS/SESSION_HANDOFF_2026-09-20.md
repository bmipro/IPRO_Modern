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
  **Final state, 4:40 p.m.:** all four names bound to ipro-prod-web; `App__AliasHosts` carries the six
  entries (the two mortgage names plus the four); the two `www` names moved at 2:45 and the two bare
  names at 4:30 (the owner chose not to wait out the old MX record's four hours: "I am not worried
  about the emails"); each of the four has its own managed certificate, valid to 20 March 2027 and
  self-renewing, proved from outside with full certificate checking: the accountants pair answers 200
  with the Accountants page and sends `/Account/Register` to the platform, the advisers pair answers
  301 to `https://app.iproadvisers.com/`, plain http goes to https on the same name. **Incoming mail
  was checked after the move:** the owner's test message (he was asked to send it from an outside
  mailbox) "arrived on both emails on iproadvisers"; the public MX, read on Google's and
  Cloudflare's resolvers, is `mail.iproadvisers.com` -> the old server; the
  app's own SPF, DKIM and DMARC records are untouched. The whole procedure, reproducible, with
  commands, scripts (`ops/domain-switch/`) and the day's timeline: **`DOCS/DOMAIN_SWITCH_RUNBOOK.md`**.
  Rollback is the old value in the DNS row (TTL 300 on all four, so minutes).
- **Seen while fixing that, for the product (TODO 506):** an adviser whose own domain publishes CAA
  records without DigiCert will hit the same wall, and today nothing tells them why.
- **The owner's three PayPal tidy-ups are done** (his clicks, seen on his screens): Boby Moore deleted
  with the financial records (133 rows, 15 tables, PayPal subscription cancelled; he had already
  refunded the $3.39 at PayPal), the three QA daily packages Inactive, and `PayPal__BaseUrl` on
  ipro-prod-admin now `https://api-m.paypal.com` (read back; the other PayPal settings unchanged by
  name and length). Two accounts remain, both his, both Active. The public page and SuperAdmin agree
  on setup fees: Silver $150; Gold and Platinum waived until 30 September.
- **The July certificate mystery is solved by the same finding** (`DOCS/20_CERTIFICATES.md`): `app.` and
  `admin.` can now move to certificates that renew themselves -- TODO 505, before 5 October.
- **SOC 2 (the owner asked):** I can do the gap assessment, the policies, the technical controls, the
  evidence and the system description; I cannot be the auditor or do the human controls. After
  launch, and first find out who is asking and which report they need.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `6fa6434` | **503** the Billing page names the next real charge when a limited promotion runs out | 1000/1000 |

## Close-out 2026-09-20

Final build on both hosts before this close-out: `c08e54c`. Tree clean and pushed; nothing half-built and
nothing waiting for a deploy. **Production is live for money and on all four public names.**

**State left for launch morning:** PayPal LIVE on both apps (`PayPal__IsSandbox=false`); 18 recurring
jobs; two accounts in the database, both the owner's, both comped Active on `OLD_DEMO`; all sandbox-era
codes Inactive; the certificate row on the Job Scheduler is red by design until TODO 505 or the hand
renewal (expiry 19 October).

Backups of the pushed HEAD: `IPRO_Modern_backup_<stamp>.zip` in `C:\Users\admin\OneDrive\Codex_Code_Bkup`
and `C:\Users\admin\Documents\IPRO_Backups`. Build servers shut down; no local app, emulator or test
process running; MySQL is the Windows service and needs nothing. Reboot-ready.

## Do this first tomorrow

1. **Launch morning (Monday 21 September), with the owner:** `/health/version` on both hosts; the Job
   Scheduler (18 recurring jobs; the certificate row is red until 505 or the hand renewal); Email
   Activity while the first sends go out; the 7:05 follow-ups email; PayPal's webhook event log after
   the first real sign-up (each event **Success**).
2. **All four public names once more from outside**, with full certificate checking
   (`DOCS/DOMAIN_SWITCH_RUNBOOK.md`, step 7; `ops/domain-switch/dns-check.sh` for the zones). By
   morning every cache has the new records.
3. **A limited-cycle promotion code is safe to publish** since 503 (the Billing page now names the
   real next charge). The owner starts fresh with new codes; a recurring discount must be restricted
   to one package.
4. **Calendar:** 505 or the hand renewal before 5 October (certificates expire 19 October); clear
   `Email__TrackingSigningKeyPrevious` around 17 October; Microsoft quota mid-October; .NET 10 in October.
5. **Open:** TODO 504 (the polish list); the truth sweep's open items (`DOCS/TRUTH_SWEEP_2026-09-18.md`);
   Google Calendar sync reads a follow-up's date as UTC (needs the owner's Google account); a
   site-language option, French first, before selling to Quebec advisers; SOC 2 after launch.

Related: `DOCS/TODO.md` 503-506; `DOCS/PAYPAL_LIVE_CUTOVER.md`; `DOCS/14_BACKUP_AND_RELEASE_CHECKLIST.md`;
`DOCS/DOMAIN_SWITCH_RUNBOOK.md`; `DOCS/DNS_ZONE_RUNBOOK.md`; `DOCS/20_CERTIFICATES.md`;
`DOCS/SESSION_HANDOFF_2026-09-19.md`.
