# Session handoff — 2026-09-25

## What happened

- **The Job Scheduler question.** The owner saw five failed `certificate-expiry` rows (the last "2 days
  ago") and no green. Answered: the Failed page is history and never clears itself; the five are the
  3:00 a.m. Eastern runs of 19-23 September, when the old certificate was inside 30 days; the last ran
  six hours before 505; the Recurring Jobs page shows the job green since ("11 hours ago" at 1:15 p.m.).
  Proved from outside: both hosts on the managed certificate to 2027-03-23, and no warning in the web
  app's log at the 07:00 UTC runs of the 24th and 25th. The five rows can be deleted from Jobs -> Failed
  (not from Recurring Jobs, where Delete removes the schedule itself).
- **521 built and deployed:** the watchdog's red row, email and doc rewritten for the managed world, and
  every platform name watched (the TODO row has the detail). Overnight (04:30 UTC) Azure moved the web
  app to a new worker; it came up fine, both hosts Healthy on `67c3bd1` before the deploy.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `9a1232d` | **521** the certificate watchdog's message, email and doc for managed certificates; every platform name watched | 1231/1231 |

## Do this first tomorrow

1. Both health endpoints, the Job Scheduler (`certificate-expiry` green at 3:00 a.m. Eastern, now over
   eight names; the five old failed rows deleted or not, the owner's choice), Email Activity,
   Reports -> Visitors.
2. **Decide:** TODO 520 (bring-your-own-website package) -- name, price, setup fee, whether the
   unpublished site stays; then the build order in the row.
3. **Open:** 506 (an adviser's domain with CAA records); retention for the two page-view tables; an
   adviser's own icon and logo; the client-invoice email's look; the comped plans' renewal date (8 July
   2027); 519 when the owner returns to it; the Girard demo if he wants it; the client Details page's
   three-collection query (`AsSplitQuery`, a one-line polish seen in the 09-24 health check).
4. **The builder review:** the two local commits on `feature/builder-ux-refresh` stay unpushed; on the
   owner's go, pull one export ZIP and validate a real export with assets.
5. **Calendar:** clear `Email__TrackingSigningKeyPrevious` around 17 October; the two unbound Let's
   Encrypt certificate resources lapse 19 October (nothing to do); Microsoft quota mid-October;
   .NET 10 in October.

Related: `DOCS/TODO.md` 505, 520, 521; `DOCS/20_CERTIFICATES.md`; `DOCS/SESSION_HANDOFF_2026-09-24.md`.
