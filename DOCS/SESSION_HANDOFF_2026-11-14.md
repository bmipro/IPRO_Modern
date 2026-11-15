# Session handoff -- 2026-11-14

## Branches and state: read this first

- **Live (production):** the portal and SuperAdmin are on `3b036f66`. The live builder is on `11bc328`. Nothing
  has been released since 2026-11-09 evening. Rollback: iPro `809510ae`.
- **Staging:** this docs commit; no code after `3b036f66`.
- **Branches:** 63, all on GitHub-side history or in the recovery kit; no side worktrees. `fix/630-631` can be
  deleted on the owner's word. `claude/582-form-rate-limit-43d3bf` holds unfinished 582 work (TODO 640): keep it.
- **Production database:** MySQL 8.0.46, both accounts on `caching_sha2_password`. 590's rehearsal is still to
  do (`DOCS/MYSQL84_UPGRADE_RUNBOOK.md`), before 2026-12-31.

## What happened

| When | What | State |
|---|---|---|
| afternoon | The new PC (Dell OptiPlex 5080) arrived. Its admin accounts belonged to whoever prepared it, so the owner is resetting it and setting it up with the local account `admin`. | under way |
| afternoon | On the owner's word, Codex's work moves too. | packed |
| afternoon | The move folder was made in `OneDrive\Codex_Code_Bkup\move-20261114`: the two double-click files and the packs, tested by unpacking into a scratch folder on the old PC. Plan and steps: `DOCS/NEW_PC_MOVE.md`. | ready |
| evening | On the new PC: tools checked, the missing Visual C++ runtime installed (MySQL could not start without it), MySQL as service `IPROLocalMySQL` with `ipro_crm` loaded, both local settings files made, both solutions built, local portal and SuperAdmin checked, builders started and signed in to Azure and GitHub by the owner. | done |
| evening | **Full gate on the new PC: 2,587 of 2,587**, on `6ff679bb`, 79 minutes (the first half fast, the second half slow; the cause is settled two rows down). | **passed** |
| evening | The old sessions came over with the second edition of `3-Bring-Sessions` (the first stopped on a 262-character path under Temp). This conversation, "Project overview (fork)", now runs on the new PC. | done |
| evening | X: (`\\TSCLIENT\X`, there only while the owner is connected by Remote Desktop with drive X shared) and both builders checked: 5087 and 5088 answer, and 5088 lists the pictures. | done |
| evening | **The slow second half was the owner's activity, not the PC.** Part 4 again on a quiet PC: 6 minutes 43 seconds, against 36 minutes 21 seconds before. A quiet full gate here takes about 30 minutes; on the old PC it took about two hours. | settled |

## Close-out 2026-11-15

| Code | What | Gate | State |
|---|---|---|---|
| `3b036f66` | 630, 631 (only docs since) | 2,587 of 2,587 on 11-09 (old PC); again 2,587 of 2,587 on 11-14 on `6ff679bb` (new PC) | live |
| `11bc328` | Builder | gated 10-30 | live, unchanged (the staging builder shows `ed4f6a1`, the workflow-only change of TODO 654) |

Backups of this commit, made on the new PC, are in both `OneDrive\Codex_Code_Bkup` and `Documents\IPRO_Backups`:
the two snapshot zips, each opened and checked against git and one of them built, plus a recovery kit.

## Do this first next session

1. Work on the NEW PC (DEVBACK). After a restart neither local builder runs: the owner double-clicks the two
   `Start-Builder*.cmd` files. X: is there only while he is connected by Remote Desktop with drive X shared.
2. Check the hosts at `/health/version`: portal and SuperAdmin on `3b036f66` (a later docs commit shows on staging
   only), and the live builder on `11bc328`.
3. Ask which work he wants next (the order suggested in the 11-10 handoff: 590 part 2, the rehearsal, before
   2026-12-31; 646 once he sets the price). 645 is done apart from retiring the old PC.
4. Before a gate: the scratchpad tools are kept in `C:\Users\admin\iPro_tools\scripts` and in the newest
   notes-and-tools zip. A quiet gate takes about 30 minutes here; keep the PC quiet while it runs.
5. The old PC: retire it, and delete the OneDrive move folder `move-20261114`, on the owner's word only.
