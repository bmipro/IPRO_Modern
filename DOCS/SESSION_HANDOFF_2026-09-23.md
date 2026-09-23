# Session handoff — 2026-09-23

## What happened

- **Nothing was waiting from the day before:** production on the evening's close-out (`0c54dfb`), tree
  clean. The owner's first words ("lets do the 505 and 504, is there anything else that has not been
  deployed?") were answered with the open list from the 09-22 handoff.
- **505, the certificates, 8:51-8:57 a.m., on the owner's go:** `app.` and `admin.iproadvisers.com` moved
  from the hand-renewed Let's Encrypt certificates to App Service managed ones -- one order per host,
  issued in about four minutes each, bound SNI, proved from outside (DigiCert, expiry 2027-03-23, both
  answering 200). The hand renewal is retired; the watchdog's four failed entries on the Job Scheduler
  stop at its next run and can be deleted. TODO 505 and `DOCS/20` carry the detail.
- **518, nine of the polish list (504) and the print layout:** the items are struck in TODO 504's row
  with what was done; the print fix came from a printed client invoice of the owner's that spilled a
  four-line invoice onto a second page ("allowing at least 6 to 8 items"). Items 10 and 11 on the list
  were the owner's own settings, and he closed both the same afternoon: the admin app's PayPal base URL
  was already live, and the customer-service email PayPal shows buyers is now Billing@iProAdvisers.com
  (PayPal's Contact information page). TODO 504, the launch-weekend polish list, is closed.

## Pushed today

| Code | Item | Gate |
|---|---|---|
| `0bd4e57` | **518** nine polish items (TODO 504) and the invoices' print layout | 1227/1227 |

## Close-out 2026-09-23

Final build on both hosts before this close-out: `a8fc2b2`. Tree clean and pushed; nothing half-built and
nothing waiting for a deploy. The day's items: 505 (the certificates, proved from outside) and 518 (nine
polish items and the print layout, red first and behind a full gate).

**State left:** PayPal live; all six public names and the platform's two hosts on certificates that renew
themselves; Company Details filled; the owner's two accounts comped Active on `OLD_DEMO`; `TAX50OFF`
active; the Visitors report counting in the platform's own days.

Backups of the pushed HEAD: `IPRO_Modern_backup_<stamp>.zip` in `C:\Users\admin\OneDrive\Codex_Code_Bkup`
and `C:\Users\admin\Documents\IPRO_Backups`. Build servers shut down; no local app, emulator or test
process running; MySQL is the Windows service and needs nothing. Reboot-ready.

Later the same afternoon, docs only: TODO 504 closed (items 10 and 11 done by the owner in Azure and PayPal),
pushed on top of this close-out with fresh backups of that HEAD.

## Do this first tomorrow

1. Both health endpoints, the Job Scheduler (the certificate job passes from now on; its four old failed
   entries may be deleted), Email Activity, Reports -> Visitors.
2. **Open:** 506 (an adviser's domain with CAA records); retention for the two page-view tables; an adviser's own icon and logo; the client-invoice
   email's look; what the billing job does with a comped plan's renewal date (8 July 2027) -- worth a
   look well before then.
3. **Calendar:** clear `Email__TrackingSigningKeyPrevious` around 17 October; Microsoft quota mid-October;
   .NET 10 in October. The certificate renewal is no longer a calendar item.

Related: `DOCS/TODO.md` 504, 505, 518; `DOCS/SESSION_HANDOFF_2026-09-22.md`.
