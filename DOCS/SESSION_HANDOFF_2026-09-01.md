# Session handoff — 2026-09-01 (20 days to launch)

## What shipped

Four code items, each red-first with a full green gate, each verified at `/health/version` on
both hosts before its TODO row was ticked; one pipeline fix; and one production incident found,
resolved, and root-caused the same afternoon.

| commit | what | gate |
|---|---|---|
| `20b9cd0` | **440** freemail Reply-To replaced at the provider seam (both providers); **441** a send that fails before the recipient loop now fans the reason out to every queued recipient row (4 dispatchers) | 538/538 |
| `c3128d0` | **443** `CanonicalEmail` — Gmail dot/plus variants are one person for uniqueness, CSV import and the suppression write (agent-scoped) | 560/560 |
| `f431f5b` | **444** Email Activity says "not tracked" instead of a dash; `Email__EngagementTrackingEnabled` flag; runbook step | 567/567 |
| `f58c320` | **445 (1)** verify step gets a second-chance restart before failing, both workflows; pin observed red on old YAML | pin only (CI) |
| `04f884f` `8fb682c` | check-marks 440/441, 443 + 442 ticket-channel note | docs |
| _held locally_ | `8ceaddf` 444 tick + 445 logged; `6256a48` 445 root cause | docs — ride with the next real push |

## The incident, in one paragraph

A docs-only push (`8fb682c`, 15:53) took `app.iproadvisers.com` to HTTP 503 on every path from
15:55 to 16:12. Container logs show the app **aborting during startup with exit 134 five times**,
40–47 s in, then recovering on its own on attempt 7 — six minutes *before* the owner-authorised
`az webapp restart` at 16:18, which was therefore a coincidence. Cause, evidence-backed on the 445
row: the workflow's explicit post-deploy restart fired ~15 s into the deploy's *own* restart and
killed attempt 1 mid startup-DDL (`Program.cs` runs ~20 un-guarded `Ensure*SchemaAsync` ALTER/CREATE
calls before `app.Run()`); the dead session's metadata lock made every later start block until the
30 s command timeout threw, unhandled. The database was healthy throughout (metrics pulled). Admin
runs the same startup against the same DB and has never crashed — its explicit restart has been
failing silently on every deploy (it lives in `ipro-prod-admin_group`, not `ipro-production`), so it
only ever restarts once. **Zero exit-134s on any of the previous seven days.**

## Do this first tomorrow

1. **445 (5), the paging alert** — the only 445 item still open, and it's portal-side: Azure Monitor
   availability test on `https://app.iproadvisers.com/health`, action group that emails AND texts.
   Until it exists, a 503 is still only a GitHub email.
2. **Watch ticket `2608310040012537`** (ACS quota → 500/min, 10,000/hour + engagement tracking).
   Reply sent 09-01 with volume figures; evaluation up to 72 h → expect Wed–Thu. Until granted:
   **no test sends to addresses that cannot receive** — `test@iproadvisers.com` only. The ticket's
   contact mailbox is behind the cPanel filter that bounced Microsoft once; three sender domains
   are whitelisted and the portal thread is the reliable channel.
3. **Read the container logs after the first deploy of the day.** (Confirmed tonight: the capture works, but only Warning+ and stderr reach it in production -- a silent app section is a clean boot; a failed step will show as `[Startup:<step>] FAILED`.) `StartupGuard` now logs every
   startup step that fails (`[Startup:<step>] FAILED` on stderr → `*_default_docker.log`) and
   whether it held the advisory lock. A clean deploy should show the lock held and no failures;
   anything else is the first real evidence of what a repair does on the production schema.
   Post-launch, not now: fold the `StartupSchemaRepair` DDL into proper migrations and delete the
   repair class — the guard makes it safe, it does not make it right.

## 445 — where each fix stands at close

- **(2) conditional restart — SHIPPED `a466ffe`, verified both hosts.** The explicit post-deploy
  restart now polls `/health/version` for 90 s and fires only if the old build is still serving;
  admin addressed in its real RG (`ipro-prod-admin_group`), which also repaired the second-chance
  restart from `f58c320`. First live run: web saw the old build through checks 5–7 while the
  deploy's own restart finished, then *"no restart needed"* — the double restart is gone.
- **(3) capture stderr — DONE.** `--docker-container-logging filesystem` on both apps. The next
  exit-134 leaves a stack trace in `*_default_docker.log`.
- **(4) startup cannot take the site down — `StartupGuard`, in flight at close.** Red 5/6 →
  green 6/6 (incl. a real-MySQL advisory-lock test); 36 steps wrapped in Web, 35 in Admin;
  `MigrateAsync` deliberately unwrapped; timeouts armed after migrations. Full gate 579/579. **Shipped `1a7b29e`, verified both hosts (admin 18:01, app 18:04); both runs "no restart needed", verify confirmed on attempt 1 -- one clean StartupGuard boot per app.**
- **(5) paging alert — OPEN, owner.** Azure Monitor availability test on `/health` with an action
  group that emails AND texts. Nothing tells a human about a 503 today except a GitHub email.
- **(1) second-chance restart** — shipped `f58c320`; still useful, now rarely needed.

## TemplateBuilder review (developer repo, commit `5916022`)

Answered the developer's `SENIOR_DEVELOPER_REVIEW.md` in place: preview-only Planning Desk
confirmed (style key never reaches the export layer; variants differentiate by home-page
composition); durable structure verified concretely — every `layoutVariant` and Hero setting the
variants emit is promised in CONTRACT v0.3 **and** switched on by the host's three renderers;
next milestone re-ordered to integration readiness. **Poll is correctly absent** — the host's
`PollResults` binds at render time to a poll the agent has run; a template has nothing to carry.
One addition to the request, no contract change: **golden export fixtures** per theme and per
Planning Desk variant, byte-deterministic, with a re-export equality test — the artefact the host
importer will be written against. **The host importer does not exist yet**; it lands in
IPRO_Modern test-first, and its timing is the owner's call (standing pause, ONBOARDING §9).

## Owner-only items still outstanding

- 🔴 **PayPal live cutover** — production is still sandbox; every package must be re-synced.
- 🟠 Google Postmaster Tools — no longer urgent (439 resolved), still worth having before launch.
- 🟠 8 unverified audit candidates; unsubscribe link end-to-end; PayPal "Verified" badge; "Contact
  us for pricing" channel.
- ⚪ Azure Support Plan Standard is active (~$100 USD/month) — review after launch, not before.

## Known-open, honestly

- **Every web deploy today had a failed first start** (probe "failed" at 1.7 / 3.8 / 13 s, then a
  clean second start) — that is the double restart, visible in every run log. Until 445 (2) ships,
  each deploy costs ~90 s of 503 plus one wasted start.
- **`bmot1966@gmail.com` still receives nothing** — a one-mailbox curiosity, not a platform problem.
- **Local MySQL has 8 orphaned `ipro_test_*` databases** from today's aborted/filtered runs.
  Harmless; one `DROP DATABASE` sweep when not mid-gate.
- Opens/clicks stay "not tracked" until 442 is granted and the flag is flipped (runbook has steps).

## Method notes worth keeping

- **"Root cause: platform race" was withdrawn within the hour.** The first write-up inferred a
  platform restart race from two clean deploys either side of a bad one. The container log said
  otherwise. Before attributing an outage to the platform, read the container log for the window —
  it is one `az webapp log download` away and it named the exit code, the attempt count, the
  self-recovery time, and the double-restart signature.
- **Restart-and-hope is not a fix.** The manual restart "worked" because the lock had already been
  reaped; the timeline proved it. Fix (1) is still worth having, but it was shipped on a wrong
  theory and the row says so.

Related: `DOCS/TODO.md` items 431, 433, 439–445; `DOCS/DNS_ZONE_RUNBOOK.md`; `.github/workflows/*`.
