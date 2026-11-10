# Session handoff -- 2026-11-10

## Branches and state: read this first

- **Live (production):** the portal and SuperAdmin are on `3b036f66` (630 and 631, released 2026-11-09 evening);
  read at `/health/version` 2026-11-10. The live builder (builder.iproadvisers.com) is on `11bc328`. Nothing was
  released today.
- **Rollback:** iPro `809510ae`, by the owner's Run workflow and approval.
- **Staging:** iPro is on this close-out's docs commit (no code after `3b036f66`). The staging builder is on `11bc328`.
- **Production database:** unchanged since 2026-11-09. MySQL 8.0.46 on Standard_B1ms; both iPro accounts on
  `caching_sha2_password`.
- **Branches:** all 63 iPro branches are kept. There are no side worktrees any more (see below).
  - `fix/630-631` is fully in `main` and can be deleted on the owner's word.
  - `claude/582-form-rate-limit-43d3bf` holds one code commit of 582 that is not in `main` (TODO 640 builds
    from 582's items), so keep it.
  - Nothing is parked, and there is no `hold/` branch.

## What happened

| When (Eastern) | What | State |
|---|---|---|
| day | Disk space on this PC (the HP, 256 GB): drive C went from about 20 GB free to **60 GB**, on the owner's yes to items A and B. Nothing was lost. Details are in TODO 645's "Disk space 2026-11-10" note. Every branch was compared before and after (`git for-each-ref`). | done |
| day | Items C and D were offered and not done: the older OneDrive backup sets, and his July conversation files, made online-only (about 25 GB). He will decide at the better computer. | waiting |
| day | The to-do list was read with him to plan the work. He will revisit it at the better computer. | waiting |
| day | Two stale to-do titles corrected: 585 is in `main` and live since 2026-10-28; 653's check ran in production and restarted nothing. | done |

**The disk clean-up in detail.** Item A:
- the 22 old iPro side-session worktrees under `.claude\worktrees` (`git worktree remove`; each had no changes);
- the 614 test's browser profiles in the old scratchpad;
- `Projects\bb` and `Projects\bu`, which were worktrees of `Projects\ipro-builder`, so all 16 of their branch tips
  are still there;
- `Projects\bb-bench`, which held only built output;
- loose Temp files from before November.

Item B: the `Documents\IPRO_Backups` sets from before 11-08 were deleted. Each file was deleted only after the
same file, at the same size, was found in `OneDrive\Codex_Code_Bkup`. Six files from 10-12 to 10-20 have no
OneDrive copy, so they stay. The owner's two builders (5087 and 5088, run from `Projects\ipro-builder-site`)
were not touched and answered 200 afterwards.

**The order suggested for the next sessions** (his to choose):
1. 590 part 2, the rehearsal. About an hour, about 15 minutes of his. It does not depend on which PC is used.
2. 645, the move to the new PC.
3. 646, keeping your own website, as soon as he sets the price.

Waiting on his decision: 615 (which 2-3 pilot advisers), 633 (how long to keep the data), 643 (where the
working notes live), and 647 (recommended: leave the request logs off and close it).

## Close-out 2026-11-10

| Code | What | Gate | State |
|---|---|---|---|
| `3b036f66` | 630, 631 (no code since) | 2,587 of 2,587 on 11-09 | live |
| `11bc328` | Builder | gated 10-30 | live, unchanged |

Backups of this commit are in both `OneDrive\Codex_Code_Bkup` and `Documents\IPRO_Backups`: the two snapshot
zips, each opened and checked against git, plus a recovery kit.

## Do this first next session

1. After a reboot, neither local builder runs: the owner double-clicks the two Start-Builder cmd files.
2. Check the hosts at `/health/version`: portal and SuperAdmin on `3b036f66` (a later docs commit shows on staging
   only), and the live builder on `11bc328`.
3. Ask which of the suggested order he wants. For 590, follow `DOCS/MYSQL84_UPGRADE_RUNBOOK.md` from "Part 2".
4. Before a gate or release, restore the scratchpad tools from the newest notes-and-tools zip, because they
   disappear after about a week. The worktree lists in old scripts may name side worktrees that are gone now.
