# Session handoff — 2026-09-22 (the day after launch)

## What happened

- **Morning check, 2 p.m. (the owner was in meetings):** both hosts on `4afcb2a`; the 7:05 follow-ups
  email went out (2 sends, one per demo account, by the email service's metrics); all six public names
  answer 200 over ordinary DNS. The email-metrics one-liner used the two mornings before had gone
  quiet: its JMESPath filter on `count` returned nothing even when there were sends -- read the raw
  JSON and filter in Python instead (the working form is in this session's log).
- **Search Console, done by the owner:** `ipromortgages.com` and `iproaccountants.com` added as their
  own properties (he chose Google's CNAME verification -- a labelled CNAME Google offers beside the
  TXT; checked from public DNS, nothing else in either zone moved: bare A records, `www`, MX intact).
  All three sitemaps read **Success**: advisers 3 pages, accountants 1, mortgages 1 -- 510 did what it
  was for; the "2 errors" rows are gone.
- **512, the Visitors report, built and shipped** (TODO 512 says what it records and where). The
  Privacy Policy: shown the before and after, the owner decided **no addition** ("it has been clearly
  described as why and how we already track and collect"). Settled. After the deploy one real visit
  from the assistant's own browser, tagged `utm_source=claude&utm_medium=check&utm_campaign=512`, was
  made to `www.iproaccountants.com` so the report's first row is a known one.
- **The owner's first real promotion code, `TAX50OFF`** (Platinum, 50% off for 4 cycles, monthly billing
  only, expires 1 November, for an accountants group from LinkedIn): checked from outside through the
  registration page's own code check -- accepted on Platinum monthly with the right sentence, refused
  on annual with the right sentence, refused on Gold. He was told the code carries no setup-fee discount,
  so a member signing up after 30 September pays Platinum's $400 unless he adds one.
- **The health check the owner asked for (3:30 p.m.), all read-only:** both hosts Healthy on `e46a5a9`;
  zero 5xx in 24 hours on either app (10,010 web requests, 1,904 admin); average response 0.12 s; the
  seven public pages 200 in 0.11-0.19 s; `http://` forwards; the six brand names' certificates 171-179
  days, **`app.` and `admin.` 26-27 days (renew before 5 October: TODO 505 or the hand renewal)**; DNS
  and both mail routes right; the 7:05 email both mornings delivered. The one number to explain:
  3,458 of the day's requests were 404 -- in bursts of a few hundred (scanners probing for files that
  do not exist; harmless, no error on our side) plus one per real visit for the missing `/favicon.ico`.
  Linux App Service keeps no per-request log, so the 404s cannot be listed by path; streaming request
  logs to a workspace is a setting change (owner's go) if he ever wants that. The application log had
  only the certificate watchdog's deliberate warning and the deploy's container start-up noise.
- **513, the favicon,** from that check: the owner supplied the icon; the platform's pages name it,
  advisers' sites are left as they were (TODO 513 says why).
- **Microsoft's closing note on the ACS quota case (2608310040012537):** routine, sent because the
  ticket was confirmed closed; the plan of record is to ask again in mid-October with a month of real
  sending history (the calendar item). Nothing to do now; the case number is worth quoting then.
- **514 and 515, the invoices redesigned, one deploy:** the marketing designer's package arrived in the
  morning ("if you like it we could implement it today"); rebuilt for our invoice to advisers and for
  advisers' invoices and estimates to their clients, with the supplier's details moved out of the
  settings into SuperAdmin -> **Company Details** at the owner's word ("can u not hardcode the elements
  needed i.e. GST/HST and addresses so we could populate it from superadmin"). TODO 514 and 515 say what
  is shown and what was deliberately not invented. **Owner:** fill Company Details after the deploy;
  until then invoices print the settings' name, email, website and one-line address, and no GST/HST
  number (it is in no setting). The invoice pages are behind sign-in, so the proof of the look is his.
- **516, the owner's first look at a real invoice (7 p.m.):** he had filled Company Details within the hour
  of the deploy and sent the invoice back with three asks -- the tax line listed as a charge, the city and
  the province on two lines, the PayPal reference broken mid-token -- fixed the same evening, with the
  "Inc.." seen beside them (TODO 516). One of his client invoices read "No tax": the client had no
  country or province on file, and the tax on a client document follows the client's province (DOCS/10);
  not a defect, he was told to complete the client and redo the document.
- **517, the Visitors report's day boundary (8:42 p.m.):** the owner's By-day list had started "Wed, Sep 23"
  while it was still Tuesday evening in Toronto -- the days were the server's UTC days. Now the platform's
  own days (`Admin:TimeZone`, Eastern when unset), the same clock the header shows. Polish item 9 (the
  follow-up list's and the dashboard's "today") is the same defect elsewhere and stays on the list.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `db3d6ef` | **512** SuperAdmin's Visitors report: visits to the platform's own pages and where they came from | 1141/1141 |
| `3d025d2` | **513** the platform's pages have an icon; advisers' sites left as they were | 1157/1157 |
| `2147d6a` | **514** our invoice on the designer's design; the supplier from SuperAdmin -> Company Details | 1184/1184 |
| `2147d6a` | **515** advisers' invoices and estimates on the same design, branded for the adviser (same commit as 514) | 1184/1184 |
| `2c790e2` | **516** the first real invoice's three fixes: the tax shown once, one city line, the PayPal reference in two rows | 1200/1200 |
| `cf9904f` | **517** the Visitors report's days are the platform's own days, not UTC | 1202/1202 |

## Close-out 2026-09-22

Final build on both hosts before this close-out: `81d000b`. Tree clean and pushed; nothing half-built and
nothing waiting for a deploy. The day's items are in the table above (512 with one real tagged visit as
its proof; 513 with the icon fetched from outside; 514 and 515 with the stylesheet and logo fetched from
outside, the pages themselves behind sign-in for the owner's eyes), each red first and behind a full gate.

**State left:** PayPal live; the three brand domains and the platform on the new site with self-renewing
certificates; all three sitemaps accepted by Search Console; two accounts in the database, both the
owner's; his first real code `TAX50OFF` active and unused; the Visitors report counting since 3:08 p.m.;
**Company Details empty until the owner fills it** (invoices print the settings' values meanwhile, and no
GST/HST number).

Backups of the pushed HEAD: `IPRO_Modern_backup_<stamp>.zip` in `C:\Users\admin\OneDrive\Codex_Code_Bkup`
and `C:\Users\admin\Documents\IPRO_Backups`. Build servers shut down; no local app, emulator or test
process running; MySQL is the Windows service and needs nothing. Reboot-ready.

## Do this first tomorrow

1. Both health endpoints, the Job Scheduler, Email Activity; PayPal's webhook event log for any real
   sign-up (each event **Success**); and now **Reports -> Visitors** -- the first real day of counts.
2. **The invoices after 516:** Company Details is filled and the owner's three screenshots are answered.
   Tomorrow: one look at a client invoice for a client WITH a province (the tax follows the client's
   province) and at the paid-invoice email's item list. (The Privacy Policy question is settled.)
3. **Calendar:** TODO 505 or the hand renewal before 5 October (certificates expire 19 October); clear
   `Email__TrackingSigningKeyPrevious` around 17 October; Microsoft quota mid-October; .NET 10 in October.
4. **Open:** TODO 504 (the polish list), 506 (an adviser's domain with CAA records); retention for the
   two page-view tables; the truth sweep's open items; Google Calendar sync reads a follow-up's date as
   UTC; a site-language option, French first; SOC 2 after launch.

Related: `DOCS/TODO.md` 512-517; `DOCS/SESSION_HANDOFF_2026-09-21.md`.
