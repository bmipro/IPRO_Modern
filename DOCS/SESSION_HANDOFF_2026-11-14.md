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
| evening | **Full gate on the new PC: 2,587 of 2,587**, on `6ff679bb`, 79 minutes (the first half fast, the second half slow, cause still open). | **passed** |

## Do this first next session

**On the NEW PC:** the work continues here. Read "Where it stands" in `DOCS/NEW_PC_MOVE.md`. Open: why the second
half of the gate was slow (re-run one part on a quiet PC); the optional `3-Bring-Sessions` step if the owner has
not run it yet.

**On the old PC:** nothing changed. It can be retired on the owner's word, and the OneDrive move folder deleted
on his word.
