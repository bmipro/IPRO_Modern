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

## Do this first next session

**On the NEW PC:** follow `DOCS/NEW_PC_MOVE.md`. Check `C:\Users\admin\iPro_tools\move-steps` to see whether
`2-Place-Files` has run, then work through "The new PC's Claude session" list. The old PC stays as it is until a
full gate passes on the new one.

**On the old PC (until then):** nothing new. Do not change or delete anything there.
