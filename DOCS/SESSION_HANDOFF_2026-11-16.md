# Session handoff -- 2026-11-16

## Branches and state: read this first

- **Live (production):** the portal and SuperAdmin are on `3b036f66`. The live builder is on `11bc328`. Nothing
  has been released since 2026-11-09 evening. Rollback: iPro `809510ae`.
- **Staging:** this docs commit; no code after `3b036f66`.
- **Branches:** 63, unchanged. Codex added copies of the old PC's branches under `refs/hp-backup/20261115/*` in
  this repository and in the builder's; they are not branches and are never pushed. Leave them.
- **Work happens on the new PC (DEVBACK).** The old PC can be reset from my side; it is the owner's to reset.

## What happened

| What | Result |
|---|---|
| Codex restored its own files that were missing on the new PC, and reported in `OneDrive\Codex_Code_Bkup\Dell-restore-verification.txt`. | its side |
| **Final old-PC check for my side.** Everything was compared file by file against Codex's snapshot of the old PC (`HP-final-supplement-20261115-105114`, which holds the old PC's whole `.claude`) and my move packs: notes, 26 of 27 conversations whole, every branch and tag of both repositories, the Documents files, the builder files and both builder databases. Only on the old PC, and not needed: the last 8 minutes of one conversation (all acted on), past one-time reminders, and Claude's internal history files. | **passed** |
| Full gate on the new PC, after Codex's changes: **2,587 of 2,587** on `590a6067`, in 97 minutes. Builder tests: 375 of 375, and 37 of 37 for its browser code. | **passed** |
| Codex had put a fixed copy of the X: pictures first in both builders' picture settings. On the owner's word it is now last, so X: comes first again and the copy is used only when X: is not connected. Codex's version is kept in `C:\Users\admin\iPro_tools\builder-settings-before-20261116`. | done |
| The gate's slow second half (TODO 655): not the owner's activity (the 11-14 guess, now corrected), and not Malwarebytes. The owner added an exclusion for `C:\Users\admin\ipro-local\mysql-data`; database rounds took 3.0 s before and after. The cause is open. | open |

## Do this first next session

1. Check the hosts at `/health/version`: portal and SuperAdmin on `3b036f66`, the live builder on `11bc328`.
2. Keep the OneDrive folders `HP-final-supplement-20261115-105114` and `move-20261114` until the owner says
   otherwise.
3. TODO 655 when there is time: a 10-minute run of `iPro_tools\scripts\db_bench.sh` rounds to see whether they
   slow down over time. Then, on the owner's word, try MySQL settings (that needs a restart of his MySQL
   service).
4. Then the order suggested in the 11-10 handoff: 590 part 2 (the rehearsal) before 2026-12-31; 646 once the owner
   sets the price.
