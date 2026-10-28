# Session handoff -- 2026-10-21

## Branches and state: read this first

- `main` = production. Live build at close: **c14a2b2** (594), on both hosts, verified at /health/version.
  Local `main` is one docs commit ahead (the 594 check-mark) plus this file: held, not pushed (a push restarts
  both sites). Push them with the next code release.

| Item | Branch (worktree under `.claude/worktrees/`) | Head | State | Waiting for |
|---|---|---|---|---|
| 585 builder Beta for customers | `claude/builder-beta-for-customers-2ebe5a`; builder repo `ipro/customer-beta` aa9c802 | f55d20e (code 6abf9b3) | gated 2,190 on 6abf9b3 (main d6716c2 merged in); builder 402 .NET + 57 front-end; all 9 walk-through findings confirmed by him | **his Azure set-up tomorrow morning** (runbook Parts A-C; step A4 now recommends MySQL 8.4 for staging), then main merged again, then staging |
| 593 two-step sign-in | `claude/musing-hodgkin-64d1ee` | ebc6340 (code fb6d9cb) | his four "yes" answers built; gated 2,140 of 2,140; review clean | **his 15-minute try** (steps: `593-try-it.md` in the shared notes), then release on his word |
| 595 step-by-step "Connect your domain" guide | `claude/nervous-banach-ebe68f` | uncommitted work (32 files) | built; targeted tests and the independent UX/security review were running at close | its session finishes (review fixes, full gate, commit); then his test with www.drhug.ca and www.ouritems.ca on bahmanmotamed |

The shared notes folder moved: `C:\Users\admin\AppData\Local\Claude-Data\claude_<id>\side-session-notes\`.

## What went live today (each on his word, the code exactly the gated tree)

| Item | What | Build | Gate |
|---|---|---|---|
| 592 | Nobody can take over a domain that left iPro: a TXT `_ipro-verify` proof (on the short address) before anything connects or is served; removals asked of Azure until confirmed. Before: his three read-only checks in Cloud Shell, and www.411trades.com removed from michaeltran | 65c4161 (admin 11:30, web 11:33) | 2,114 |
| 594 | My Website's Remove-domain box says plainly what to type (label, greyed example, plain warning, button waits for a match) | c14a2b2 (web 18:30, admin 18:33) | 2,120 |

Both releases: customers' domains (4ipro, lavenuebakery, fortressconsys) kept serving; no errors after restarts.

## Domains today

- **www.maison1850.com** (Lavenue Bakery's new domain): he connected it at the shop; both names bound, padlocks
  issued as the first `managed--` certificates (591's naming), the short name forwards to www. Working.
- **www.411trades.com** is iPro's own: re-added to michaeltran; **his TXT `_ipro-verify` record at GoDaddy is still
  to add** (Name box: just `_ipro-verify`; value from My Website).
- **www.drhug.ca and www.ouritems.ca** (iPro's own, were bound with nobody's site): bindings removed on his word;
  their three old certificates stay to expire (my certificate deletions are blocked; he chose to leave them).
- The setup screen confused him at the bakery (registrar steps folded when an adviser already has a domain,
  "Check now" rate limit). 595 is the answer.

## Builder Beta re-check (585)

Morning: fixes 1-5 confirmed by hand in Edge; four new findings (6 local logo blocked by the dev image policy,
7 a PNG named .jpg refused in the builder and iPro, 8 a stale review sentence, 9 the Versions panel sliding
sideways). Evening: 6, 7 and 9 confirmed fixed by him (8 seen by me). Bench stopped.

## Prepared today (read-only)

- Azure set-up for 585: all names free (ipro-builder, ipro-builder-staging, ipro-staging-web, ipro-staging-admin,
  iprostoragestaging, ipro-mysql-staging; group ipro-builder-staging absent); Canada East offers B2 Linux, MySQL
  B1ms and 8.4; iproadvisers.com CAA includes digicert.com; builder.iproadvisers.com not yet in DNS.
- 590 (MySQL 8.4 upgrade before 2026-12-31): facts added to the TODO row (in-place upgrade; a Burstable server is
  moved to General Purpose for the upgrade); proposal: staging MySQL on 8.4 from the start.

## Still his

- 592's after-release SELECT (`ops/sql/domain-ownership-after-release.sql`, Cloud Shell, read-only).
- The GoDaddy TXT record for www.411trades.com.
- Sales tax: his calls to Revenu Quebec, BC, Manitoba and Saskatchewan (or an accountant), then scope the change.
- Empty the Recycle Bin (2.2 GB of old snapshots).

## Do this first next session

1. His builders (5087, 5088): ask him to double-click Start-Builder.cmd and Start-Builder-For-Handovers.cmd if
   they are down (started from my tools they die).
2. **The Azure set-up for 585**, guided in the portal (runbook Parts A-C, his clicks and secrets); then merge main
   into the Beta again, gate, first staging deploy on his word.
3. **593 try** (15 min, Edge, his phone), then release.
4. 595 when its session reports: his end-to-end test with drhug.ca / ouritems.ca.
5. 592's after-release SELECT; the 411trades TXT.
