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
  Privacy Policy's new section is still only drafted (09-21 handoff): the owner was asked whether to
  include it in this deploy or hold it for his lawyer, and had not answered when 512 shipped.
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

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `db3d6ef` | **512** SuperAdmin's Visitors report: visits to the platform's own pages and where they came from | 1141/1141 |
| _see log 513_ | **513** the platform's pages have an icon; advisers' sites left as they were | whole tree |

## Do this first tomorrow

1. Both health endpoints, the Job Scheduler, Email Activity; PayPal's webhook event log for any real
   sign-up (each event **Success**); and now **Reports -> Visitors** -- the first real day of counts.
2. **The Privacy Policy section for 512** if the owner has answered (the text is in the 09-21 handoff;
   `_LegalPrivacy.cshtml`, after "From visitors to your public website"). The report is live without it.
3. **Calendar:** TODO 505 or the hand renewal before 5 October (certificates expire 19 October); clear
   `Email__TrackingSigningKeyPrevious` around 17 October; Microsoft quota mid-October; .NET 10 in October.
4. **Open:** TODO 504 (the polish list), 506 (an adviser's domain with CAA records); retention for the
   two page-view tables; the truth sweep's open items; Google Calendar sync reads a follow-up's date as
   UTC; a site-language option, French first; SOC 2 after launch.

Related: `DOCS/TODO.md` 512-513; `DOCS/SESSION_HANDOFF_2026-09-21.md`.
