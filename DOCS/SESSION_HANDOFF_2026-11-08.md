# Session handoff -- 2026-11-07 and 2026-11-08

## Branches and state: read this first

- **Live (production):** portal and SuperAdmin on `14a0a0ed` since 7:59 PM (portal) and 8:02 PM (SuperAdmin) Eastern
  on 2026-11-08, on the owner's Run workflow and approval: 648, 618, 649, 611, 635, 650 and 652. Live builder
  (builder.iproadvisers.com) on `11bc328`, unchanged.
- **Rollback for tonight's release:** iPro `2e2b95d8`, by the owner's Run workflow and approval.
- **Staging:** iPro on this close-out's docs commit, which holds 651 (gate 2,568 of 2,568 at the first try); staging builder `11bc328`.
- **651 is on staging only**, waiting for the owner's look and word. `feat/651-client-pages` is fully in `main`.
  `feat/649-611-635` is fully in `main` and can be deleted.
- **New at start-up tonight (live):** `AdminUsers.SignInVersion` (648) and the indexes
  `IX_DripCampaignStepSends_DripCampaignStepId`, `IX_ClientLifeEvents_ClientId`, `IX_PortalDocuments_ClientId` (652).
  Neither live app's log shows a start-up warning.
- **The release hold is over:** the second meeting was cancelled (the owner is with family in hospital); releases
  happen on the owner's word as usual.

## What happened

| When (Eastern) | What | State |
|---|---|---|
| 11-07 | System check: the repeated 403s were a SuperAdmin Job Scheduler tab reloading once a minute on another computer of the owner's, closed by the owner (last reload 11:21 AM 11-08). 647 and 648 found | done |
| 11-08 morning | 648 (Sign out everywhere for SuperAdmin; a password reset ends the account's other sign-ins) and 618 (adviser sites draw the font their design names; Modern keeps Apple's own font on Apple devices, the owner's word) | gate 2,487 of 2,487 on `74ca9ec6`, staging |
| 11-08 | 647 corrected: Application Insights does keep requests (30 days); the 11-04 slowdown traced to a scanner from Andorra on www.lavenuebakery.com (about 950 requests in a minute, 474 database-built 404s); 649 opened for it | done |
| 11-08 | 649 (scanner paths answered "Not found." before any database work), 611 (staging opens a test adviser's site through its own preview), 635 (a second copy of the Data Protection keys in iPro's storage), 650 (every due reminder made whatever the client count), 652 (campaign pages and Email Activity count in the database) | first full run 2,563 of 2,564 on `791f7512`: M18 caught the key copy's container missing from the storage report; fixed in `101ade41`; gate **2,565 of 2,565** |
| 11-08 evening | 636 fixed with the owner at a full-size PC: both Microsoft Authenticator lines linked again, a phone number added as the directory's second way in | done |
| 11-08 evening | 590's read-only account check in Cloud Shell: `iproadmin` and `ipro_builder` are on `mysql_native_password` | the switch waits for a quiet hour |
| 7:32 PM | Staging on `14a0a0ed` | done |
| 7:59 and 8:02 PM | **Live** on the owner's release: 0 5xx, no start-up warnings, scanner paths "Not found." in 0.09-0.49 s, public pages and adviser sites normal; the owner saw both production keys in `data-protection-keys/ipro-prod-web/` | **live** |
| 11-08 evening | 651 (the client list in pages of 50) built on the owner's word, checked on the local copy with 60 throwaway clients (removed after) | gate 2,568 of 2,568 at the first try, on staging at 10:00 PM (portal) and 10:01 PM (SuperAdmin) as ae057b15 |

## Found, on the to-do list

- **651's note:** at phone width (a headless picture, 390 px) the Clients page runs past the right edge, with or
  without the pager, so older than 651: confirm on a real phone.
- **The SuperAdmin site answers a script 401:** App Service's Microsoft sign-in (the owner's setting of 2026-10-31),
  `/health` and `/health/version` left open. Normal; a browser is sent to sign in.
- **590:** before the switch, check that Cloud Shell's MariaDB client signs in to a `caching_sha2_password` account
  (against staging); then `ipro_builder` first, then `iproadmin`, the same passwords, typed by the owner.

## The owner's Azure sign-in (636), for next time

Two checks on two Authenticator lines: check 1 at login.live.com is the owner's personal Microsoft account (line "Microsoft",
8-digit codes); check 2 at login.microsoftonline.com is the directory (line "Default Directory", 6-digit codes). Tell
them apart by the address bar. The directory now lists two Authenticator entries on SM-S928W (one dead, left in place:
they look alike) and the owner's mobile number.

## Close-out 2026-11-08

| Code | What | Gate | State |
|---|---|---|---|
| `74ca9ec6` | 648, 618 | 2,487 of 2,487 | live as part of `14a0a0ed` |
| `101ade41` | 649, 611, 635, 650, 652 | 2,565 of 2,565 | live as part of `14a0a0ed` |
| `a3f49e4a` | 651 | 2,568 of 2,568 at the first try | **staging only**, waiting for the owner |
| `11bc328` | Builder | gated 10-30 | live, unchanged |

Backups of this commit (two snapshot zips, each opened and checked against git) and a recovery kit are in
`OneDrive\Codex_Code_Bkup` and `Documents\IPRO_Backups`. Azurite was already running and is left as it was; the
owner's builders on 5087 and 5088 are left running.

## Do this first next session

1. After a reboot neither local builder runs: the owner double-clicks the two Start-Builder cmd files.
2. Check the hosts (`/health/version`): portal and SuperAdmin `14a0a0ed` (a later docs commit shows on staging only),
   live builder `11bc328`.
3. 651: the owner's look on staging, then the owner's release (the client list in pages; deleting a test client there
   shows the shared Delete form at work).
4. 590: the client check against staging, then the two account switches at a quiet hour, well before 2026-12-31.
5. Scratchpad tools disappear after about a week: restore from the newest notes-and-tools zip before a gate or release.
